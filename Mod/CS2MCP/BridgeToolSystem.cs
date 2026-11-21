using System;
using System.Collections.Generic;
using System.Text;
using airimayor.Host;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;
using AgeMask = Game.Tools.AgeMask;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem : ObjectToolBaseSystem
    {
        private enum Stage
        {
            Idle,
            CreateDefinitions,
            Apply,
            Finish,
        }

        private enum OperationKind
        {
            Demolish,
            Upgrade,
            OperationalArea,
            Zone,
            FacilityUpgrade,
            TransitLine,
        }

        private Stage m_Stage = Stage.Idle;
        private OperationKind m_PendingKind;
        private Entity m_PendingPrefabEntity;
        private Entity m_PendingTarget;
        private PrefabBase m_PendingPrefab;
        private string m_PendingLabel;
        private float3 m_PendingPosition;
        private CompositionFlags m_PendingUpgradeFlags;
        private Game.Areas.Node[] m_PendingOperationalAreaNodes;
        private Entity m_PendingOwner;
        private float m_PendingPreviousAreaSurface;
        private int m_PendingPreviousStorageCapacity;
        private float m_PendingTargetArea;
        private string m_PendingOperationalAreaKind;
        private string m_PendingOperationalResource;
        private float m_PendingOperationalResourceAmount;
        private float m_PendingOperationalResourceCoverage;
        private int m_PendingOperationalResourceSampleCount;
        private float m_PendingOperationalResourceMaxConcentration;
        private ZoneType m_PendingZone;
        private float2 m_PendingZoneCenter;
        private float m_PendingZoneRadius;
        private float2 m_PendingZoneSize;
        private float m_PendingZoneRotationDegrees;
        private quaternion m_PendingRotation;
        private BridgeRequest m_PendingRequest;
        private ToolBaseSystem m_PreviousTool;
        private Entity[] m_PendingTransitStops;
        private EntityQuery m_TempRouteQuery;

        private CityConfigurationSystem m_CityConfigurationSystem;

        public override string toolID => "CS2MCP.Bridge";

        public bool IsBusy => m_Stage != Stage.Idle;

        /// <summary>
        /// Resets a bridge operation that has not finished within the watchdog
        /// window. Must be called on the simulation thread.
        /// </summary>
        public void AbortStuckOperation()
        {
            if (m_Stage == Stage.Idle)
            {
                return;
            }
            CompletePending(BridgeResponse.Error(BridgeErrorKind.Timeout,
                "build operation aborted: it did not finish within the bridge watchdog window; " +
                "stage=" + m_Stage));
            applyMode = ApplyMode.None;
            Deactivate();
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_TempRouteQuery = GetEntityQuery(
                ComponentType.ReadOnly<Route>(),
                ComponentType.ReadOnly<Temp>());
        }

        public override PrefabBase GetPrefab()
        {
            return m_PendingPrefab;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            // Never let the game UI select this tool via asset selection.
            return false;
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueueUpgrade(Entity target, string label, CompositionFlags upgradeFlags, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.Upgrade;
            m_PendingTarget = target;
            m_PendingLabel = label;
            m_PendingUpgradeFlags = upgradeFlags;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>
        /// Must be called on the simulation thread. Installs a facility upgrade
        /// through ObjectToolBaseSystem.CreateDefinitions with the parent as
        /// owner — the same job ObjectToolSystem uses for Mode.Upgrade.
        /// </summary>
        public bool TryQueueFacilityUpgrade(
            Entity parent,
            Entity upgradePrefabEntity,
            PrefabBase upgradePrefab,
            float3 position,
            quaternion rotation,
            BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.FacilityUpgrade;
            m_PendingTarget = parent;
            m_PendingPrefabEntity = upgradePrefabEntity;
            m_PendingPrefab = upgradePrefab;
            m_PendingLabel = upgradePrefab != null ? upgradePrefab.name : null;
            m_PendingPosition = position;
            m_PendingRotation = rotation;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>
        /// Must be called on the simulation thread. Creates a passenger line
        /// through RouteTool CreationDefinition + WaypointDefinition so
        /// GenerateRoutesSystem / ApplyRoutesSystem own pathfinding and apply.
        /// </summary>
        public bool TryQueueTransitLine(
            Entity linePrefabEntity,
            PrefabBase linePrefab,
            Entity[] stops,
            BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.TransitLine;
            m_PendingPrefabEntity = linePrefabEntity;
            m_PendingPrefab = linePrefab;
            m_PendingLabel = linePrefab != null ? linePrefab.name : null;
            m_PendingTransitStops = stops;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueueDemolish(Entity target, string label, BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.Demolish;
            m_PendingTarget = target;
            m_PendingLabel = label;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>
        /// Queues a complete replacement polygon for an existing building-owned
        /// operational area. The handler owns the expansion-only geometry checks;
        /// the game's area generation, validation and apply systems own the write.
        /// </summary>
        public bool TryQueueOperationalAreaExpansion(
            Entity area,
            Entity owner,
            Entity prefabEntity,
            PrefabBase prefab,
            Game.Areas.Node[] polygonNodes,
            float previousSurface,
            int previousCapacity,
            float targetArea,
            string areaKind,
            string resource,
            float resourceAmount,
            float resourceCoverage,
            int resourceSampleCount,
            float resourceMaxConcentration,
            BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.OperationalArea;
            m_PendingTarget = area;
            m_PendingOwner = owner;
            m_PendingPrefabEntity = prefabEntity;
            m_PendingPrefab = prefab;
            m_PendingOperationalAreaNodes = polygonNodes;
            m_PendingPreviousAreaSurface = previousSurface;
            m_PendingPreviousStorageCapacity = previousCapacity;
            m_PendingTargetArea = targetArea;
            m_PendingOperationalAreaKind = areaKind;
            m_PendingOperationalResource = resource;
            m_PendingOperationalResourceAmount = resourceAmount;
            m_PendingOperationalResourceCoverage = resourceCoverage;
            m_PendingOperationalResourceSampleCount = resourceSampleCount;
            m_PendingOperationalResourceMaxConcentration = resourceMaxConcentration;
            m_PendingRequest = request;
            Activate();
            return true;
        }

        /// <summary>Queues zone-cell painting for ToolUpdate, where ToolOutputBarrier is
        /// open and the game's zone cell-check lifecycle can observe Updated.
        /// Must be called on the simulation thread.</summary>
        public bool TryQueueZone(
            ZoneType zone,
            string label,
            float2 center,
            float2 size,
            float rotationDegrees,
            BridgeRequest request)
        {
            if (m_Stage != Stage.Idle)
            {
                return false;
            }
            m_PendingKind = OperationKind.Zone;
            m_PendingZone = zone;
            m_PendingLabel = label;
            m_PendingZoneCenter = center;
            m_PendingZoneSize = size;
            m_PendingZoneRotationDegrees = rotationDegrees;
            m_PendingZoneRadius = math.length(size * 0.5f);
            m_PendingRequest = request;
            Activate();
            return true;
        }

        private void Activate()
        {
            m_Stage = Stage.CreateDefinitions;
            m_PreviousTool = m_ToolSystem.activeTool;
            if (m_PreviousTool != null && m_PreviousTool.toolID == "airimayor.PointedPlace")
            {
                m_PreviousTool = m_DefaultToolSystem;
            }
            m_ToolSystem.activeTool = this;
        }

        [Preserve]
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            try
            {
                switch (m_Stage)
                {
                    case Stage.CreateDefinitions:
                        applyMode = ApplyMode.Clear;
                        switch (m_PendingKind)
                        {
                            case OperationKind.Demolish:
                                CreateModifyDefinitions(CreationFlags.Delete, default);
                                break;
                            case OperationKind.Upgrade:
                                CreateModifyDefinitions(CreationFlags.Upgrade, m_PendingUpgradeFlags);
                                break;
                            case OperationKind.OperationalArea:
                                CreateOperationalAreaDefinition();
                                break;
                            case OperationKind.Zone:
                                ApplyZoneCells();
                                break;
                            case OperationKind.FacilityUpgrade:
                                CreateFacilityUpgradeDefinitions();
                                break;
                            case OperationKind.TransitLine:
                                BuildTransitLineDefinitions();
                                break;
                        }
                        m_Stage = m_PendingKind == OperationKind.Zone ? Stage.Finish : Stage.Apply;
                        break;

                    case Stage.Apply:
                        if (m_PendingKind == OperationKind.TransitLine)
                        {
                            UpdateTransitLineApply();
                            break;
                        }
                        if (GetAllowApply())
                        {
                            applyMode = ApplyMode.Apply;
                            CompletePending(BuildSuccessResponse());
                        }
                        else
                        {
                            applyMode = ApplyMode.Clear;
                            CompletePending(BridgeResponse.Error(BridgeErrorKind.Conflict, DescribeValidationBlock()));
                        }
                        m_Stage = Stage.Finish;
                        break;

                    case Stage.Finish:
                        applyMode = ApplyMode.None;
                        Deactivate();
                        break;

                    default:
                        applyMode = ApplyMode.None;
                        break;
                }
            }
            catch (Exception e)
            {
                AgentTimeline.Warn("tool", $"BridgeToolSystem error in stage {m_Stage}: {e}");
                CompletePending(BridgeResponse.Error(BridgeErrorKind.Internal, $"tool operation failed: {e.GetType().Name}: {e.Message}"));
                applyMode = ApplyMode.None;
                Deactivate();
            }
            return inputDeps;
        }

        private void ApplyZoneCells()
        {
            const float cellSize = 8f;
            int cellsChanged = 0;
            int blocksTouched = 0;
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            using (EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Block>(),
                    ComponentType.ReadOnly<Cell>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            }))
            using (NativeArray<Entity> blocks = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity blockEntity in blocks)
                {
                    Block block = EntityManager.GetComponentData<Block>(blockEntity);
                    float blockExtent = cellSize * (math.cmax(block.m_Size) + 1) * 0.71f;
                    if (math.distance(block.m_Position.xz, m_PendingZoneCenter) > m_PendingZoneRadius + blockExtent)
                    {
                        continue;
                    }

                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
                    int blockCells = 0;
                    for (int cellZ = 0; cellZ < block.m_Size.y; cellZ++)
                    {
                        for (int cellX = 0; cellX < block.m_Size.x; cellX++)
                        {
                            int index = cellZ * block.m_Size.x + cellX;
                            if (index >= cells.Length)
                            {
                                continue;
                            }
                            Cell cell = cells[index];
                            if ((cell.m_State & CellFlags.Visible) == 0
                                || (cell.m_State & (CellFlags.Blocked | CellFlags.Overridden)) != 0
                                || cell.m_Zone.Equals(m_PendingZone))
                            {
                                continue;
                            }
                            float3 cellPosition = ZoneUtils.GetCellPosition(block, new int2(cellX, cellZ));
                            if (!PendingZoneContains(cellPosition.xz))
                            {
                                continue;
                            }
                            cell.m_Zone = m_PendingZone;
                            cells[index] = cell;
                            blockCells++;
                        }
                    }

                    if (blockCells == 0)
                    {
                        continue;
                    }
                    cellsChanged += blockCells;
                    blocksTouched++;
                    if (!EntityManager.HasComponent<Updated>(blockEntity))
                    {
                        commandBuffer.AddComponent<Updated>(blockEntity);
                    }
                }
            }

            var payload = new Dictionary<string, object>
            {
                ["zone"] = m_PendingLabel,
                ["center"] = new { x = m_PendingZoneCenter.x, z = m_PendingZoneCenter.y },
                ["cellsChanged"] = cellsChanged,
                ["blocksTouched"] = blocksTouched,
                ["width"] = m_PendingZoneSize.x,
                ["depth"] = m_PendingZoneSize.y,
                ["rotation"] = m_PendingZoneRotationDegrees,
                ["note"] = cellsChanged == 0
                    ? "no zonable cells found in shape - zone cells only exist along roads and must be unoccupied"
                    : "zoned; buildings grow after set_simulation advance",
            };
            CompletePending(BridgeResponse.Json(payload));
        }

        private bool PendingZoneContains(float2 position)
        {
            float2 delta = position - m_PendingZoneCenter;
            float radians = math.radians(m_PendingZoneRotationDegrees);
            float sine = math.sin(radians);
            float cosine = math.cos(radians);
            float2 local = new float2(
                cosine * delta.x + sine * delta.y,
                -sine * delta.x + cosine * delta.y);
            float2 halfSize = m_PendingZoneSize * 0.5f;
            return math.abs(local.x) <= halfSize.x && math.abs(local.y) <= halfSize.y;
        }

        private BridgeResponse BuildSuccessResponse()
        {
            switch (m_PendingKind)
            {
                case OperationKind.Demolish:
                    return BridgeResponse.Json(new
                    {
                        demolished = true,
                        prefab = m_PendingLabel,
                        entity = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                        note = "demolished; connected nodes and lanes are cleaned up by the game",
                    });
                case OperationKind.Upgrade:
                    return BridgeResponse.Json(new
                    {
                        roadFeaturesSet = true,
                        canonicalTool = "set_road_features",
                        prefab = m_PendingLabel,
                        entity = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                        note = "road features applied to the segment",
                    });
                case OperationKind.FacilityUpgrade:
                    return BridgeResponse.Json(new
                    {
                        applied = true,
                        upgrade = m_PendingLabel,
                        enabled = true,
                        installed = true,
                        entity = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                        position = new
                        {
                            x = m_PendingPosition.x,
                            y = m_PendingPosition.y,
                            z = m_PendingPosition.z,
                        },
                        note = "upgrade installed on the parent building",
                    });
                case OperationKind.TransitLine:
                    return BridgeResponse.Json(new
                    {
                        created = true,
                        prefab = m_PendingLabel,
                        stopCount = m_PendingTransitStops != null ? m_PendingTransitStops.Length : 0,
                        note = "passenger line created; vehicles spawn from depots. Call list_transit_lines to read the new line.",
                    });
                case OperationKind.OperationalArea:
                    return BridgeResponse.Json(new
                    {
                        expanded = true,
                        area = new { index = m_PendingTarget.Index, version = m_PendingTarget.Version },
                        prefab = m_PendingPrefab != null ? m_PendingPrefab.name : null,
                        targetAreaM2 = (float)Math.Round(m_PendingTargetArea, 1),
                        previousSurfaceAreaM2 = (float)Math.Round(m_PendingPreviousAreaSurface, 1),
                        previousCapacity = m_PendingPreviousStorageCapacity >= 0
                            ? (int?)m_PendingPreviousStorageCapacity
                            : null,
                        resource = m_PendingOperationalResource == null
                            ? null
                            : new
                            {
                                type = m_PendingOperationalResource,
                                estimatedRemainingAmount = (float)Math.Round(m_PendingOperationalResourceAmount, 1),
                                coverage = m_PendingOperationalResource == Game.Areas.MapFeature.Forest.ToString()
                                    ? (float?)null
                                    : (float)Math.Round(m_PendingOperationalResourceCoverage, 3),
                                treeCount = m_PendingOperationalResource == Game.Areas.MapFeature.Forest.ToString()
                                    ? (int?)m_PendingOperationalResourceSampleCount
                                    : null,
                                maxConcentration = m_PendingOperationalResource == Game.Areas.MapFeature.Forest.ToString()
                                    ? (float?)Math.Round(m_PendingOperationalResourceMaxConcentration, 3)
                                    : null,
                            },
                        nodes = m_PendingOperationalAreaNodes != null ? m_PendingOperationalAreaNodes.Length : 0,
                        note = $"{m_PendingOperationalAreaKind} operational area expanded; call inspect_operational_area to verify resource, capacity and ownership",
                    });
                default:
                    throw new InvalidOperationException($"No result for {m_PendingKind}.");
            }
        }

        private void CompletePending(BridgeResponse response)
        {
            m_PendingRequest?.Complete(response);
            m_PendingRequest = null;
        }

        /// <summary>
        /// Pull ErrorType values off temp entities via their notification icons
        /// (IconElement → PrefabRef → ToolErrorData), so the agent sees why
        /// GetAllowApply failed instead of a generic overlap/water list.
        /// </summary>
        private string DescribeValidationBlock()
        {
            var reasons = new List<string>();
            using (NativeArray<Entity> entities = m_ErrorQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    CollectErrorReasons(entities[i], reasons);
                }
            }

            var message = new StringBuilder("operation blocked by game validation");
            if (reasons.Count > 0)
            {
                var unique = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string reason in reasons)
                {
                    if (seen.Add(reason))
                    {
                        unique.Add(reason);
                    }
                }
                message.Append(": ").Append(string.Join("; ", unique));
            }
            else
            {
                message.Append(" (no ErrorType icons on temp entities)");
            }

            message.Append("; try a different position or target");
            return message.ToString();
        }

        private void CollectErrorReasons(Entity entity, List<string> reasons)
        {
            if (EntityManager.HasComponent<PrefabRef>(entity))
            {
                TryAddToolError(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab, reasons);
            }

            if (!EntityManager.HasBuffer<IconElement>(entity))
            {
                return;
            }

            DynamicBuffer<IconElement> icons = EntityManager.GetBuffer<IconElement>(entity, true);
            for (int i = 0; i < icons.Length; i++)
            {
                Entity icon = icons[i].m_Icon;
                if (icon == Entity.Null || !EntityManager.Exists(icon))
                {
                    continue;
                }
                if (EntityManager.HasComponent<PrefabRef>(icon))
                {
                    TryAddToolError(EntityManager.GetComponentData<PrefabRef>(icon).m_Prefab, reasons);
                }
            }
        }

        private void TryAddToolError(Entity prefab, List<string> reasons)
        {
            if (prefab == Entity.Null || !EntityManager.HasComponent<ToolErrorData>(prefab))
            {
                return;
            }

            ErrorType error = EntityManager.GetComponentData<ToolErrorData>(prefab).m_Error;
            if (error == ErrorType.None)
            {
                return;
            }

            reasons.Add(DescribeErrorType(error));
        }

        private static string DescribeErrorType(ErrorType error)
        {
            switch (error)
            {
                case ErrorType.OverlapExisting:
                    return "OverlapExisting (overlaps building/prop/network)";
                case ErrorType.InvalidShape:
                    return "InvalidShape (geometry not valid here)";
                case ErrorType.NotEnoughMoney:
                    return "NotEnoughMoney";
                case ErrorType.LongDistance:
                    return "LongDistance (too far / split into shorter segments)";
                case ErrorType.TightCurve:
                    return "TightCurve (curve too sharp)";
                case ErrorType.InWater:
                    return "InWater (placement is invalid at this water depth)";
                case ErrorType.NotOnShoreline:
                    return "NotOnShoreline (move the placement seed closer to a wet/dry shoreline transition)";
                case ErrorType.ExceedsCityLimits:
                    return "ExceedsCityLimits (outside owned map tiles)";
                case ErrorType.AlreadyExists:
                    return "AlreadyExists";
                case ErrorType.ShortDistance:
                    return "ShortDistance (endpoints too close)";
                case ErrorType.LowElevation:
                    return "LowElevation";
                case ErrorType.SteepSlope:
                    return "SteepSlope";
                case ErrorType.SmallArea:
                    return "SmallArea";
                case ErrorType.ExceedsLotLimits:
                    return "ExceedsLotLimits";
                case ErrorType.OnFire:
                    return "OnFire";
                default:
                    return error.ToString();
            }
        }

        private void Deactivate()
        {
            m_Stage = Stage.Idle;
            m_PendingRequest = null;
            m_PendingPrefab = null;
            m_PendingPrefabEntity = Entity.Null;
            m_PendingOwner = Entity.Null;
            m_PendingOperationalAreaNodes = null;
            m_PendingOperationalAreaKind = null;
            m_PendingOperationalResource = null;
            m_PendingTransitStops = null;
            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_PreviousTool != null ? m_PreviousTool : m_DefaultToolSystem;
            }
            m_PreviousTool = null;
        }

        /// <summary>
        /// Creates a modify definition for the pending target, faithfully
        /// mirroring BulldozeToolSystem.AddEntity (CreationDefinition with
        /// m_Original + flags plus a NetCourse/ObjectDefinition describing the
        /// original). Used for Delete (bulldoze) and Upgrade (road upgrades:
        /// grass/trees/lighting...). The game's generate/apply systems handle
        /// all related cleanup/recreation — nodes, zone blocks, lanes — which a
        /// raw component edit would skip (and skipping corrupts state).
        /// </summary>
        private void CreateModifyDefinitions(CreationFlags flags, CompositionFlags upgrades)
        {
            Entity target = m_PendingTarget;
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity e = commandBuffer.CreateEntity();
            var definition = new CreationDefinition
            {
                m_Original = target,
                m_Flags = flags,
            };
            commandBuffer.AddComponent(e, default(Updated));
            if (upgrades != default(CompositionFlags))
            {
                commandBuffer.AddComponent(e, new Game.Net.Upgraded { m_Flags = upgrades });
            }

            if (EntityManager.HasComponent<Game.Net.Edge>(target))
            {
                Game.Net.Edge edge = EntityManager.GetComponentData<Game.Net.Edge>(target);
                NetCourse course = default;
                course.m_Curve = EntityManager.GetComponentData<Game.Net.Curve>(target).m_Bezier;
                course.m_Length = MathUtils.Length(course.m_Curve);
                course.m_FixedIndex = EntityManager.HasComponent<Game.Net.Fixed>(target)
                    ? EntityManager.GetComponentData<Game.Net.Fixed>(target).m_Index
                    : -1;
                course.m_StartPosition.m_Entity = edge.m_Start;
                course.m_StartPosition.m_Position = course.m_Curve.a;
                course.m_StartPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve));
                course.m_StartPosition.m_CourseDelta = 0f;
                course.m_EndPosition.m_Entity = edge.m_End;
                course.m_EndPosition.m_Position = course.m_Curve.d;
                course.m_EndPosition.m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve));
                course.m_EndPosition.m_CourseDelta = 1f;
                commandBuffer.AddComponent(e, course);
            }
            else if (EntityManager.HasComponent<Transform>(target))
            {
                Transform transform = EntityManager.GetComponentData<Transform>(target);
                var objectDefinition = new ObjectDefinition
                {
                    m_Position = transform.m_Position,
                    m_Rotation = transform.m_Rotation,
                    m_Probability = 100,
                    m_PrefabSubIndex = -1,
                    m_LocalPosition = transform.m_Position,
                    m_LocalRotation = transform.m_Rotation,
                };
                if (EntityManager.HasComponent<Game.Objects.Elevation>(target))
                {
                    Game.Objects.Elevation elevation = EntityManager.GetComponentData<Game.Objects.Elevation>(target);
                    objectDefinition.m_Elevation = elevation.m_Elevation;
                    objectDefinition.m_ParentMesh = Game.Objects.ObjectUtils.GetSubParentMesh(elevation.m_Flags);
                }
                else
                {
                    objectDefinition.m_ParentMesh = -1;
                }
                commandBuffer.AddComponent(e, objectDefinition);
            }
            else if (EntityManager.HasBuffer<Game.Areas.Node>(target))
            {
                DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(target, isReadOnly: true);
                commandBuffer.AddBuffer<Game.Areas.Node>(e).CopyFrom(nodes.AsNativeArray());
            }

            commandBuffer.AddComponent(e, definition);
        }

        /// <summary>
        /// Mirrors AreaToolSystem's existing-area edit definition: the original
        /// area, its real owner, existing prefab, Relocate flag, and full Node
        /// buffer travel through Generate/Validation/ApplyAreasSystem together.
        /// </summary>
        private void CreateOperationalAreaDefinition()
        {
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity definitionEntity = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(definitionEntity, new CreationDefinition
            {
                m_Original = m_PendingTarget,
                m_Owner = m_PendingOwner,
                m_Prefab = m_PendingPrefabEntity,
                m_Flags = CreationFlags.Relocate,
            });
            commandBuffer.AddComponent(definitionEntity, default(Updated));
            DynamicBuffer<Game.Areas.Node> nodes =
                commandBuffer.AddBuffer<Game.Areas.Node>(definitionEntity);
            foreach (Game.Areas.Node node in m_PendingOperationalAreaNodes)
            {
                nodes.Add(node);
            }
        }

        /// <summary>
        /// <summary>
        /// Schedules the native ObjectToolBaseSystem definition job with the
        /// parent building as owner. Completes immediately so the following
        /// Apply frame sees the same Temp entities as ObjectToolSystem.Mode.Upgrade.
        /// </summary>
        private void CreateFacilityUpgradeDefinitions()
        {
            var controlPoints = new NativeList<ControlPoint>(1, Allocator.TempJob);
            try
            {
                controlPoints.Add(new ControlPoint
                {
                    m_Position = m_PendingPosition,
                    m_HitPosition = m_PendingPosition,
                    m_Rotation = m_PendingRotation,
                });
                NativeReference<AttachmentData> attachmentPrefab = default;
                JobHandle handle = base.CreateDefinitions(
                    m_PendingPrefabEntity,
                    Entity.Null,
                    Entity.Null,
                    m_PendingTarget,
                    Entity.Null,
                    Entity.Null,
                    m_CityConfigurationSystem.defaultTheme,
                    controlPoints,
                    attachmentPrefab,
                    false,
                    m_CityConfigurationSystem.leftHandTraffic,
                    false,
                    false,
                    0f,
                    0f,
                    0f,
                    0f,
                    0f,
                    RandomSeed.Next(),
                    Snap.None,
                    AgeMask.Mature,
                    false,
                    default(PlacementOverrides),
                    default(JobHandle));
                handle.Complete();
            }
            finally
            {
                if (controlPoints.IsCreated)
                {
                    controlPoints.Dispose();
                }
            }
        }

        private void UpdateTransitLineApply()
        {
            if (!TransitLinePathfindReady())
            {
                applyMode = ApplyMode.None;
                return;
            }
            if (GetAllowApply())
            {
                applyMode = ApplyMode.Apply;
                CompletePending(BuildSuccessResponse());
            }
            else
            {
                applyMode = ApplyMode.Clear;
                CompletePending(BridgeResponse.Error(BridgeErrorKind.Conflict, DescribeValidationBlock()));
            }
            m_Stage = Stage.Finish;
        }

        private bool TransitLinePathfindReady()
        {
            if (m_TempRouteQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }
            using (NativeArray<Entity> routes = m_TempRouteQuery.ToEntityArray(Allocator.Temp))
            {
                Entity route = routes[0];
                if (!EntityManager.HasComponent<Route>(route)
                    || (EntityManager.GetComponentData<Route>(route).m_Flags & RouteFlags.Complete) == 0
                    || !EntityManager.HasBuffer<RouteWaypoint>(route)
                    || !EntityManager.HasBuffer<RouteSegment>(route))
                {
                    return false;
                }
                DynamicBuffer<RouteWaypoint> waypoints =
                    EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);
                DynamicBuffer<RouteSegment> segments =
                    EntityManager.GetBuffer<RouteSegment>(route, isReadOnly: true);
                if (waypoints.Length == 0 || segments.Length == 0)
                {
                    return false;
                }
                for (int i = 0; i < segments.Length; i++)
                {
                    Entity segment = segments[i].m_Segment;
                    if (!EntityManager.HasComponent<PathTargets>(segment)
                        || !EntityManager.HasComponent<PathInformation>(segment))
                    {
                        return false;
                    }
                    PathTargets targets = EntityManager.GetComponentData<PathTargets>(segment);
                    RouteWaypoint start = waypoints[i];
                    RouteWaypoint end = waypoints[i + 1 >= waypoints.Length ? 0 : i + 1];
                    if (!EntityManager.HasComponent<Position>(start.m_Waypoint)
                        || !EntityManager.HasComponent<Position>(end.m_Waypoint))
                    {
                        return false;
                    }
                    float3 startPosition = EntityManager.GetComponentData<Position>(start.m_Waypoint).m_Position;
                    float3 endPosition = EntityManager.GetComponentData<Position>(end.m_Waypoint).m_Position;
                    if (math.distancesq(targets.m_ReadyStartPosition, startPosition) >= 1f
                        || math.distancesq(targets.m_ReadyEndPosition, endPosition) >= 1f)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Mirrors RouteToolSystem.CreateDefinitionsJob for a new complete
        /// passenger route: CreationDefinition + ColorDefinition + closed
        /// WaypointDefinition loop attached to existing TransportStop entities.
        /// </summary>
        private void BuildTransitLineDefinitions()
        {
            if (m_PendingTransitStops == null || m_PendingTransitStops.Length < 2)
            {
                return;
            }
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity definition = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(definition, new CreationDefinition
            {
                m_Prefab = m_PendingPrefabEntity,
            });
            UnityEngine.Color32 color = UnityEngine.Color.magenta;
            if (m_PendingPrefab is RoutePrefab routePrefab)
            {
                color = routePrefab.color;
            }
            commandBuffer.AddComponent(definition, new ColorDefinition { m_Color = color });
            commandBuffer.AddComponent(definition, default(Updated));
            DynamicBuffer<WaypointDefinition> waypoints = commandBuffer.AddBuffer<WaypointDefinition>(definition);
            for (int i = 0; i < m_PendingTransitStops.Length; i++)
            {
                waypoints.Add(MakeStopWaypoint(m_PendingTransitStops[i]));
            }
            waypoints.Add(MakeStopWaypoint(m_PendingTransitStops[0]));
        }

        private WaypointDefinition MakeStopWaypoint(Entity stop)
        {
            Transform transform = EntityManager.GetComponentData<Transform>(stop);
            return new WaypointDefinition(transform.m_Position)
            {
                m_Connection = stop,
            };
        }
    }
}
