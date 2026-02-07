using Colossal.Collections;
using Colossal.Logging;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.Buildings;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RadiusDelete
{
    [System.Flags]
    public enum DeleteFilters
    {
        None = 0, Networks = 1, Buildings = 2, Trees = 4, Plants = 8, Props = 16, All = 31
    }

    public partial class RadiusDeleteTool : BulldozeToolSystem
    {
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Game.Objects.SearchSystem m_ObjectSearchSystem;
        private Game.Net.SearchSystem m_NetSearchSystem;
        private ToolRaycastSystem m_ToolRaycastSystem;

        private ComponentLookup<PrefabRef> m_PrefabRef;
        private ComponentLookup<BuildingData> m_BuildingData;
        private ComponentLookup<TreeData> m_TreeData;
        private ComponentLookup<ObjectData> m_ObjectData;
        private ComponentLookup<PlantData> m_PlantData;
        private ComponentLookup<Owner> m_Owner;
        private ComponentLookup<Game.Objects.Transform> m_Transform;
        private ComponentLookup<Game.Net.Edge> m_Edge;
        private ComponentLookup<Game.Net.Node> m_Node;
        private BufferLookup<ConnectedEdge> m_ConnectedEdges;
        private ComponentLookup<Building> m_Building;
        private ComponentLookup<Temp> m_Temp;
        private BufferLookup<Game.Objects.SubObject> m_SubObjects;

        public float Radius = 20f;
        public DeleteFilters ActiveFilters = DeleteFilters.All;

        public override string toolID => "Bulldoze Tool";

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_ObjectSearchSystem = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            m_ToolRaycastSystem = World.GetOrCreateSystemManaged<ToolRaycastSystem>();

            m_PrefabRef = SystemAPI.GetComponentLookup<PrefabRef>(true);
            m_BuildingData = SystemAPI.GetComponentLookup<BuildingData>(true);
            m_TreeData = SystemAPI.GetComponentLookup<TreeData>(true);
            m_ObjectData = SystemAPI.GetComponentLookup<ObjectData>(true);
            m_PlantData = SystemAPI.GetComponentLookup<PlantData>(true);
            m_Owner = SystemAPI.GetComponentLookup<Owner>(true);
            m_Transform = SystemAPI.GetComponentLookup<Game.Objects.Transform>(true);
            m_Edge = SystemAPI.GetComponentLookup<Game.Net.Edge>(true);
            m_Node = SystemAPI.GetComponentLookup<Game.Net.Node>(true);
            m_ConnectedEdges = SystemAPI.GetBufferLookup<ConnectedEdge>(true);
            m_Building = SystemAPI.GetComponentLookup<Building>(true);
            m_Temp = SystemAPI.GetComponentLookup<Temp>(true);
            m_SubObjects = SystemAPI.GetBufferLookup<Game.Objects.SubObject>(true);
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain | TypeMask.StaticObjects | TypeMask.Net;
            // Expanded mask to ensure we don't miss sunken roads
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground | CollisionMask.Underground;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            m_PrefabRef.Update(this);
            m_BuildingData.Update(this);
            m_TreeData.Update(this);
            m_ObjectData.Update(this);
            m_PlantData.Update(this);
            m_Owner.Update(this);
            m_Transform.Update(this);
            m_Edge.Update(this);
            m_Node.Update(this);
            m_ConnectedEdges.Update(this);
            m_Building.Update(this);
            m_Temp.Update(this);
            m_SubObjects.Update(this);

            bool hitFound = GetRaycastResult(out Entity e, out RaycastHit hit);
            
            // Visualization
            if (hitFound && !hit.m_HitPosition.Equals(float3.zero))
            {
                RadiusDeleteVisualizationJob vizJob = new RadiusDeleteVisualizationJob()
                {
                    m_OverlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle outJobHandle),
                    m_Position = hit.m_HitPosition,
                    m_Radius = Radius,
                    m_Color = new UnityEngine.Color(1f, 0f, 0f, 0.5f)
                };
                inputDeps = vizJob.Schedule(JobHandle.CombineDependencies(inputDeps, outJobHandle));
                m_OverlayRenderSystem.AddBufferWriter(inputDeps);

                // Input Check
                if (applyAction.WasPressedThisFrame())
                {
                    RadiusDeleteMod.Log.Info($"[Input] Button Pressed! Raycast Hit at {hit.m_HitPosition}. Entity: {e.Index}");
                    inputDeps.Complete();
                    DeleteInRadius(hit.m_HitPosition, Radius, ActiveFilters);
                }
            }

            return inputDeps;
        }

        private void DeleteInRadius(float3 center, float radius, DeleteFilters filters)
        {
            var objTree = m_ObjectSearchSystem.GetStaticSearchTree(true, out JobHandle objDep);
            var netTree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle netDep);
            objDep.Complete();
            netDep.Complete();

            NativeList<Entity> rawResults = new NativeList<Entity>(Allocator.Temp);
            NativeParallelHashSet<Entity> processedRoots = new NativeParallelHashSet<Entity>(100, Allocator.Temp);
            NativeParallelHashSet<Entity> finalDeleteSet = new NativeParallelHashSet<Entity>(200, Allocator.Temp);

            try
            {
                RadiusDeleteMod.Log.Info($"[Logic] --- SEARCHING --- (R: {radius})");

                // 1. Gather Everything
                RadiusDeleteSearchJob searchJob = new RadiusDeleteSearchJob
                {
                    m_Results = rawResults,
                    m_Center = center,
                    m_RadiusSq = radius * radius,
                    m_SearchBounds = new Bounds2(center.xz - radius, center.xz + radius),
                    m_ObjectSearchTree = objTree,
                    m_NetSearchTree = netTree,
                    m_Transform = m_Transform
                };
                searchJob.Run();

                RadiusDeleteMod.Log.Info($"[Logic] Search found {rawResults.Length} raw entities.");

                // 2. Filter & Redirect
                int accepted = 0;
                for (int i = 0; i < rawResults.Length; i++)
                {
                    Entity raw = rawResults[i];
                    Entity root = GetRoot(raw); // Redirect to Parent Road/Building
                    
                    if (processedRoots.Contains(root)) continue;
                    processedRoots.Add(root);

                    string reason;
                    if (IsValidRoot(root, filters, out reason))
                    {
                        finalDeleteSet.Add(root);
                        accepted++;
                        if (accepted <= 5) RadiusDeleteMod.Log.Info($"[Accepted] #{i} RootID: {root.Index} ({reason})");
                    }
                    else
                    {
                        // Log first few rejections to diagnose issues
                        if (i < 5) RadiusDeleteMod.Log.Info($"[Rejected] #{i} RootID: {root.Index} Reason: {reason}");
                    }
                }

                // 3. Cascade SubObjects (Props/Trees on Buildings)
                // We do NOT cascade Lanes or Upgrades - the engine handles those.
                var roots = finalDeleteSet.ToNativeArray(Allocator.Temp);
                for (int i = 0; i < roots.Length; i++)
                {
                    if (m_SubObjects.TryGetBuffer(roots[i], out var subObjs))
                    {
                        for (int j = 0; j < subObjs.Length; j++) finalDeleteSet.Add(subObjs[j].m_SubObject);
                    }
                }
                roots.Dispose();

                // 4. Topology (Orphan Nodes)
                NativeList<Entity> orphanedNodes = new NativeList<Entity>(Allocator.Temp);
                foreach (Entity entity in finalDeleteSet)
                {
                    if (m_Edge.TryGetComponent(entity, out Game.Net.Edge edge))
                    {
                        ProcessNodeNeighbors(edge.m_Start, entity, ref orphanedNodes);
                        ProcessNodeNeighbors(edge.m_End, entity, ref orphanedNodes);
                    }
                }
                foreach (var node in orphanedNodes) finalDeleteSet.Add(node);

                // 5. Delete
                if (finalDeleteSet.Count() > 0)
                {
                    var deleteArray = finalDeleteSet.ToNativeArray(Allocator.Temp);
                    RadiusDeleteMod.Log.Info($"[Logic] Deleting {deleteArray.Length} items.");
                    EntityManager.AddComponent<Game.Common.Deleted>(deleteArray);
                }
                else
                {
                    RadiusDeleteMod.Log.Info($"[Logic] 0 items to delete after filtering.");
                }
            }
            catch (System.Exception ex) { RadiusDeleteMod.Log.Error($"[Logic] ERROR: {ex}"); }
            finally
            {
                if (rawResults.IsCreated) rawResults.Dispose();
                if (processedRoots.IsCreated) processedRoots.Dispose();
                if (finalDeleteSet.IsCreated) finalDeleteSet.Dispose();
            }
        }

        private Entity GetRoot(Entity entity)
        {
            Entity current = entity;
            int safety = 0;
            while (m_Owner.HasComponent(current) && safety < 10)
            {
                Entity parent = m_Owner[current].m_Owner;
                if (parent == Entity.Null) break;
                current = parent;
                safety++;
            }
            return current;
        }

        private bool IsValidRoot(Entity root, DeleteFilters filters, out string info)
        {
            info = "";
            if (root == Entity.Null) { info = "Null"; return false; }
            if (m_Temp.HasComponent(root)) { info = "Temp"; return false; }
            
            // Safe Underground Check (Nodes for Net, Transform for Objects)
            if (m_Edge.TryGetComponent(root, out var edge))
            {
                // Network: Check if BOTH nodes are deep underground (-5m)
                bool s = m_Transform.HasComponent(edge.m_Start) && m_Transform[edge.m_Start].m_Position.y < -5f;
                bool e = m_Transform.HasComponent(edge.m_End) && m_Transform[edge.m_End].m_Position.y < -5f;
                if (s && e) { info = "Net Deep Underground"; return false; }
            }
            else if (m_Transform.HasComponent(root))
            {
                // Object: Simple height check
                if (m_Transform[root].m_Position.y < -5f) { info = "Obj Deep Underground"; return false; }
            }

            if (!m_PrefabRef.HasComponent(root)) { info = "No Prefab"; return false; }
            Entity prefab = m_PrefabRef[root].m_Prefab;

            // Filter Logic
            if (m_BuildingData.HasComponent(prefab)) return CheckFilter(filters, DeleteFilters.Buildings, "Building", out info);
            if (m_TreeData.HasComponent(prefab)) return CheckFilter(filters, DeleteFilters.Trees, "Tree", out info);
            if (m_PlantData.HasComponent(prefab)) return CheckFilter(filters, DeleteFilters.Plants, "Plant", out info);
            if (m_ObjectData.HasComponent(prefab)) return CheckFilter(filters, DeleteFilters.Props, "Prop", out info);
            if (m_Edge.HasComponent(root) || m_Node.HasComponent(root)) return CheckFilter(filters, DeleteFilters.Networks, "Network", out info);

            info = "Unknown Type";
            return false;
        }

        private bool CheckFilter(DeleteFilters active, DeleteFilters target, string name, out string info)
        {
            if ((active & target) != 0) { info = name; return true; }
            info = $"{name} Filtered";
            return false;
        }

        private void ProcessNodeNeighbors(Entity node, Entity triggeringEdge, ref NativeList<Entity> orphanedNodes)
        {
            if (node == Entity.Null || !EntityManager.Exists(node)) return;
            if (m_ConnectedEdges.TryGetBuffer(node, out var connections))
            {
                if (connections.Length == 1 && connections[0].m_Edge == triggeringEdge)
                {
                    orphanedNodes.Add(node);
                }
                else
                {
                    foreach (var conn in connections)
                    {
                        if (conn.m_Edge != triggeringEdge && conn.m_Edge != Entity.Null)
                        {
                            EntityManager.AddComponent<Game.Common.Updated>(conn.m_Edge);
                            // Update neighbors' start/end nodes to refresh geometry
                            if (m_Edge.TryGetComponent(conn.m_Edge, out Game.Net.Edge neighborEdge))
                            {
                                EntityManager.AddComponent<Game.Common.Updated>(neighborEdge.m_Start);
                                EntityManager.AddComponent<Game.Common.Updated>(neighborEdge.m_End);
                            }
                        }
                    }
                    EntityManager.AddComponent<Game.Common.Updated>(node);
                }
            }
        }

        [BurstCompile]
        private struct RadiusDeleteVisualizationJob : Unity.Jobs.IJob
        {
            public OverlayRenderSystem.Buffer m_OverlayBuffer;
            public float3 m_Position;
            public float m_Radius;
            public UnityEngine.Color m_Color;
            public void Execute() { m_OverlayBuffer.DrawCircle(m_Color, default, m_Radius / 20f, 0, new float2(0, 1), m_Position, m_Radius * 2f); }
        }
    }

    [BurstCompile]
    public struct RadiusDeleteSearchJob : Unity.Jobs.IJob
    {
        public NativeList<Entity> m_Results;
        public float3 m_Center;
        public float m_RadiusSq;
        public Bounds2 m_SearchBounds;
        
        [ReadOnly] public NativeQuadTree<Entity, QuadTreeBoundsXZ> m_ObjectSearchTree;
        [ReadOnly] public NativeQuadTree<Entity, QuadTreeBoundsXZ> m_NetSearchTree;
        [ReadOnly] public ComponentLookup<Game.Objects.Transform> m_Transform;

        public void Execute()
        {
            RadiusIterator iterator = new RadiusIterator { m_Center = m_Center, m_RadiusSq = m_RadiusSq, m_SearchBounds = m_SearchBounds, m_Results = m_Results, m_Transform = m_Transform };
            m_ObjectSearchTree.Iterate(ref iterator);
            m_NetSearchTree.Iterate(ref iterator);
        }
    }

    public struct RadiusIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
    {
        public float3 m_Center;
        public float m_RadiusSq;
        public Bounds2 m_SearchBounds;
        public NativeList<Entity> m_Results;
        [ReadOnly] public ComponentLookup<Game.Objects.Transform> m_Transform;

        public bool Intersect(QuadTreeBoundsXZ bounds)
        {
            float3 min = bounds.m_Bounds.min; float3 max = bounds.m_Bounds.max;
            bool overlapX = (max.x >= m_SearchBounds.min.x) && (min.x <= m_SearchBounds.max.x);
            bool overlapZ = (max.z >= m_SearchBounds.min.y) && (min.z <= m_SearchBounds.max.y);
            return overlapX && overlapZ;
        }

        public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
        {
            if (Intersect(bounds))
            {
                // Simple radius check. If no transform (Network Segment), accept it and filter later.
                if (m_Transform.HasComponent(entity))
                {
                    if (math.distancesq(m_Transform[entity].m_Position, m_Center) <= m_RadiusSq) m_Results.Add(entity);
                }
                else
                {
                    m_Results.Add(entity);
                }
            }
        }
    }
}