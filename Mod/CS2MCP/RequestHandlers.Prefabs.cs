using System;
using System.Collections.Generic;
using System.Linq;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private const float kFootprintCellSize = Game.Zones.ZoneUtils.CELL_SIZE;
        private EntityQuery m_BuildingPrefabQuery;
        private bool m_BuildingPrefabQueryCreated;
        private EntityQuery m_RoadPrefabQuery;
        private bool m_RoadPrefabQueryCreated;
        private EntityQuery m_NetPrefabQuery;
        private bool m_NetPrefabQueryCreated;
        private EntityQuery m_AssetCategoryQuery;
        private bool m_AssetCategoryQueryCreated;
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

        private EntityQuery AssetCategoryQuery
        {
            get
            {
                if (!m_AssetCategoryQueryCreated)
                {
                    m_AssetCategoryQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<UIAssetCategoryData>(),
                        ComponentType.ReadOnly<UIGroupElement>());
                    m_AssetCategoryQueryCreated = true;
                }
                return m_AssetCategoryQuery;
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
            Func<Entity, bool> matchesCategory;
            switch (category)
            {
                case "building":
                    bool includeCurb = requestedRole == "transport" || requestedRole == "post";
                    matchesCategory = asset => IsIndependentBuildingPrefab(asset) || (includeCurb && IsMenuCurbObject(asset));
                    break;
                case "road":
                    matchesCategory = asset => EntityManager.HasComponent<RoadData>(asset) && IsPlaceableNetworkPrefab(asset);
                    break;
                case "net":
                    matchesCategory = IsPlaceableNetworkPrefab;
                    break;
                case "tree":
                    matchesCategory = asset => EntityManager.HasComponent<TreeData>(asset) && EntityManager.HasComponent<PlaceableObjectData>(asset);
                    break;
                case "object":
                    matchesCategory = asset => IsMenuCurbObject(asset) || OsmStandalonePointPrefab(asset) == OsmStandalonePoint.StreetLamp;
                    break;
                default:
                    return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "category must be 'building', 'road', 'net' (all networks incl. pipes/power/tracks/paths), 'tree', or 'object' (roadside objects)");
            }
            string availability = request.Query.TryGetValue("availability", out string scope) ? scope : "available";
            if (availability != "available" && availability != "locked" && availability != "all")
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Choose available, locked or all assets.");
            int offset = 0;
            if (request.Query.ContainsKey("offset") && (!request.TryGetInt("offset", out offset) || offset < 0))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Choose a nonnegative catalog offset.");
            if (!string.IsNullOrEmpty(requestedRole) && !kPrefabRoles.Contains(requestedRole))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    $"unknown prefab role '{requestedRole}'; valid: {string.Join(", ", kPrefabRoles)}");
            }
            int limit = request.TryGetInt("limit", out int rawLimit) ? math.clamp(rawLimit, 1, 64) : 16;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var operationalAreas = new PrefabOperationalAreaClassifier(EntityManager);
            var uniqueAssets = World.GetOrCreateSystemManaged<Game.UI.InGame.UniqueAssetTrackingSystem>();
            var matches = new List<(Entity Entity, PrefabBase Prefab, bool Locked, bool AlreadyPlacedUnique)>();
            var seen = new HashSet<Entity>();
            using (var categories = AssetCategoryQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity assetCategory in categories)
                {
                    foreach (UIGroupElement entry in EntityManager.GetBuffer<UIGroupElement>(assetCategory, true))
                    {
                        Entity entity = entry.m_Prefab;
                        if (!seen.Add(entity) || !matchesCategory(entity)) continue;
                        bool locked = IsLocked(entity);
                        bool alreadyPlacedUnique = uniqueAssets.IsPlacedUniqueAsset(entity);
                        if (availability == "available" && (locked || alreadyPlacedUnique)) continue;
                        if (availability == "locked" && !locked) continue;
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
                        if (!string.IsNullOrEmpty(requestedRole) && !GetPrefabRoles(entity, operationalAreas).Contains(requestedRole))
                        {
                            continue;
                        }
                        matches.Add((entity, prefab, locked, alreadyPlacedUnique));
                    }
                }
            }

            var results = new List<object>();
            foreach (var match in matches.OrderBy(item => item.Prefab.name, StringComparer.Ordinal)
                .ThenBy(item => item.Entity.Index).Skip(offset).Take(limit))
            {
                results.Add(ReadCatalogAsset(match.Entity, match.Prefab, match.Locked, match.AlreadyPlacedUnique, prefabSystem, operationalAreas));
            }

            return BridgeResponse.Json(new
            {
                category,
                role = requestedRole,
                availability,
                totalMatches = matches.Count,
                nextOffset = offset + results.Count < matches.Count ? (int?)(offset + results.Count) : null,
                returned = results.Count,
                stalenessWarning = LockStalenessWarning,
                prefabs = results,
            });
        }

        private object ReadCatalogAsset(Entity entity, PrefabBase prefab, bool locked, bool alreadyPlacedUnique, PrefabSystem prefabSystem,
            PrefabOperationalAreaClassifier operationalAreas)
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
                RoadPrefabFacts facts = ReadRoadPrefabFacts(entity, prefabSystem);
                road = new
                {
                    roadClass = facts.RoadClass,
                    speedKmh = facts.SpeedKmh,
                    carLanes = facts.CarLanes,
                    highwayRules = facts.HighwayRules,
                    zonable = facts.Zonable,
                };
            }
            return new
            {
                name = prefab.name,
                type = prefab.GetType().Name,
                roles = GetPrefabRoles(entity, operationalAreas),
                stopType = EntityManager.HasComponent<TransportStopData>(entity)
                    ? EntityManager.GetComponentData<TransportStopData>(entity).m_TransportType.ToString() : null,
                locked,
                alreadyPlacedUnique,
                lotSize,
                footprintMeters,
                widthM,
                road,
            };
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

        private bool IsPlaceableNetworkPrefab(Entity prefab)
        {
            return EntityManager.HasComponent<NetGeometryData>(prefab) && EntityManager.HasComponent<PlaceableNetData>(prefab)
                && (EntityManager.GetComponentData<PlaceableNetData>(prefab).m_PlacementFlags
                    & (Game.Net.PlacementFlags.UpgradeOnly | Game.Net.PlacementFlags.IsUpgrade)) == 0;
        }

        private bool IsIndependentBuildingPrefab(Entity prefabEntity)
        {
            return EntityManager.HasComponent<BuildingData>(prefabEntity) && EntityManager.HasComponent<PlaceableObjectData>(prefabEntity)
                && !EntityManager.HasComponent<ServiceUpgradeData>(prefabEntity);
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
