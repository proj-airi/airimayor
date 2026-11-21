using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private struct OsmEditRoadAsset
        {
            public Entity Prefab;
            public string Name;
            public string Highway;
            public bool OneWay;
            public bool Backward;
            public int? Lanes;
            public double? Speed;
            public float Width;
        }

        private List<OsmEditRoadAsset> ReadOsmRoadAssets()
        {
            var assets = new List<OsmEditRoadAsset>();
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            using (NativeArray<Entity> entities = RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefab in entities)
                {
                    if (IsLocked(prefab) || !EntityManager.HasComponent<PlaceableNetData>(prefab)
                        || !EntityManager.HasComponent<NetGeometryData>(prefab)
                        || (EntityManager.GetComponentData<PlaceableNetData>(prefab).m_PlacementFlags
                            & (PlacementFlags.UpgradeOnly | PlacementFlags.IsUpgrade)) != 0)
                    {
                        continue;
                    }
                    var flags = EntityManager.GetComponentData<RoadData>(prefab).m_Flags;
                    RoadPrefabFacts facts = ReadRoadPrefabFacts(prefab, prefabs, string.Empty);
                    int? lanes = facts.CarLanes;
                    if (EntityManager.HasBuffer<DefaultNetLane>(prefab))
                    {
                        lanes = 0;
                        foreach (DefaultNetLane lane in EntityManager.GetBuffer<DefaultNetLane>(prefab, true))
                        {
                            if (IsOsmCarLane(lane.m_Flags)) lanes++;
                        }
                    }
                    assets.Add(new OsmEditRoadAsset
                    {
                        Prefab = prefab,
                        Name = PrefabDisplayName(prefabs, prefab),
                        Highway = OsmHighway(facts.RoadClass),
                        OneWay = (flags & (Game.Prefabs.RoadFlags.DefaultIsForward | Game.Prefabs.RoadFlags.DefaultIsBackward)) != 0,
                        Backward = (flags & Game.Prefabs.RoadFlags.DefaultIsBackward) != 0,
                        Lanes = lanes,
                        Speed = facts.SpeedKmh,
                        Width = NativeRoadWidthM(prefab),
                    });
                }
            }
            return assets;
        }

        private static bool IsOsmCarLane(LaneFlags flags) => (flags & LaneFlags.Road) != 0
            && (flags & (LaneFlags.Master | LaneFlags.Parking | LaneFlags.Virtual | LaneFlags.BicyclesOnly)) == 0;

        private Entity ResolveOsmRoad(ref List<OsmEditRoadAsset> assets, OsmWay original, OsmWay way, Entity existing, out bool reverse)
        {
            string requested = CityOsm.Tag(way, "airimayor:prefab");
            string highway = CityOsm.Tag(way, "highway");
            string oneway = CityOsm.Tag(way, "oneway");
            bool backward = oneway == "-1" || oneway == "reverse";
            bool directional = backward || oneway == "yes" || oneway == "true" || oneway == "1"
                || (oneway == null && (highway == "motorway" || highway == "motorway_link"));
            if (!directional && oneway != null && oneway != "no" && oneway != "0" && oneway != "false")
            {
                throw new FormatException($"Way {way.Id} needs an ordinary yes, no or -1 travel direction.");
            }

            Entity existingPrefab = existing == Entity.Null ? Entity.Null : EntityManager.GetComponentData<PrefabRef>(existing).m_Prefab;
            string[] selection = { "airimayor:prefab", "highway", "lanes", "maxspeed", "width", "oneway" };
            if (existingPrefab != Entity.Null && selection.All(key => CityOsm.Tag(original, key) == CityOsm.Tag(way, key)))
            {
                reverse = false;
                return existingPrefab;
            }

            string category = OsmRoadCategory(highway);
            float? width = OsmPositiveNumber(way, "width");
            float? speed = OsmPositiveNumber(way, "maxspeed");
            float? lanes = OsmPositiveNumber(way, "lanes");
            if (lanes.HasValue && lanes != math.floor(lanes.Value))
            {
                throw new FormatException($"Way {way.Id} needs a whole lane count.");
            }
            bool samePin = original != null && requested == CityOsm.Tag(original, "airimayor:prefab");
            assets ??= ReadOsmRoadAssets();
            var choices = assets.Where(a => (requested == null || samePin || a.Name == requested)
                && (category == null || a.Highway == category)
                && a.OneWay == directional
                && (lanes == null || a.Lanes == lanes)
                && (speed == null || a.Speed == speed)
                && (width == null || math.abs(a.Width - width.Value) < 0.1f)
                && a.Lanes != 0);
            OsmEditRoadAsset best = choices.OrderBy(a => a.Prefab == existingPrefab ? 0 : 1)
                .ThenBy(a => a.Width).ThenBy(a => a.Name, StringComparer.Ordinal).FirstOrDefault();
            if (best.Prefab == Entity.Null)
            {
                throw new FormatException($"No unlocked road asset matches way {way.Id}. Choose a listed road prefab and matching road tags.");
            }
            reverse = best.OneWay && backward != best.Backward;
            return best.Prefab;
        }

        private static string OsmRoadCategory(string highway)
        {
            switch (highway)
            {
                case "motorway":
                case "motorway_link": return "motorway";
                case "trunk":
                case "trunk_link":
                case "primary":
                case "primary_link": return "primary";
                case "secondary":
                case "secondary_link": return "secondary";
                case "tertiary":
                case "tertiary_link":
                case "unclassified":
                case "living_street":
                case "service":
                case "residential": return "residential";
                case "road": return null;
                default: throw new FormatException($"The city has no road class for highway={highway}.");
            }
        }

        private static float? OsmPositiveNumber(OsmWay way, string key)
        {
            string raw = CityOsm.Tag(way, key);
            if (raw == null) return null;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                || !math.isfinite(number) || number <= 0f)
            {
                throw new FormatException($"Way {way.Id} needs a positive numeric {key} value.");
            }
            return number;
        }

        private OsmBuildingPlacement ResolveOsmBuilding(OsmWay original, OsmWay way, Entity existing,
            CityOsmBuilding building, ref TerrainHeightData terrain)
        {
            string requested = CityOsm.Tag(way, "airimayor:prefab");
            string amenity = CityOsm.Tag(way, "amenity");
            if (amenity != null && amenity != "school")
            {
                throw new FormatException($"Building {way.Id} cannot represent amenity={amenity}.");
            }
            var existingPrefab = existing == Entity.Null ? Entity.Null : EntityManager.GetComponentData<PrefabRef>(existing).m_Prefab;
            Entity prefab = Entity.Null;
            quaternion rotation;
            float3 position;
            bool retained = building.Position.HasValue && building.Rotation.HasValue && existingPrefab != Entity.Null
                && CityOsm.Tag(original, "airimayor:prefab") == requested
                && CityOsm.Tag(original, "building") == building.Building && CityOsm.Tag(original, "amenity") == amenity;
            if (retained)
            {
                prefab = existingPrefab;
                position = building.Position.Value;
                rotation = building.Rotation.Value;
            }
            else
            {
                var pose = CityOsmEdit.LotPose(building);
                position = pose.Position;
                rotation = pose.Rotation;
                string best = null;
                bool rotate = false;
                var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
                using (NativeArray<Entity> candidates = BuildingPrefabQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity candidate in candidates)
                    {
                        if (IsLocked(candidate) || !IsIndependentBuildingPrefab(candidate)
                            || !EntityManager.HasComponent<PlaceableObjectData>(candidate)) continue;
                        string name = PrefabDisplayName(prefabs, candidate);
                        if (requested != null && requested != name) continue;
                        if (amenity == "school" && !EntityManager.HasComponent<SchoolData>(candidate)) continue;
                        string kind = OsmBuildingKind(candidate);
                        bool apartment = building.Building == "apartments" && kind == "residential";
                        bool retail = building.Building == "retail" && kind == "commercial";
                        if (building.Building != "yes" && building.Building != kind && !apartment && !retail) continue;
                        float2 size = (float2)EntityManager.GetComponentData<BuildingData>(candidate).m_LotSize * Game.Zones.ZoneUtils.CELL_SIZE;
                        bool straight = math.all(math.abs(size - pose.Size) <= 0.1f);
                        if (!straight && !math.all(math.abs(size.yx - pose.Size) <= 0.1f)) continue;
                        if (candidate != existingPrefab && best != null && string.CompareOrdinal(best, name) <= 0) continue;
                        prefab = candidate;
                        best = name;
                        rotate = !straight;
                        if (candidate == existingPrefab || requested != null) break;
                    }
                }
                if (prefab == Entity.Null)
                {
                    throw new FormatException($"No unlocked {building.Building} asset fits building {way.Id}'s {pose.Size.x:F1} by {pose.Size.y:F1} m lot.");
                }
                if (rotate) rotation = math.mul(rotation, quaternion.RotateY(math.PI / 2f));
                if (!math.isfinite(position.y)) position.y = TerrainUtils.SampleHeight(ref terrain, position);
                var lot = (float2)EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize * Game.Zones.ZoneUtils.CELL_SIZE;
                var placed = CityOsm.Corners(position, rotation, lot);
                foreach (float3 point in building.Footprint)
                {
                    if (!placed.Any(corner => math.distance(corner.xz, point.xz) <= 0.1f
                        && (!math.isfinite(point.y) || math.abs(corner.y - point.y) <= 0.1f)))
                    {
                        throw new FormatException($"Building {way.Id} has a footprint that its asset cannot realize.");
                    }
                }
            }
            return new OsmBuildingPlacement
            {
                WayId = way.Id, Original = existing, Prefab = prefab,
                Position = position, Rotation = rotation, Name = OsmRequestedName(original, way),
            };
        }
    }
}
