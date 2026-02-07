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
using Game.Vehicles;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RadiusDelete
{
    [System.Flags]
    public enum DeleteFilters
    {
        None = 0,
        Networks = 1,
        Buildings = 2,
        Trees = 4,
        Plants = 8,
        Props = 16,
        All = 31
    }

    public partial class RadiusDeleteTool : BulldozeToolSystem
    {
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Game.Objects.SearchSystem m_ObjectSearchSystem;
        private Game.Net.SearchSystem m_NetSearchSystem;
        private ToolRaycastSystem m_ToolRaycastSystem;
        private BulldozeToolSystem m_VanillaBulldoze;

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
        
        private BufferLookup<Game.Net.SubLane> m_SubLanes;
        private BufferLookup<InstalledUpgrade> m_InstalledUpgrades;
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
            m_VanillaBulldoze = World.GetOrCreateSystemManaged<BulldozeToolSystem>();

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
            
            m_SubLanes = SystemAPI.GetBufferLookup<Game.Net.SubLane>(true);
            m_InstalledUpgrades = SystemAPI.GetBufferLookup<InstalledUpgrade>(true);
            m_SubObjects = SystemAPI.GetBufferLookup<Game.Objects.SubObject>(true);
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain | TypeMask.StaticObjects | TypeMask.Net;
            // Limit cursor to surface objects only
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        protected override void OnStartRunning() { applyAction.shouldBeEnabled = true; }
        protected override void OnStopRunning() { applyAction.shouldBeEnabled = false; }

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
            m_SubLanes.Update(this);
            m_InstalledUpgrades.Update(this);
            m_SubObjects.Update(this);

            bool hitFound = GetRaycastResult(out Entity e, out RaycastHit hit);
            if (hit.m_HitPosition.Equals(float3.zero)) hitFound = false;

            if (!hitFound) return inputDeps;

            RadiusDeleteVisualizationJob vizJob = new RadiusDeleteVisualizationJob()
            {
                m_OverlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle outJobHandle),
                m_Position = hit.m_HitPosition,
                m_Radius = Radius,
                m_Color = new UnityEngine.Color(1f, 0f, 0f, 0.5f)
            };
            inputDeps = vizJob.Schedule(JobHandle.CombineDependencies(inputDeps, outJobHandle));
            m_OverlayRenderSystem.AddBufferWriter(inputDeps);

            if (applyAction.WasPressedThisFrame())
            {
                inputDeps.Complete();
                DeleteInRadius(hit.m_HitPosition, Radius, ActiveFilters);
            }

            return inputDeps;
        }

        private void DeleteInRadius(float3 center, float radius, DeleteFilters filters)
        {
            var objTree = m_ObjectSearchSystem.GetStaticSearchTree(true, out JobHandle objDep);
            var netTree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle netDep);
            objDep.Complete();
            netDep.Complete();

            NativeParallelHashSet<Entity> edgesToDelete = new NativeParallelHashSet<Entity>(100, Allocator.Temp);
            NativeParallelHashSet<Entity> finalDeleteSet = new NativeParallelHashSet<Entity>(500, Allocator.Temp);
            NativeList<Entity> rootCandidates = new NativeList<Entity>(Allocator.Temp);

            try
            {
                RadiusDeleteMod.Log.Info($"[Audit] --- START DELETE --- (R:{radius} @ {center})");

                // 1. Root Search
                RadiusDeleteSearchJob searchJob = new RadiusDeleteSearchJob
                {
                    m_Results = rootCandidates,
                    m_EdgesFound = edgesToDelete,
                    m_Center = center,
                    m_RadiusSq = radius * radius,
                    m_SearchBounds = new Bounds2(center.xz - radius, center.xz + radius),
                    m_Filters = filters,
                    m_ObjectSearchTree = objTree,
                    m_NetSearchTree = netTree,
                    m_PrefabRef = m_PrefabRef,
                    m_BuildingData = m_BuildingData,
                    m_TreeData = m_TreeData,
                    m_ObjectData = m_ObjectData,
                    m_PlantData = m_PlantData,
                    m_Owner = m_Owner,
                    m_Transform = m_Transform,
                    m_Edge = m_Edge,
                    m_Node = m_Node,
                    m_Building = m_Building,
                    m_Temp = m_Temp
                };
                searchJob.Run();

                RadiusDeleteMod.Log.Info($"[Audit] Phase 1: Found {rootCandidates.Length} Root objects.");

                foreach (var root in rootCandidates) finalDeleteSet.Add(root);

                // 2. Cascade Search (Sub-objects)
                // We create a temporary list to iterate while adding to the set
                var tempArray = finalDeleteSet.ToNativeArray(Allocator.Temp);
                int cascadeTotal = 0;
                for (int i = 0; i < tempArray.Length; i++)
                {
                    Entity parent = tempArray[i];
                    
                    if (m_SubLanes.TryGetBuffer(parent, out var lanes))
                    {
                        for (int j = 0; j < lanes.Length; j++) 
                            if (finalDeleteSet.Add(lanes[j].m_SubLane)) cascadeTotal++;
                    }
                    if (m_InstalledUpgrades.TryGetBuffer(parent, out var upgrades))
                    {
                        for (int j = 0; j < upgrades.Length; j++) 
                            if (finalDeleteSet.Add(upgrades[j].m_Upgrade)) cascadeTotal++;
                    }
                    if (m_SubObjects.TryGetBuffer(parent, out var subObjs))
                    {
                        for (int j = 0; j < subObjs.Length; j++) 
                            if (finalDeleteSet.Add(subObjs[j].m_SubObject)) cascadeTotal++;
                    }
                }
                tempArray.Dispose();
                RadiusDeleteMod.Log.Info($"[Audit] Phase 2: Cascaded to {cascadeTotal} child entities.");

                // 3. Topology Audit (Orphaned Nodes)
                int nodesAdded = 0;
                NativeList<Entity> nodesToVerify = new NativeList<Entity>(Allocator.Temp);
                foreach (Entity edgeEntity in edgesToDelete)
                {
                    if (m_Edge.TryGetComponent(edgeEntity, out Game.Net.Edge edge))
                    {
                        ProcessNodeNeighbors(edge.m_Start, edgeEntity, ref nodesToVerify);
                        ProcessNodeNeighbors(edge.m_End, edgeEntity, ref nodesToVerify);
                    }
                }
                foreach (var node in nodesToVerify) 
                    if (finalDeleteSet.Add(node)) nodesAdded++;
                
                RadiusDeleteMod.Log.Info($"[Audit] Phase 3: Added {nodesAdded} orphaned network nodes.");

                // 4. Final Audit & Delete
                if (finalDeleteSet.Count() > 0)
                {
                    var deleteArray = finalDeleteSet.ToNativeArray(Allocator.Temp);
                    int edges = 0, nodes = 0, misc = 0;
                    
                    for (int i = 0; i < deleteArray.Length; i++)
                    {
                        Entity ent = deleteArray[i];
                        if (m_Edge.HasComponent(ent)) edges++;
                        else if (m_Node.HasComponent(ent)) nodes++;
                        else misc++;
                        
                        // Log full detail for small deletes, first 50 for big ones
                        if (deleteArray.Length < 100 || i < 50)
                            RadiusDeleteMod.Log.Info($"[Final Delete List] Item #{i}: ID:{ent.Index} (Edge:{m_Edge.HasComponent(ent)})");
                    }

                    RadiusDeleteMod.Log.Info($"[Audit] DELETING: {edges} Edges, {nodes} Nodes, {misc} Subelements/Props.");
                    EntityManager.AddComponent<Deleted>(deleteArray);
                    RadiusDeleteMod.Log.Info($"[Audit] SUCCESS.");
                }
            }
            catch (System.Exception ex)
            {
                RadiusDeleteMod.Log.Error($"[Audit] FATAL ERROR IN RADIUS DELETE: {ex}");
            }
            finally
            {
                if (finalDeleteSet.IsCreated) finalDeleteSet.Dispose();
                if (edgesToDelete.IsCreated) edgesToDelete.Dispose();
                if (rootCandidates.IsCreated) rootCandidates.Dispose();
            }
        }

        private void ProcessNodeNeighbors(Entity node, Entity triggeringEdge, ref NativeList<Entity> nodesToVerify)
        {
            if (node == Entity.Null || !EntityManager.Exists(node)) return;
            if (m_ConnectedEdges.TryGetBuffer(node, out var connections))
            {
                if (connections.Length == 1 && connections[0].m_Edge == triggeringEdge)
                {
                    nodesToVerify.Add(node);
                }
                else
                {
                    foreach (var conn in connections)
                    {
                        if (conn.m_Edge != triggeringEdge && conn.m_Edge != Entity.Null)
                        {
                            EntityManager.AddComponent<Updated>(conn.m_Edge);
                            if (m_Edge.TryGetComponent(conn.m_Edge, out Game.Net.Edge neighborEdge))
                            {
                                EntityManager.AddComponent<Updated>(neighborEdge.m_Start);
                                EntityManager.AddComponent<Updated>(neighborEdge.m_End);
                            }
                        }
                    }
                    EntityManager.AddComponent<Updated>(node);
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
        public NativeParallelHashSet<Entity> m_EdgesFound;
        public float3 m_Center;
        public float m_RadiusSq;
        public Bounds2 m_SearchBounds;
        public DeleteFilters m_Filters;
        
        [ReadOnly] public NativeQuadTree<Entity, QuadTreeBoundsXZ> m_ObjectSearchTree;
        [ReadOnly] public NativeQuadTree<Entity, QuadTreeBoundsXZ> m_NetSearchTree;
        [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRef;
        [ReadOnly] public ComponentLookup<BuildingData> m_BuildingData;
        [ReadOnly] public ComponentLookup<TreeData> m_TreeData;
        [ReadOnly] public ComponentLookup<ObjectData> m_ObjectData;
        [ReadOnly] public ComponentLookup<PlantData> m_PlantData;
        [ReadOnly] public ComponentLookup<Owner> m_Owner;
        [ReadOnly] public ComponentLookup<Game.Objects.Transform> m_Transform;
        [ReadOnly] public ComponentLookup<Game.Net.Edge> m_Edge;
        [ReadOnly] public ComponentLookup<Game.Net.Node> m_Node;
        [ReadOnly] public ComponentLookup<Building> m_Building;
        [ReadOnly] public ComponentLookup<Temp> m_Temp;

        public void Execute()
        {
            RadiusIterator iterator = new RadiusIterator { m_Center = m_Center, m_RadiusSq = m_RadiusSq, m_SearchBounds = m_SearchBounds, m_Results = new NativeList<Entity>(Allocator.Temp), m_Transform = m_Transform };
            
            // 1. STATIC OBJECT SEARCH
            m_ObjectSearchTree.Iterate(ref iterator);
            for (int i = 0; i < iterator.m_Results.Length; i++)
            {
                Entity entity = iterator.m_Results[i];
                if (IsRootAndValid(entity)) m_Results.Add(entity);
            }

            // 2. NETWORK SEARCH
            if ((m_Filters & DeleteFilters.Networks) != 0)
            {
                iterator.m_Results.Clear();
                m_NetSearchTree.Iterate(ref iterator);
                for (int i = 0; i < iterator.m_Results.Length; i++)
                {
                    Entity entity = iterator.m_Results[i];
                    if (m_Temp.HasComponent(entity) || IsUnderground(entity)) continue;
                    
                    if (m_Edge.HasComponent(entity)) 
                    { 
                        m_Results.Add(entity); 
                        m_EdgesFound.Add(entity); 
                    }
                    else if (m_Node.HasComponent(entity)) 
                    {
                        m_Results.Add(entity);
                    }
                }
            }
            iterator.m_Results.Dispose();
        }

        private bool IsUnderground(Entity entity)
        {
            if (m_Transform.HasComponent(entity))
            {
                // Falling back to height check because 'Underground' component is unreliable
                return m_Transform[entity].m_Position.y < -1.0f;
            }
            return false;
        }

        private bool IsRootAndValid(Entity entity)
        {
            if (entity == Entity.Null || m_Temp.HasComponent(entity) || IsUnderground(entity)) return false;

            // CRITICAL: Filter out child entities. 
            // We only want parents (Edges, Nodes, standalone Props).
            // Sub-elements are added via Cascade later.
            if (m_Owner.HasComponent(entity) && m_Owner[entity].m_Owner != Entity.Null) return false;

            if (!m_PrefabRef.HasComponent(entity)) return false;
            Entity prefab = m_PrefabRef[entity].m_Prefab;
            
            bool isBuilding = m_BuildingData.HasComponent(prefab);
            if ((m_Filters & DeleteFilters.Buildings) != 0 && isBuilding) return true;
            if ((m_Filters & DeleteFilters.Trees) != 0 && m_TreeData.HasComponent(prefab)) return true;
            if ((m_Filters & DeleteFilters.Plants) != 0 && m_PlantData.HasComponent(prefab)) return true;
            if ((m_Filters & DeleteFilters.Props) != 0 && m_ObjectData.HasComponent(prefab) && !isBuilding) return true;
            return false;
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
                if (m_Transform.HasComponent(entity))
                {
                    if (math.distancesq(m_Transform[entity].m_Position, m_Center) <= m_RadiusSq) m_Results.Add(entity);
                }
                else m_Results.Add(entity);
            }
        }
    }
}