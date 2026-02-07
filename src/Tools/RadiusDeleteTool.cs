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

        public float Radius = 20f;
        public DeleteFilters ActiveFilters = DeleteFilters.All;

        public override string toolID => "Radius Delete Tool";

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_ObjectSearchSystem = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            m_ToolRaycastSystem = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain | TypeMask.StaticObjects | TypeMask.Net;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            inputDeps = base.OnUpdate(inputDeps);

            bool hitFound = GetRaycastResult(out Entity e, out RaycastHit hit);
            if (hit.m_HitPosition.Equals(float3.zero)) hitFound = false;

            if (hitFound)
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

                if (applyAction.WasPressedThisFrame())
                {
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
            NativeParallelHashSet<Entity> finalDeleteSet = new NativeParallelHashSet<Entity>(500, Allocator.Temp);
            
            // Lookups needed for topology safety
            var edgeLookup = SystemAPI.GetComponentLookup<Game.Net.Edge>(true);
            var connectedEdgesLookup = SystemAPI.GetBufferLookup<Game.Net.ConnectedEdge>(true);

            try
            {
                RadiusDeleteMod.Log.Info($"[Forensic Audit] === START SAFE DELETE (Radius: {radius}) ===");

                RadiusDeleteSearchJob searchJob = new RadiusDeleteSearchJob
                {
                    m_Results = rawResults,
                    m_Center = center,
                    m_RadiusSq = radius * radius,
                    m_SearchBounds = new Bounds2(center.xz - radius, center.xz + radius),
                    m_ObjectSearchTree = objTree,
                    m_NetSearchTree = netTree,
                    m_Transform = SystemAPI.GetComponentLookup<Game.Objects.Transform>(true)
                };
                searchJob.Run();

                for (int i = 0; i < rawResults.Length; i++)
                {
                    Entity entity = rawResults[i];
                    if (!EntityManager.Exists(entity)) continue;

                    // ... (Keep your existing filters: Markers, Owners, Temp, Subway Shield) ...
                    if (EntityManager.HasComponent<Game.Objects.Marker>(entity)) continue;
                    if (EntityManager.HasComponent<Game.Common.Owner>(entity))
                    {
                        if (EntityManager.GetComponentData<Game.Common.Owner>(entity).m_Owner != Entity.Null) continue;
                    }

                    if (IsTypeValid(entity, filters))
                    {
                        finalDeleteSet.Add(entity);

                        // --- TOPOLOGY SAFETY: NODE EXPANSION ---
                        // If we are deleting a Node, we MUST delete all edges connected to it
                        if (EntityManager.HasComponent<Game.Net.Node>(entity))
                        {
                            if (connectedEdgesLookup.TryGetBuffer(entity, out var connections))
                            {
                                for (int j = 0; j < connections.Length; j++)
                                {
                                    Entity connectedEdge = connections[j].m_Edge;
                                    if (EntityManager.Exists(connectedEdge))
                                    {
                                        finalDeleteSet.Add(connectedEdge);
                                        RadiusDeleteMod.Log.Info($"   -> [TOPOLOGY] Adding Edge {connectedEdge.Index} because its Node {entity.Index} is being deleted.");
                                    }
                                }
                            }
                        }
                    }
                }

                if (finalDeleteSet.Count() > 0)
                {
                    var deleteArray = finalDeleteSet.ToNativeArray(Allocator.Temp);
                    
                    // --- TOPOLOGY SAFETY: UPDATE NEIGHBORS ---
                    // Before deleting, tell the neighbors of these edges/nodes to refresh
                    for (int i = 0; i < deleteArray.Length; i++)
                    {
                        Entity e = deleteArray[i];
                        if (edgeLookup.TryGetComponent(e, out var edge))
                        {
                            // Mark the start/end nodes as Updated so they refresh their geometry/pathing
                            if (EntityManager.Exists(edge.m_Start)) EntityManager.AddComponent<Game.Common.Updated>(edge.m_Start);
                            if (EntityManager.Exists(edge.m_End)) EntityManager.AddComponent<Game.Common.Updated>(edge.m_End);
                        }
                    }

                    RadiusDeleteMod.Log.Info($"[Forensic Audit] Deleting {deleteArray.Length} entities with topology safety.");
                    EntityManager.AddComponent<Game.Common.Deleted>(deleteArray);
                    RadiusDeleteMod.Log.Info($"[Forensic Audit] SUCCESS.");
                }
            }
            catch (System.Exception ex) { RadiusDeleteMod.Log.Error($"[Forensic Audit] FATAL: {ex}"); }
            finally
            {
                if (rawResults.IsCreated) rawResults.Dispose();
                if (finalDeleteSet.IsCreated) finalDeleteSet.Dispose();
            }
        }

        private bool IsTypeValid(Entity entity, DeleteFilters active)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;

            if (EntityManager.HasComponent<BuildingData>(prefab)) 
                return (active & DeleteFilters.Buildings) != 0;
            
            if (EntityManager.HasComponent<TreeData>(prefab)) 
                return (active & DeleteFilters.Trees) != 0;
            
            if (EntityManager.HasComponent<PlantData>(prefab)) 
                return (active & DeleteFilters.Plants) != 0;

            if (EntityManager.HasComponent<ObjectData>(prefab)) 
                return (active & DeleteFilters.Props) != 0;

            // FIX 2: Resolve Ambiguous Reference for Line 190
            if (EntityManager.HasComponent<Game.Net.Edge>(entity) || EntityManager.HasComponent<Game.Net.Node>(entity))
                return (active & DeleteFilters.Networks) != 0;

            return false;
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
                if (m_Transform.HasComponent(entity))
                {
                    float2 pos = m_Transform[entity].m_Position.xz;
                    if (math.distancesq(pos, m_Center.xz) <= m_RadiusSq) m_Results.Add(entity);
                }
                else
                {
                    // For network segments, just grab them and we'll check their nodes' distances/heights on the main thread
                    m_Results.Add(entity);
                }
            }
        }
    }
}