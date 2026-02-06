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
        None = 0,
        Networks = 1,
        Buildings = 2,
        Trees = 4,
        Plants = 8,
        Props = 16,
        All = 31
    }

    public partial class RadiusDeleteTool : ToolBaseSystem
    {
        private ToolOutputBarrier m_ToolOutputBarrier;
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Game.Objects.SearchSystem m_ObjectSearchSystem;
        private Game.Net.SearchSystem m_NetSearchSystem;

        // Component Lookups (Must be updated every frame!)
        private ComponentLookup<PrefabRef> m_PrefabRef;
        private ComponentLookup<BuildingData> m_BuildingData;
        private ComponentLookup<TreeData> m_TreeData;
        private ComponentLookup<ObjectData> m_ObjectData;
        private ComponentLookup<PlantData> m_PlantData;
        private ComponentLookup<Owner> m_Owner;
        private ComponentLookup<Game.Objects.Transform> m_Transform;

        public float Radius = 20f;
        public DeleteFilters ActiveFilters = DeleteFilters.All;

        public override string toolID => "RadiusDeleteTool";

        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_ObjectSearchSystem = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();

            // Initialize Lookups
            m_PrefabRef = SystemAPI.GetComponentLookup<PrefabRef>(true);
            m_BuildingData = SystemAPI.GetComponentLookup<BuildingData>(true);
            m_TreeData = SystemAPI.GetComponentLookup<TreeData>(true);
            m_ObjectData = SystemAPI.GetComponentLookup<ObjectData>(true);
            m_PlantData = SystemAPI.GetComponentLookup<PlantData>(true);
            m_Owner = SystemAPI.GetComponentLookup<Owner>(true);
            m_Transform = SystemAPI.GetComponentLookup<Game.Objects.Transform>(true);
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            RadiusDeleteMod.Log.Info("RadiusDeleteTool: OnStartRunning called. Tool Activated.");
        }

        protected override void OnStopRunning()
        {
            base.OnStopRunning();
            applyAction.shouldBeEnabled = false;
            RadiusDeleteMod.Log.Info("RadiusDeleteTool: OnStopRunning called. Tool Deactivated.");
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            // 1. Update Lookups (Critical)
            UpdateLookups();

            // 2. Debug Raycast
            bool hitFound = GetRaycastResult(out Entity e, out RaycastHit hit);
            
            // 3. Visualization (Run immediately on main thread for debug)
            if (hitFound)
            {
                // Simple debug draw to prove we are alive
                // (This line draws a persistent line in the scene view if you have it open)
                // UnityEngine.Debug.DrawLine(hit.m_HitPosition, hit.m_HitPosition + new float3(0, 10, 0), Color.red);
            }

            // 4. Input Check
            // Log every frame that we are active to prove the loop isn't crashing
            // RadiusDeleteMod.Log.Info("RadiusDeleteTool: OnUpdate running..."); 

            if (applyAction.WasPressedThisFrame())
            {
                RadiusDeleteMod.Log.Info($"RadiusDeleteTool: Click Detected! Scheduling Delete at {hit.m_HitPosition}");
                
                // Pass the job
                inputDeps = ScheduleRadiusDelete(hit.m_HitPosition, Radius, ActiveFilters, inputDeps);
            }

            return inputDeps;
        }

        private void UpdateLookups()
        {
            m_PrefabRef.Update(this);
            m_BuildingData.Update(this);
            m_TreeData.Update(this);
            m_ObjectData.Update(this);
            m_PlantData.Update(this);
            m_Owner.Update(this);
            m_Transform.Update(this);
        }
        

        private JobHandle ScheduleRadiusDelete(float3 center, float radius, DeleteFilters filters, JobHandle inputDeps)
        {
            var objTree = m_ObjectSearchSystem.GetStaticSearchTree(true, out JobHandle objDep);
            var netTree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle netDep);

            RadiusDeleteJob job = new RadiusDeleteJob
            {
                m_CommandBuffer = m_ToolOutputBarrier.CreateCommandBuffer(),
                m_Center = center,
                m_RadiusSq = radius * radius,
                m_SearchBounds = new Bounds2(center.xz - radius, center.xz + radius),
                m_Filters = filters,
                m_ObjectSearchTree = objTree,
                m_NetSearchTree = netTree,
                // Pass the updated lookups
                m_PrefabRef = m_PrefabRef,
                m_BuildingData = m_BuildingData,
                m_TreeData = m_TreeData,
                m_ObjectData = m_ObjectData,
                m_PlantData = m_PlantData,
                m_Owner = m_Owner,
                m_Transform = m_Transform
            };

            return job.Schedule(JobHandle.CombineDependencies(inputDeps, objDep, netDep));
        }

        //[BurstCompile]
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

    //[BurstCompile]
    public struct RadiusDeleteJob : IJob
    {
        public EntityCommandBuffer m_CommandBuffer;
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

        public void Execute()
        {
            
            UnityEngine.Debug.Log($"[RadiusJob] Executing search at {m_Center} with radius {math.sqrt(m_RadiusSq)}");
            
            try 
            {
                
                RadiusIterator iterator = new RadiusIterator
                {
                    m_Center = m_Center,
                    m_RadiusSq = m_RadiusSq,
                    m_SearchBounds = m_SearchBounds,
                    m_Results = new NativeList<Entity>(Allocator.Temp),
                    m_Transform = m_Transform
                };

                // Objects
                m_ObjectSearchTree.Iterate(ref iterator);
                for (int i = 0; i < iterator.m_Results.Length; i++)
                {
                    Entity entity = iterator.m_Results[i];
                    if (ShouldDelete(entity)) m_CommandBuffer.AddComponent<Deleted>(entity);
                }

                // Networks
                if ((m_Filters & DeleteFilters.Networks) != 0)
                {
                    iterator.m_Results.Clear();
                    m_NetSearchTree.Iterate(ref iterator);
                    for (int i = 0; i < iterator.m_Results.Length; i++)
                    {
                        Entity entity = iterator.m_Results[i];
                        if (!m_Owner.HasComponent(entity)) m_CommandBuffer.AddComponent<Deleted>(entity);
                    }
                }

                iterator.m_Results.Dispose();
                
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[RadiusJob] CRASH: {ex.Message} \n {ex.StackTrace}");
            }
        }

        private bool ShouldDelete(Entity entity)
        {
            if (m_Owner.HasComponent(entity)) return false;
            if (!m_PrefabRef.HasComponent(entity)) return false;
            Entity prefab = m_PrefabRef[entity].m_Prefab;

            if ((m_Filters & DeleteFilters.Buildings) != 0 && m_BuildingData.HasComponent(prefab)) return true;
            if ((m_Filters & DeleteFilters.Trees) != 0 && m_TreeData.HasComponent(prefab)) return true;
            if ((m_Filters & DeleteFilters.Plants) != 0 && m_PlantData.HasComponent(prefab)) return true;
            if ((m_Filters & DeleteFilters.Props) != 0 && m_ObjectData.HasComponent(prefab) && !m_BuildingData.HasComponent(prefab)) return true;

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
            Bounds2 nodeBounds = new Bounds2(bounds.m_Bounds.min.xz, bounds.m_Bounds.max.xz);
            return MathUtils.Intersect(nodeBounds, m_SearchBounds);
        }

        public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
        {
            Bounds2 nodeBounds = new Bounds2(bounds.m_Bounds.min.xz, bounds.m_Bounds.max.xz);
            if (MathUtils.Intersect(nodeBounds, m_SearchBounds))
            {
                if (m_Transform.TryGetComponent(entity, out var transform))
                {
                    if (math.distancesq(transform.m_Position, m_Center) <= m_RadiusSq)
                    {
                        m_Results.Add(entity);
                    }
                }
                else
                {
                    m_Results.Add(entity);
                }
            }
        }
    }
}