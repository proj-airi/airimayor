using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private const float kFootprintCellSize = 8f;
        private const float kMapHalfSize = 7168f;
        private const float kMapTileSize = 623.304347826f;

        private EntityQuery m_BuildingPrefabQuery;
        private bool m_BuildingPrefabQueryCreated;
        private EntityQuery m_RoadPrefabQuery;
        private bool m_RoadPrefabQueryCreated;
        private EntityQuery m_PlacedBuildingQuery;
        private bool m_PlacedBuildingQueryCreated;
        private EntityQuery m_PlacedRoadQuery;
        private bool m_PlacedRoadQueryCreated;
        private EntityQuery m_NetPrefabQuery;
        private bool m_NetPrefabQueryCreated;
        private EntityQuery m_TreePrefabQuery;
        private bool m_TreePrefabQueryCreated;
        private EntityQuery m_CurbObjectPrefabQuery;
        private bool m_CurbObjectPrefabQueryCreated;

        private EntityQuery NetPrefabQuery
        {
            get
            {
                if (!m_NetPrefabQueryCreated)
                {
                    m_NetPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<NetGeometryData>());
                    m_NetPrefabQueryCreated = true;
                }
                return m_NetPrefabQuery;
            }
        }

        private EntityQuery TreePrefabQuery
        {
            get
            {
                if (!m_TreePrefabQueryCreated)
                {
                    m_TreePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<TreeData>());
                    m_TreePrefabQueryCreated = true;
                }
                return m_TreePrefabQuery;
            }
        }

        private EntityQuery CurbObjectPrefabQuery
        {
            get
            {
                if (!m_CurbObjectPrefabQueryCreated)
                {
                    m_CurbObjectPrefabQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<PrefabData>(),
                            ComponentType.ReadOnly<NetObjectData>(),
                            ComponentType.ReadOnly<PlaceableObjectData>(),
                            ComponentType.ReadOnly<UIObjectData>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<BuildingData>(),
                            ComponentType.ReadOnly<ServiceUpgradeData>(),
                        },
                    });
                    m_CurbObjectPrefabQueryCreated = true;
                }
                return m_CurbObjectPrefabQuery;
            }
        }

        private EntityQuery BuildingPrefabQuery
        {
            get
            {
                if (!m_BuildingPrefabQueryCreated)
                {
                    m_BuildingPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<BuildingData>());
                    m_BuildingPrefabQueryCreated = true;
                }
                return m_BuildingPrefabQuery;
            }
        }

        private EntityQuery RoadPrefabQuery
        {
            get
            {
                if (!m_RoadPrefabQueryCreated)
                {
                    m_RoadPrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<RoadData>());
                    m_RoadPrefabQueryCreated = true;
                }
                return m_RoadPrefabQuery;
            }
        }

        private EntityQuery PlacedBuildingQuery
        {
            get
            {
                if (!m_PlacedBuildingQueryCreated)
                {
                    m_PlacedBuildingQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Buildings.Building>(),
                            ComponentType.ReadOnly<Transform>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_PlacedBuildingQueryCreated = true;
                }
                return m_PlacedBuildingQuery;
            }
        }

        private EntityQuery PlacedRoadQuery
        {
            get
            {
                if (!m_PlacedRoadQueryCreated)
                {
                    m_PlacedRoadQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Net.Edge>(),
                            ComponentType.ReadOnly<Game.Net.Curve>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                            ComponentType.ReadOnly<Game.Common.Owner>(),
                        },
                    });
                    m_PlacedRoadQueryCreated = true;
                }
                return m_PlacedRoadQuery;
            }
        }

        private BridgeResponse GetPrefabs(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            string category = request.Query.TryGetValue("category", out string rawCategory)
                ? rawCategory.ToLowerInvariant()
                : "building";
            request.Query.TryGetValue("query", out string search);
            request.Query.TryGetValue("role", out string requestedRole);
            requestedRole = requestedRole?.Trim().ToLowerInvariant();
            var queries = new List<EntityQuery>(2);
            bool primaryIsCurb = false;
            switch (category)
            {
                case "building":
                    queries.Add(BuildingPrefabQuery);
                    break;
                case "road":
                    queries.Add(RoadPrefabQuery);
                    break;
                case "net":
                    queries.Add(NetPrefabQuery);
                    break;
                case "tree":
                    queries.Add(TreePrefabQuery);
                    break;
                case "object":
                    queries.Add(CurbObjectPrefabQuery);
                    primaryIsCurb = true;
                    break;
                default:
                    return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "category must be 'building', 'road', 'net' (all networks incl. pipes/power/tracks/paths), 'tree', or 'object' (roadside objects)");
            }
            if (!primaryIsCurb
                && category == "building"
                && (requestedRole == "transport" || requestedRole == "post"))
            {
                queries.Add(CurbObjectPrefabQuery);
            }

            if (string.IsNullOrEmpty(search) && string.IsNullOrEmpty(requestedRole))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    "provide ?role=<prefab role> and/or ?query=<name substring>");
            }
            if (!string.IsNullOrEmpty(requestedRole) && !kPrefabRoles.Contains(requestedRole))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    $"unknown prefab role '{requestedRole}'; valid: {string.Join(", ", kPrefabRoles)}");
            }
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 64) : 16;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var operationalAreas = new PrefabOperationalAreaClassifier(EntityManager);
            var results = new List<object>();
            int total = 0;
            for (int batch = 0; batch < queries.Count; batch++)
            {
                bool curbBatch = primaryIsCurb || batch > 0;
                using (NativeArray<Entity> entities = queries[batch].ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity entity in entities)
                    {
                        if (curbBatch)
                        {
                            if (!IsMenuCurbObject(entity))
                            {
                                continue;
                            }
                        }
                        else if (category == "building" && !IsIndependentBuildingPrefab(entity))
                        {
                            continue;
                        }
                        PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(entity);
                        if (prefab == null)
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(search)
                            && prefab.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        List<string> roles = GetPrefabRoles(entity, operationalAreas);
                        if (!string.IsNullOrEmpty(requestedRole) && !roles.Contains(requestedRole))
                        {
                            continue;
                        }
                        total++;
                        if (results.Count < limit)
                        {
                            object lotSize = null;
                            object footprintMeters = null;
                            float? widthM = null;
                            if (EntityManager.HasComponent<BuildingData>(entity))
                            {
                                int2 lot = EntityManager.GetComponentData<BuildingData>(entity).m_LotSize;
                                lotSize = new { x = lot.x, z = lot.y };
                                footprintMeters = new
                                {
                                    x = (float)Math.Round(lot.x * kFootprintCellSize, 1),
                                    z = (float)Math.Round(lot.y * kFootprintCellSize, 1),
                                };
                            }
                            else if (EntityManager.HasComponent<ObjectGeometryData>(entity))
                            {
                                float3 size = EntityManager.GetComponentData<ObjectGeometryData>(entity).m_Size;
                                footprintMeters = new
                                {
                                    x = (float)Math.Round(size.x, 1),
                                    z = (float)Math.Round(size.z, 1),
                                };
                            }
                            if (EntityManager.HasComponent<NetGeometryData>(entity))
                            {
                                widthM = EntityManager.GetComponentData<NetGeometryData>(entity).m_DefaultWidth;
                            }
                            object road = null;
                            if (EntityManager.HasComponent<RoadData>(entity))
                            {
                                RoadPrefabFacts facts = ReadRoadPrefabFacts(entity, prefabSystem, prefab.name);
                                road = new
                                {
                                    roadClass = facts.RoadClass,
                                    speedKmh = facts.SpeedKmh,
                                    carLanes = facts.CarLanes,
                                    highwayRules = facts.HighwayRules,
                                    zonable = facts.Zonable,
                                };
                            }
                            results.Add(new
                            {
                                name = prefab.name,
                                type = prefab.GetType().Name,
                                roles,
                                locked = IsLocked(entity),
                                lotSize,
                                footprintMeters,
                                widthM,
                                road,
                            });
                        }
                    }
                }
            }

            return BridgeResponse.Json(new
            {
                category,
                role = requestedRole,
                totalMatches = total,
                returned = results.Count,
                note = "Locked assets need milestone progress.",
                stalenessWarning = LockStalenessWarning,
                prefabs = results,
            });
        }

        private static readonly HashSet<string> kPrefabRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "power",
            "water",
            "sewage",
            "garbage",
            "healthcare",
            "fire",
            "police",
            "education",
            "transport",
            "post",
            "telecom",
            "specialized-industry",
        };

        private List<string> GetPrefabRoles(
            Entity prefab,
            PrefabOperationalAreaClassifier operationalAreas)
        {
            var roles = new List<string>();
            AddPrefabRole<PowerPlantData>(prefab, "power", roles);
            AddPrefabRole<PowerLineData>(prefab, "power", roles);
            AddPrefabRole<WaterPumpingStationData>(prefab, "water", roles);
            AddPrefabRole<WaterTowerData>(prefab, "water", roles);
            AddPrefabRole<WaterPipeConnectionData>(prefab, "water", roles);
            AddPrefabRole<SewageOutletData>(prefab, "sewage", roles);
            AddPrefabRole<GarbageFacilityData>(prefab, "garbage", roles);
            AddPrefabRole<HospitalData>(prefab, "healthcare", roles);
            AddPrefabRole<FireStationData>(prefab, "fire", roles);
            AddPrefabRole<PoliceStationData>(prefab, "police", roles);
            AddPrefabRole<SchoolData>(prefab, "education", roles);
            AddPrefabRole<TransportDepotData>(prefab, "transport", roles);
            AddPrefabRole<TransportStationData>(prefab, "transport", roles);
            if (EntityManager.HasComponent<TransportStopData>(prefab))
            {
                TransportType stopType =
                    EntityManager.GetComponentData<TransportStopData>(prefab).m_TransportType;
                AddRole(
                    stopType == TransportType.Post ? "post" : "transport",
                    roles);
            }
            AddPrefabRole<PostFacilityData>(prefab, "post", roles);
            AddPrefabRole<TelecomFacilityData>(prefab, "telecom", roles);
            if (operationalAreas.DeclaresExtractorArea(prefab))
            {
                roles.Add("specialized-industry");
            }
            return roles;
        }

        private void AddPrefabRole<T>(Entity prefab, string role, List<string> roles)
            where T : unmanaged, IComponentData
        {
            if (EntityManager.HasComponent<T>(prefab))
            {
                AddRole(role, roles);
            }
        }

        private static void AddRole(string role, List<string> roles)
        {
            if (!roles.Contains(role))
            {
                roles.Add(role);
            }
        }

        private static float3[] SampleNetworkCurve(Game.Net.Curve curve)
        {
            int segments = math.clamp((int)math.ceil(curve.m_Length / 8f), 1, 192);
            var points = new float3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                points[i] = BezierPoint(curve.m_Bezier, i / (float)segments);
            }
            return points;
        }

        private static readonly Dictionary<string, (Game.Prefabs.CompositionFlags.General general, Game.Prefabs.CompositionFlags.Side side)> kUpgradeNames =
            new Dictionary<string, (Game.Prefabs.CompositionFlags.General, Game.Prefabs.CompositionFlags.Side)>(StringComparer.OrdinalIgnoreCase)
            {
                ["grass"] = (default, Game.Prefabs.CompositionFlags.Side.PrimaryBeautification),
                ["trees"] = (default, Game.Prefabs.CompositionFlags.Side.SecondaryBeautification),
                ["wideSidewalk"] = (default, Game.Prefabs.CompositionFlags.Side.WideSidewalk),
                ["soundBarrier"] = (default, Game.Prefabs.CompositionFlags.Side.SoundBarrier),
                ["parking"] = (default, Game.Prefabs.CompositionFlags.Side.ParkingSpaces),
                ["lighting"] = (Game.Prefabs.CompositionFlags.General.Lighting, default),
                ["medianGrass"] = (Game.Prefabs.CompositionFlags.General.PrimaryMiddleBeautification, default),
                ["medianTrees"] = (Game.Prefabs.CompositionFlags.General.SecondaryMiddleBeautification, default),
            };

        private BridgeResponse SetRoadFeatures(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "provide ?index=&version= of a road segment from list_networks");
            }
            if (!request.Query.TryGetValue("upgrades", out string upgradesRaw) || string.IsNullOrEmpty(upgradesRaw))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    $"provide ?upgrades=<comma list>: {string.Join(", ", kUpgradeNames.Keys)}");
            }

            if (!TryResolveExistingEntity(index, version, out Entity entity)
                || !EntityManager.HasComponent<Game.Net.Edge>(entity))
            {
                return BridgeResponse.Error(BridgeErrorKind.NotFound, $"entity {index}:{version} is not an existing road segment");
            }

            string side = request.Query.TryGetValue("side", out string rawSide) ? rawSide.ToLowerInvariant() : "both";
            if (side != "left" && side != "right" && side != "both")
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "side must be 'left', 'right' or 'both'");
            }
            Game.Prefabs.CompositionFlags flags = default;
            foreach (string name in upgradesRaw.Split(','))
            {
                string trimmed = name.Trim();
                if (!kUpgradeNames.TryGetValue(trimmed, out (Game.Prefabs.CompositionFlags.General general, Game.Prefabs.CompositionFlags.Side side) mapped))
                {
                    return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, $"unknown road feature '{trimmed}'; valid: {string.Join(", ", kUpgradeNames.Keys)}");
                }
                flags.m_General |= mapped.general;
                if (side == "left" || side == "both")
                {
                    flags.m_Left |= mapped.side;
                }
                if (side == "right" || side == "both")
                {
                    flags.m_Right |= mapped.side;
                }
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            string prefabName = null;
            if (EntityManager.HasComponent<PrefabRef>(entity))
            {
                PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                prefabName = prefab != null ? prefab.name : null;
            }

            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueueUpgrade(entity, prefabName, flags, request))
            {
                return BridgeResponse.Error(BridgeErrorKind.Conflict, "another build operation is in progress, retry shortly");
            }
            return null;
        }

        private BridgeResponse ListBuildings(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            request.Query.TryGetValue("query", out string search);
            request.Query.TryGetValue("role", out string requestedRole);
            if (!string.IsNullOrWhiteSpace(requestedRole))
            {
                requestedRole = requestedRole.Trim().ToLowerInvariant();
                if (!kPrefabRoles.Contains(requestedRole))
                {
                    return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                        $"unknown role '{requestedRole}'; use {string.Join(", ", kPrefabRoles)}");
                }
            }
            request.Query.TryGetValue("operational_area", out string operationalAreaFilter);
            if (!string.IsNullOrWhiteSpace(operationalAreaFilter))
            {
                operationalAreaFilter = operationalAreaFilter.Trim().ToLowerInvariant();
                if (operationalAreaFilter != "any"
                    && operationalAreaFilter != "storage"
                    && operationalAreaFilter != "extractor")
                {
                    return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                        "operational_area must be any, storage or extractor");
                }
            }
            const int hardMax = 64;
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, hardMax) : 16;
            if (!TryGetOptionalCenter(request, out bool hasCenter, out float2 center, out BridgeResponse centerError))
            {
                return centerError;
            }
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 250f;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var operationalAreas = new PrefabOperationalAreaClassifier(EntityManager);
            var found = new List<(float distance, object item)>();
            int total = 0;
            using (NativeArray<Entity> entities = PlacedBuildingQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
                    PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(prefabRef.m_Prefab);
                    string name = prefab != null ? prefab.name : "<unknown>";
                    if (!string.IsNullOrEmpty(search)
                        && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    List<string> roles = GetPrefabRoles(
                        prefabRef.m_Prefab,
                        operationalAreas);
                    if (!string.IsNullOrEmpty(requestedRole) && !roles.Contains(requestedRole))
                    {
                        continue;
                    }
                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    if (hasCenter && math.distance(transform.m_Position.xz, center) > radius)
                    {
                        continue;
                    }
                    GetBuildingOperationalCapabilities(
                        entity,
                        out bool hasStorageArea,
                        out bool hasExtractorArea,
                        out bool expandableStorageArea,
                        out bool expandableExtractorArea);
                    if ((operationalAreaFilter == "any" && !hasStorageArea && !hasExtractorArea)
                        || (operationalAreaFilter == "storage" && !hasStorageArea)
                        || (operationalAreaFilter == "extractor" && !hasExtractorArea))
                    {
                        continue;
                    }
                    total++;
                    float distance = hasCenter ? math.distance(transform.m_Position.xz, center) : 0f;
                    object lotSize = null;
                    object footprintMeters = null;
                    if (EntityManager.HasComponent<BuildingData>(prefabRef.m_Prefab))
                    {
                        int2 lot = EntityManager.GetComponentData<BuildingData>(prefabRef.m_Prefab).m_LotSize;
                        lotSize = new { x = lot.x, z = lot.y };
                        footprintMeters = new
                        {
                            x = (float)Math.Round(lot.x * kFootprintCellSize, 1),
                            z = (float)Math.Round(lot.y * kFootprintCellSize, 1),
                        };
                    }
                    var item = new
                    {
                        entity = new { index = entity.Index, version = entity.Version },
                        prefab = name,
                        roles,
                        capabilities = new
                        {
                            operationalArea = hasStorageArea || hasExtractorArea,
                            storageArea = hasStorageArea,
                            extractorArea = hasExtractorArea,
                            expandableStorageArea,
                            expandableExtractorArea,
                        },
                        isSubBuilding = EntityManager.HasComponent<Game.Common.Owner>(entity),
                        position = new
                        {
                            x = transform.m_Position.x,
                            y = transform.m_Position.y,
                            z = transform.m_Position.z,
                        },
                        lotSize,
                        footprintMeters,
                        distanceM = hasCenter ? (double?)Math.Round(distance, 1) : null,
                    };
                    if (found.Count < limit)
                    {
                        found.Add((distance, item));
                    }
                    else if (hasCenter)
                    {
                        int worst = 0;
                        for (int j = 1; j < found.Count; j++)
                        {
                            if (found[j].distance > found[worst].distance)
                            {
                                worst = j;
                            }
                        }
                        if (distance < found[worst].distance)
                        {
                            found[worst] = (distance, item);
                        }
                    }
                }
            }

            if (hasCenter && found.Count > 1)
            {
                for (int i = 0; i < found.Count - 1; i++)
                {
                    for (int j = i + 1; j < found.Count; j++)
                    {
                        if (found[j].distance < found[i].distance)
                        {
                            (found[i], found[j]) = (found[j], found[i]);
                        }
                    }
                }
            }
            var results = new List<object>(found.Count);
            foreach ((_, object item) in found)
            {
                results.Add(item);
            }

            return BridgeResponse.Json(new
            {
                totalMatches = total,
                returned = results.Count,
                limit,
                truncated = total > results.Count,
                warning = total > results.Count
                    ? $"too many results: {total} buildings match, only {results.Count} returned; shrink radius / add query filter, or paginate."
                    : null,
                note = "hard max 64; sorted by distanceM when x/z given; use entity index+version with demolish",
                buildings = results,
            });
        }

        private void GetBuildingOperationalCapabilities(
            Entity building,
            out bool hasStorageArea,
            out bool hasExtractorArea,
            out bool expandableStorageArea,
            out bool expandableExtractorArea)
        {
            hasStorageArea = false;
            hasExtractorArea = false;
            expandableStorageArea = false;
            expandableExtractorArea = false;
            if (!EntityManager.HasBuffer<Game.Areas.SubArea>(building))
            {
                return;
            }
            DynamicBuffer<Game.Areas.SubArea> subAreas =
                EntityManager.GetBuffer<Game.Areas.SubArea>(building, isReadOnly: true);
            foreach (Game.Areas.SubArea subArea in subAreas)
            {
                Entity area = subArea.m_Area;
                if (area == Entity.Null
                    || !EntityManager.Exists(area)
                    || !IsAreaOwnedBy(area, building))
                {
                    continue;
                }
                bool storage = EntityManager.HasComponent<Game.Areas.Storage>(area);
                bool extractor = EntityManager.HasComponent<Game.Areas.Extractor>(area);
                hasStorageArea |= storage;
                hasExtractorArea |= extractor;
                if (storage
                    && EntityManager.HasBuffer<Game.Areas.Node>(area)
                    && EntityManager.HasComponent<PrefabRef>(area))
                {
                    Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    int nodeCount = EntityManager.GetBuffer<Game.Areas.Node>(area, isReadOnly: true).Length;
                    expandableStorageArea |= nodeCount >= 4
                        && nodeCount <= 16
                        && EntityManager.HasComponent<StorageAreaData>(areaPrefab)
                        && (EntityManager.GetComponentData<StorageAreaData>(areaPrefab).m_Resources
                            & Game.Economy.Resource.Garbage) != 0;
                }
                if (extractor
                    && EntityManager.HasBuffer<Game.Areas.Node>(area)
                    && EntityManager.HasComponent<PrefabRef>(area))
                {
                    Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    int nodeCount = EntityManager.GetBuffer<Game.Areas.Node>(area, isReadOnly: true).Length;
                    expandableExtractorArea |= nodeCount >= 4
                        && nodeCount <= 16
                        && EntityManager.HasComponent<ExtractorAreaData>(areaPrefab);
                }
            }
        }

        private static float? NetworkWidthM(EntityManager entityManager, Entity prefabEntity)
        {
            if (!entityManager.HasComponent<NetGeometryData>(prefabEntity))
            {
                return null;
            }
            return (float?)Math.Round(
                entityManager.GetComponentData<NetGeometryData>(prefabEntity).m_DefaultWidth,
                1);
        }

        private BridgeResponse Demolish(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!request.TryGetInt("index", out int index) || !request.TryGetInt("version", out int version))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "provide ?index=<int>&version=<int> from /city/buildings");
            }

            if (!TryResolveExistingEntity(index, version, out Entity entity))
            {
                return BridgeResponse.Error(BridgeErrorKind.NotFound, $"entity {index}:{version} does not exist (stale id?)");
            }
            bool isBuilding = EntityManager.HasComponent<Game.Buildings.Building>(entity);
            bool isTypedNetwork = false;
            if (EntityManager.HasComponent<Game.Net.Edge>(entity)
                && EntityManager.HasComponent<PrefabRef>(entity))
            {
                TypedNetworkKinds kinds = ClassifyNetPrefab(
                    EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                isTypedNetwork = (kinds & TypedNetworkMath.ProductKinds) != TypedNetworkKinds.None;
            }
            if (!isBuilding && !isTypedNetwork)
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    "demolish only accepts a building from list_buildings or a typed-network edge from list_networks (road, water, sewage, low_voltage); trees, plants, districts, tracks and other nets are unsupported");
            }
            if (EntityManager.HasComponent<Game.Common.Deleted>(entity))
            {
                return BridgeResponse.Error(BridgeErrorKind.Conflict, "entity is already being deleted");
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            string prefabName = null;
            if (EntityManager.HasComponent<PrefabRef>(entity))
            {
                PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                prefabName = prefab != null ? prefab.name : null;
            }

            // Deletion MUST go through the game's bulldoze pipeline. Adding a raw
            // Deleted component skips node/block/lane cleanup and corrupts state.
            BridgeToolSystem tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            if (!tool.TryQueueDemolish(entity, prefabName, request))
            {
                return BridgeResponse.Error(BridgeErrorKind.Conflict, "another build operation is in progress, retry shortly");
            }
            return null;
        }

        private static float3 BezierPoint(Bezier4x3 bezier, float t)
        {
            float3 ab = math.lerp(bezier.a, bezier.b, t);
            float3 bc = math.lerp(bezier.b, bezier.c, t);
            float3 cd = math.lerp(bezier.c, bezier.d, t);
            float3 abc = math.lerp(ab, bc, t);
            float3 bcd = math.lerp(bc, cd, t);
            return math.lerp(abc, bcd, t);
        }

        private static long MapTileKey(float2 position)
        {
            int x = (int)Math.Floor((position.x + kMapHalfSize) / kMapTileSize);
            int z = (int)Math.Floor((position.y + kMapHalfSize) / kMapTileSize);
            return ((long)x << 32) | (uint)z;
        }

        private static float BuildingRadius(BuildingData building)
        {
            return math.length(new float2(building.m_LotSize)) * 4f;
        }

        private float BuildingRadius(Entity prefabEntity)
        {
            return EntityManager.HasComponent<BuildingData>(prefabEntity)
                ? BuildingRadius(EntityManager.GetComponentData<BuildingData>(prefabEntity))
                : 0f;
        }

        private bool IsOnOwnedTile(float3 position)
        {
            long key = MapTileKey(position.xz);
            using (NativeArray<Entity> entities = MapTileQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (MapTileKey(EntityManager.GetComponentData<Game.Areas.Geometry>(entity)
                            .m_CenterPosition.xz) == key)
                    {
                        return !EntityManager.HasComponent<Game.Common.Native>(entity);
                    }
                }
            }
            return false;
        }

        private static string PrefabDisplayName(PrefabSystem prefabSystem, Entity prefabEntity)
        {
            PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(prefabEntity);
            return prefab != null ? prefab.name : "<unknown>";
        }

        private bool IsMenuCurbObject(Entity prefab)
        {
            if (!EntityManager.HasComponent<NetObjectData>(prefab)
                || !EntityManager.HasComponent<PlaceableObjectData>(prefab)
                || !EntityManager.HasComponent<UIObjectData>(prefab)
                || EntityManager.HasComponent<BuildingData>(prefab)
                || EntityManager.HasComponent<ServiceUpgradeData>(prefab))
            {
                return false;
            }
            PlaceableObjectData placeable =
                EntityManager.GetComponentData<PlaceableObjectData>(prefab);
            if (placeable.m_SubReplacementType != Game.Net.SubReplacementType.None)
            {
                return false;
            }
            Game.Objects.PlacementFlags roadside =
                Game.Objects.PlacementFlags.RoadEdge | Game.Objects.PlacementFlags.RoadSide;
            return (placeable.m_Flags & roadside) != 0;
        }

        private bool IsIndependentBuildingPrefab(Entity prefabEntity)
        {
            return !EntityManager.HasComponent<ServiceUpgradeData>(prefabEntity);
        }

        private bool TryFindPrefabByName(EntityQuery query, string name, out Entity prefabEntity, out PrefabBase prefab)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabBase candidate = prefabSystem.GetPrefab<PrefabBase>(entity);
                    if (candidate != null && string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        prefabEntity = entity;
                        prefab = candidate;
                        return true;
                    }
                }
            }
            prefabEntity = Entity.Null;
            prefab = null;
            return false;
        }
    }
}
