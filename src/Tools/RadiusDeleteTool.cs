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
        None = 0, Networks = 1, Buildings = 2, Trees = 4, Plants = 8, Props = 16, Surfaces = 32, All = 63
    }

    public partial class RadiusDeleteTool : BulldozeToolSystem
    {
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Game.Objects.SearchSystem m_ObjectSearchSystem;
        private Game.Net.SearchSystem m_NetSearchSystem;
        private EntityQuery m_HighlightQuery;

        private ComponentLookup<PrefabRef> m_PrefabRef;
        private ComponentLookup<BuildingData> m_BuildingData;
        private ComponentLookup<TreeData> m_TreeData;
        private ComponentLookup<ObjectData> m_ObjectData;
        private ComponentLookup<PlantData> m_PlantData;
        private ComponentLookup<Game.Objects.Transform> m_Transform;

        public float Radius = 20f;
        public DeleteFilters ActiveFilters = DeleteFilters.All;

        public override string toolID => "Bulldoze Tool";

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_ObjectSearchSystem = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();

            m_PrefabRef = SystemAPI.GetComponentLookup<PrefabRef>(true);
            m_BuildingData = SystemAPI.GetComponentLookup<BuildingData>(true);
            m_TreeData = SystemAPI.GetComponentLookup<TreeData>(true);
            m_ObjectData = SystemAPI.GetComponentLookup<ObjectData>(true);
            m_PlantData = SystemAPI.GetComponentLookup<PlantData>(true);
            m_Transform = SystemAPI.GetComponentLookup<Game.Objects.Transform>(true);

            m_HighlightQuery = GetEntityQuery(ComponentType.ReadWrite<Game.Tools.Highlighted>());
        }

        protected override void OnStartRunning()
        {
            if (applyAction != null) applyAction.shouldBeEnabled = true;
        }

        protected override void OnStopRunning()
        {
            if (applyAction != null) applyAction.shouldBeEnabled = false;
            if (!m_HighlightQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.RemoveComponent<Game.Tools.Highlighted>(m_HighlightQuery);
            }
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain | TypeMask.StaticObjects | TypeMask.Net;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            m_PrefabRef.Update(this);
            m_BuildingData.Update(this);
            m_TreeData.Update(this);
            m_ObjectData.Update(this);
            m_PlantData.Update(this);
            m_Transform.Update(this);

            if (!m_HighlightQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.RemoveComponent<Game.Tools.Highlighted>(m_HighlightQuery);
            }

            try
            {
                if (!GetRaycastResult(out Entity e, out RaycastHit hit) || hit.m_HitPosition.Equals(float3.zero))
                {
                    return inputDeps;
                }

                NativeList<Entity> targets = new NativeList<Entity>(Allocator.TempJob);
                RadiusDeleteSearchJob searchJob = new RadiusDeleteSearchJob
                {
                    m_Results = targets,
                    m_Center = hit.m_HitPosition,
                    m_RadiusSq = Radius * Radius,
                    m_SearchBounds = new Bounds2(hit.m_HitPosition.xz - Radius, hit.m_HitPosition.xz + Radius),
                    m_ObjectSearchTree = m_ObjectSearchSystem.GetStaticSearchTree(true, out JobHandle objDep),
                    m_NetSearchTree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle netDep),
                    m_Transform = m_Transform
                };

                JobHandle searchHandle = searchJob.Schedule(JobHandle.CombineDependencies(objDep, netDep, inputDeps));
                searchHandle.Complete();

                /* Highlighting trigger commented out per request
                for (int i = 0; i < targets.Length; i++)
                {
                    Entity target = targets[i];
                    if (EntityManager.Exists(target) && IsTypeValid(target, ActiveFilters, hit.m_HitPosition.y))
                    {
                        EntityManager.AddComponent<Game.Tools.Highlighted>(target);
                    }
                }
                */

                RadiusDeleteVisualizationJob vizJob = new RadiusDeleteVisualizationJob()
                {
                    m_OverlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle outJobHandle),
                    m_Position = hit.m_HitPosition,
                    m_Radius = Radius,
                    m_Color = new UnityEngine.Color(1f, 0f, 0f, 0.4f)
                };
                inputDeps = vizJob.Schedule(JobHandle.CombineDependencies(searchHandle, outJobHandle));
                m_OverlayRenderSystem.AddBufferWriter(inputDeps);

                if (applyAction != null && applyAction.WasPressedThisFrame())
                {
                    inputDeps.Complete();
                    DeleteInRadius(hit.m_HitPosition, Radius, ActiveFilters);
                }

                targets.Dispose();
            }
            catch (System.Exception ex)
            {
                RadiusDeleteMod.Log.Warn($"RadiusTool Update Error: {ex.Message}");
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
            
            var edgeLookup = SystemAPI.GetComponentLookup<Game.Net.Edge>(true);
            var connectedEdgesLookup = SystemAPI.GetBufferLookup<Game.Net.ConnectedEdge>(true);

            try
            {
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

                for (int i = 0; i < rawResults.Length; i++)
                {
                    Entity entity = rawResults[i];
                    if (!EntityManager.Exists(entity)) continue;

                    if (IsTypeValid(entity, filters, center.y))
                    {
                        finalDeleteSet.Add(entity);

                        if (EntityManager.HasComponent<Game.Net.Node>(entity))
                        {
                            if (connectedEdgesLookup.TryGetBuffer(entity, out var connections))
                            {
                                for (int j = 0; j < connections.Length; j++)
                                {
                                    if (EntityManager.Exists(connections[j].m_Edge))
                                        finalDeleteSet.Add(connections[j].m_Edge);
                                }
                            }
                        }
                    }
                }

                if (finalDeleteSet.Count() > 0)
                {
                    var deleteArray = finalDeleteSet.ToNativeArray(Allocator.Temp);
                    for (int i = 0; i < deleteArray.Length; i++)
                    {
                        if (edgeLookup.TryGetComponent(deleteArray[i], out var edge))
                        {
                            if (EntityManager.Exists(edge.m_Start)) EntityManager.AddComponent<Game.Common.Updated>(edge.m_Start);
                            if (EntityManager.Exists(edge.m_End)) EntityManager.AddComponent<Game.Common.Updated>(edge.m_End);
                        }
                    }
                    EntityManager.AddComponent<Game.Common.Deleted>(deleteArray);
                }
            }
            finally
            {
                if (rawResults.IsCreated) rawResults.Dispose();
                if (finalDeleteSet.IsCreated) finalDeleteSet.Dispose();
            }
        }

        private bool IsTypeValid(Entity entity, DeleteFilters active, float surfaceHeight)
        {
            if (EntityManager.HasComponent<Game.Common.Deleted>(entity)) return false;

            // 1. Relative Elevation check (Networks)
            // Fixes CS1061: Use HasComponent and GetComponentData instead of TryGetComponent
            if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
            {
                var elevation = EntityManager.GetComponentData<Game.Net.Elevation>(entity);
                if (elevation.m_Elevation.x < 0) return false;
            }

            // 2. Absolute Depth check relative to the surface hit point (fixes the "hill" issue)
            if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
            {
                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                if (transform.m_Position.y < surfaceHeight - 19.0f) return false;
            }

            if (EntityManager.HasComponent<Game.Objects.Marker>(entity)) return false;
            if (EntityManager.HasComponent<Game.Common.Owner>(entity) && EntityManager.GetComponentData<Game.Common.Owner>(entity).m_Owner != Entity.Null) return false;

            if (EntityManager.HasComponent<Game.Areas.Surface>(entity))
                return (active & DeleteFilters.Surfaces) != 0;

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

            if (EntityManager.HasComponent<Game.Net.Edge>(entity) || EntityManager.HasComponent<Game.Net.Node>(entity))
                return (active & DeleteFilters.Networks) != 0;

            return false;
        }

        [BurstCompile]
        private struct RadiusDeleteVisualizationJob : IJob
        {
            public OverlayRenderSystem.Buffer m_OverlayBuffer;
            public float3 m_Position;
            public float m_Radius;
            public UnityEngine.Color m_Color;
            public void Execute() 
            { 
                m_OverlayBuffer.DrawCircle(m_Color, default, m_Radius / 20f, 0, new float2(0, 1), m_Position, m_Radius * 2f); 
            }
        }
    }

    [BurstCompile]
    public struct RadiusDeleteSearchJob : IJob
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
                float3 position;
                if (m_Transform.HasComponent(entity))
                {
                    position = m_Transform[entity].m_Position;
                }
                else
                {
                    // Fallback for Edges: calculate the center of the Bounds3 manually
                    position = (bounds.m_Bounds.min + bounds.m_Bounds.max) * 0.5f;
                }

                if (math.distancesq(position, m_Center) <= m_RadiusSq) 
                {
                    m_Results.Add(entity);
                }
            }
        }
    }
}