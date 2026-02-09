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
        // Systems required for rendering overlays and searching spatial structures
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Game.Objects.SearchSystem m_ObjectSearchSystem;
        private Game.Net.SearchSystem m_NetSearchSystem;
        private EntityQuery m_HighlightQuery;
        
        // Use a barrier for safe deletion command buffering (prevents race conditions)
        private ToolOutputBarrier m_ToolOutputBarrier;

        // ComponentLookups allow efficient access to entity component data during jobs
        private ComponentLookup<PrefabRef> m_PrefabRef;
        private ComponentLookup<BuildingData> m_BuildingData;
        private ComponentLookup<TreeData> m_TreeData;
        private ComponentLookup<ObjectData> m_ObjectData;
        private ComponentLookup<PlantData> m_PlantData;
        private ComponentLookup<Game.Objects.Transform> m_Transform;

        // Tool settings: radius size and active filters (Surfaces are excluded by default)
        public float Radius = 30f;
        public DeleteFilters ActiveFilters = DeleteFilters.All ^ DeleteFilters.Surfaces;
        
        public override string toolID => "Bulldoze Tool";

        protected override void OnCreate()
        {
            base.OnCreate();
            // Retrieve necessary game systems
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_ObjectSearchSystem = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_NetSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            m_ToolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();

            // Initialize component lookups
            m_PrefabRef = SystemAPI.GetComponentLookup<PrefabRef>(true);
            m_BuildingData = SystemAPI.GetComponentLookup<BuildingData>(true);
            m_TreeData = SystemAPI.GetComponentLookup<TreeData>(true);
            m_ObjectData = SystemAPI.GetComponentLookup<ObjectData>(true);
            m_PlantData = SystemAPI.GetComponentLookup<PlantData>(true);
            m_Transform = SystemAPI.GetComponentLookup<Game.Objects.Transform>(true);

            // Query for handling highlighted entities (cleanup)
            m_HighlightQuery = GetEntityQuery(ComponentType.ReadWrite<Game.Tools.Highlighted>());
        }

        protected override void OnStartRunning()
        {
            // Enable the "Apply" action (usually left mouse click) when tool activates
            if (applyAction != null) applyAction.shouldBeEnabled = true;
        }

        protected override void OnStopRunning()
        {
            // Disable input action and clear highlights when tool deactivates
            if (applyAction != null) applyAction.shouldBeEnabled = false;
            if (!m_HighlightQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.RemoveComponent<Game.Tools.Highlighted>(m_HighlightQuery);
            }
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            // Configure the raycast to only hit Terrain. 
            // This ensures the tool cursor stays on the ground and ignores buildings/objects blocking the view.
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            // Refresh component lookups every frame to ensure data validity
            m_PrefabRef.Update(this);
            m_BuildingData.Update(this);
            m_TreeData.Update(this);
            m_ObjectData.Update(this);
            m_PlantData.Update(this);
            m_Transform.Update(this);

            // Clear any lingering highlight components
            if (!m_HighlightQuery.IsEmptyIgnoreFilter)
            {
                EntityManager.RemoveComponent<Game.Tools.Highlighted>(m_HighlightQuery);
            }

            try
            {
                // Perform raycast to find the cursor position on the terrain
                if (!GetRaycastResult(out Entity e, out RaycastHit hit) || hit.m_HitPosition.Equals(float3.zero))
                {
                    return inputDeps;
                }

                float3 groundPos = hit.m_HitPosition;

                NativeList<Entity> targets = new NativeList<Entity>(Allocator.TempJob);
                try
                {
                    // Run a search query around the cursor position. 
                    // (Currently unused but structure exists for potential highlighting or pre-calculation).
                    RadiusDeleteSearchJob searchJob = new RadiusDeleteSearchJob
                    {
                        m_Results = targets,
                        m_Center = groundPos,
                        m_RadiusSq = Radius * Radius,
                        m_SearchBounds = new Bounds2(groundPos.xz - Radius, groundPos.xz + Radius),
                        m_ObjectSearchTree = m_ObjectSearchSystem.GetStaticSearchTree(true, out JobHandle objDep),
                        m_NetSearchTree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle netDep),
                        m_Transform = m_Transform
                    };

                    JobHandle searchHandle = searchJob.Schedule(JobHandle.CombineDependencies(objDep, netDep, inputDeps));
                    searchHandle.Complete();

                    /* Highlighting disabled */

                    // Queue a job to draw the tool's visual radius circle on the overlay
                    RadiusDeleteVisualizationJob vizJob = new RadiusDeleteVisualizationJob()
                    {
                        m_OverlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle outJobHandle),
                        m_Position = groundPos,
                        m_Radius = Radius,
                        m_Color = new UnityEngine.Color(1f, 0f, 0f, 0.4f)
                    };
                    inputDeps = vizJob.Schedule(JobHandle.CombineDependencies(searchHandle, outJobHandle));
                    m_OverlayRenderSystem.AddBufferWriter(inputDeps);

                    // Check if the user clicked (Apply Action)
                    if (applyAction != null && applyAction.WasPressedThisFrame())
                    {
                        inputDeps.Complete();
                        DeleteInRadius(groundPos, Radius, ActiveFilters);
                    }
                }
                finally
                {
                    // Ensure the temporary target list is disposed to prevent memory leaks
                    if (targets.IsCreated) targets.Dispose();
                }
            }
            catch (System.Exception ex)
            {
                RadiusDeleteMod.Log.Warn($"RadiusTool Update Error: {ex.Message}");
            }

            return inputDeps;
        }

        private void DeleteInRadius(float3 center, float radius, DeleteFilters filters)
        {
            // Retrieve spatial search trees for objects and networks
            var objTree = m_ObjectSearchSystem.GetStaticSearchTree(true, out JobHandle objDep);
            var netTree = m_NetSearchSystem.GetNetSearchTree(true, out JobHandle netDep);
            objDep.Complete();
            netDep.Complete();

            NativeList<Entity> rawResults = new NativeList<Entity>(Allocator.Temp);
            
            try 
            {
                // Run the search job synchronously to find all potential entities in range
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

                NativeParallelHashSet<Entity> finalDeleteSet = new NativeParallelHashSet<Entity>(500, Allocator.Temp);
                var edgeLookup = SystemAPI.GetComponentLookup<Game.Net.Edge>(true);
                var connectedEdgesLookup = SystemAPI.GetBufferLookup<Game.Net.ConnectedEdge>(true);
                var subObjectLookup = SystemAPI.GetBufferLookup<Game.Objects.SubObject>(true);

                try
                {
                    // Iterate through raw results to validate and filter them
                    for (int i = 0; i < rawResults.Length; i++)
                    {
                        Entity entity = rawResults[i];
                        if (!EntityManager.Exists(entity)) continue;

                        // Check if entity matches active filters and depth rules
                        if (IsTypeValid(entity, filters, center.y))
                        {
                            finalDeleteSet.Add(entity);

                            // If deleting a Network Node, also find and delete attached Edges
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

                    // Apply the deletion components
                    if (finalDeleteSet.Count() > 0)
                    {
                        // Use buffer for safe deletion (prevents race conditions with simulation threads)
                        EntityCommandBuffer buffer = m_ToolOutputBarrier.CreateCommandBuffer();
                        var deleteArray = finalDeleteSet.ToNativeArray(Allocator.Temp);

                        for (int i = 0; i < deleteArray.Length; i++)
                        {
                            Entity currentEntity = deleteArray[i];

                            // 1. Recursive Neighborhood Update & Orphaned Node Cleanup
                            // If deleting an edge, check if its start/end nodes become orphaned (0 connections).
                            // If not orphaned, mark neighbors as Updated to refresh geometry.
                            if (edgeLookup.TryGetComponent(currentEntity, out var edge))
                            {
                                // Check Start Node
                                if (connectedEdgesLookup.TryGetBuffer(edge.m_Start, out var startEdges))
                                {
                                    if (startEdges.Length == 1 && startEdges[0].m_Edge == currentEntity)
                                        buffer.AddComponent<Game.Common.Deleted>(edge.m_Start); // Orphaned Node cleanup
                                    else
                                        UpdateNeighbors(edge.m_Start, currentEntity, ref buffer, connectedEdgesLookup, edgeLookup);
                                }

                                // Check End Node
                                if (connectedEdgesLookup.TryGetBuffer(edge.m_End, out var endEdges))
                                {
                                    if (endEdges.Length == 1 && endEdges[0].m_Edge == currentEntity)
                                        buffer.AddComponent<Game.Common.Deleted>(edge.m_End); // Orphaned Node cleanup
                                    else
                                        UpdateNeighbors(edge.m_End, currentEntity, ref buffer, connectedEdgesLookup, edgeLookup);
                                }
                            }

                            // 2. Sub-Object Deep Deletion (for Buildings/Extensions)
                            // Ensures props/sub-buildings are removed immediately rather than lingering.
                            if (subObjectLookup.TryGetBuffer(currentEntity, out var subObjects))
                            {
                                foreach (var sub in subObjects)
                                {
                                    if (EntityManager.Exists(sub.m_SubObject))
                                        buffer.AddComponent<Game.Common.Deleted>(sub.m_SubObject);
                                }
                            }

                            // 3. Final Deletion Tag
                            // Tagging with 'Deleted' causes the game systems to remove the entity
                            buffer.AddComponent<Game.Common.Deleted>(currentEntity);
                        }
                    }
                }
                finally
                {
                    if (finalDeleteSet.IsCreated) finalDeleteSet.Dispose();
                }
            }
            finally
            {
                if (rawResults.IsCreated) rawResults.Dispose();
            }
        }

        // Helper to recursively update neighboring network segments to prevent visual ghosting
        private void UpdateNeighbors(Entity node, Entity originalEdge, ref EntityCommandBuffer buffer, BufferLookup<ConnectedEdge> connectedLookup, ComponentLookup<Game.Net.Edge> edgeLookup)
        {
            if (connectedLookup.TryGetBuffer(node, out var connections))
            {
                buffer.AddComponent<Game.Common.Updated>(node);
                foreach (var connection in connections)
                {
                    if (connection.m_Edge != originalEdge && EntityManager.Exists(connection.m_Edge))
                    {
                        buffer.AddComponent<Game.Common.Updated>(connection.m_Edge);
                        // Also update the far end of the neighbor edge to fully refresh the segment
                        if (edgeLookup.TryGetComponent(connection.m_Edge, out var neighborEdge))
                        {
                            buffer.AddComponent<Game.Common.Updated>(neighborEdge.m_Start);
                            buffer.AddComponent<Game.Common.Updated>(neighborEdge.m_End);
                        }
                    }
                }
            }
        }

        private bool IsTypeValid(Entity entity, DeleteFilters active, float surfaceHeight)
        {
            if (EntityManager.HasComponent<Game.Common.Deleted>(entity)) return false;

            // Filter out entities based on elevation (to skip underground objects)
            if (EntityManager.HasComponent<Game.Net.Elevation>(entity))
            {
                var elevation = EntityManager.GetComponentData<Game.Net.Elevation>(entity);
                if (elevation.m_Elevation.x < 0) return false;
            }

            // Depth check: prevent deleting objects deep underground (like subway tunnels)
            if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
            {
                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                if (transform.m_Position.y < surfaceHeight - 18.0f) return false;
            }

            if (EntityManager.HasComponent<Game.Objects.Marker>(entity)) return false;

            // Special handling for Surfaces (Areas)
            if (EntityManager.HasComponent<Game.Areas.Surface>(entity))
            {
                if ((active & DeleteFilters.Surfaces) == 0) return false;
                // Don't delete surfaces owned by a building (e.g., pavement attached to a house)
                if (EntityManager.HasComponent<Game.Common.Owner>(entity))
                {
                    Entity owner = EntityManager.GetComponentData<Game.Common.Owner>(entity).m_Owner;
                    if (EntityManager.HasComponent<Game.Buildings.Building>(owner)) return false;
                }
                return true;
            }

            // Handle objects with an Owner (e.g., props or extensions attached to a building)
            if (EntityManager.HasComponent<Game.Common.Owner>(entity) && EntityManager.GetComponentData<Game.Common.Owner>(entity).m_Owner != Entity.Null)
            {
                // Allow deleting Building Extensions or Service Upgrades individually if the Buildings filter is active.
                // This specifically allows targeting "sub-buildings" without affecting the main parent building.
                if ((active & DeleteFilters.Buildings) != 0 && 
                    (EntityManager.HasComponent<Game.Buildings.Extension>(entity) || EntityManager.HasComponent<Game.Buildings.ServiceUpgrade>(entity)))
                {
                    return true;
                }

                // Protect other owned objects (like props or small decorative pieces) from being deleted individually.
                return false;
            }

            if (!EntityManager.HasComponent<PrefabRef>(entity)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;

            // Check against the ActiveFilters bitmask
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
                // Draw a circle on the overlay system
                m_OverlayBuffer.DrawCircle(m_Color, default, m_Radius / 20f, 0, new float2(0, 1), m_Position, m_Radius * 2f);
            }
        }
    }

    // Job to query the spatial quadtrees for entities within the radius
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
            // Iterate both object and network trees
            m_ObjectSearchTree.Iterate(ref iterator);
            m_NetSearchTree.Iterate(ref iterator);
        }
    }

    // Iterator struct for the QuadTree
    public struct RadiusIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
    {
        public float3 m_Center;
        public float m_RadiusSq;
        public Bounds2 m_SearchBounds;
        public NativeList<Entity> m_Results;
        [ReadOnly] public ComponentLookup<Game.Objects.Transform> m_Transform;

        // Check if a quadtree node overlaps the search area
        public bool Intersect(QuadTreeBoundsXZ bounds)
        {
            float3 min = bounds.m_Bounds.min; float3 max = bounds.m_Bounds.max;
            bool overlapX = (max.x >= m_SearchBounds.min.x) && (min.x <= m_SearchBounds.max.x);
            bool overlapZ = (max.z >= m_SearchBounds.min.y) && (min.z <= m_SearchBounds.max.y);
            return overlapX && overlapZ;
        }

        // Check if specific entity bounds are within the radius
        public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
        {
            if (Intersect(bounds))
            {
                // Calculate the closest point on the entity's bounding box to the center.
                // This ensures large objects are detected even if their center point is outside the radius.
                float3 closestPoint = math.clamp(m_Center, bounds.m_Bounds.min, bounds.m_Bounds.max);
                if (math.distancesq(closestPoint, m_Center) <= m_RadiusSq)
                {
                    m_Results.Add(entity);
                }
            }
        }
    }
}