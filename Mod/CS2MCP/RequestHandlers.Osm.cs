using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse ExportOsm(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error)
                || !TryGetWorldBounds(request, out float xMin, out float zMin, out float xMax, out float zMax, out error))
            {
                return error;
            }
            if (!math.all(math.isfinite(new float4(xMin, zMin, xMax, zMax))))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Choose finite coordinates for the map extent.");
            }
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var streetNames = new Dictionary<Entity, string>();
            var roads = new List<CityOsmRoadSource>();
            var roadPrefabs = new Dictionary<Entity, (string Name, RoadPrefabFacts Facts)>();
            using (NativeArray<Entity> entities = PlacedRoadQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!EntityManager.HasComponent<RoadData>(prefab))
                    {
                        continue;
                    }
                    Curve curve = EntityManager.GetComponentData<Curve>(entity);
                    if (!CurveOverlapsBounds(curve, xMin, zMin, xMax, zMax))
                    {
                        continue;
                    }
                    Edge edge = EntityManager.GetComponentData<Edge>(entity);
                    if (!roadPrefabs.TryGetValue(prefab, out var asset))
                    {
                        string name = PrefabDisplayName(prefabs, prefab);
                        asset = (name, ReadRoadPrefabFacts(prefab, prefabs, string.Empty));
                        roadPrefabs.Add(prefab, asset);
                    }
                    var composition = EntityManager.HasComponent<Composition>(entity)
                        ? EntityManager.GetComponentData<Composition>(entity).m_Edge : Entity.Null;
                    var traffic = OsmCarLanes(composition);
                    string streetName = null;
                    if (EntityManager.HasComponent<Aggregated>(entity))
                    {
                        Entity street = EntityManager.GetComponentData<Aggregated>(entity).m_Aggregate;
                        if (EntityManager.Exists(street) && !streetNames.TryGetValue(street, out streetName))
                        {
                            streetName = names.GetRenderedLabelName(street);
                            streetNames.Add(street, streetName);
                        }
                    }
                    float width = EntityManager.HasComponent<NetCompositionData>(composition)
                        ? EntityManager.GetComponentData<NetCompositionData>(composition).m_Width : 0f;
                    roads.Add(new CityOsmRoadSource
                    {
                        Index = entity.Index,
                        Entity = OsmEntityIdentity(entity),
                        Prefab = asset.Name,
                        Name = streetName,
                        Highway = OsmHighway(asset.Facts.RoadClass),
                        OneWay = traffic.Direction,
                        Lanes = traffic.Count,
                        SpeedKmh = asset.Facts.SpeedKmh,
                        WidthMeters = width,
                        Grade = EdgeGrade(entity),
                        Start = OsmJunction(edge.m_Start),
                        End = OsmJunction(edge.m_End),
                        Curve = new RoadPath(curve.m_Bezier.a, curve.m_Bezier.b, curve.m_Bezier.c, curve.m_Bezier.d),
                    });
                }
            }
            var buildings = new List<CityOsmBuildingSource>();
            using (NativeArray<Entity> entities = PlacedBuildingQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!EntityManager.HasComponent<BuildingData>(prefab))
                    {
                        continue;
                    }
                    float2 lot = (float2)EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize * Game.Zones.ZoneUtils.CELL_SIZE;
                    if (math.any(lot <= 0f))
                    {
                        continue;
                    }
                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    float2 projectedHalfExtent = math.abs(math.mul(transform.m_Rotation, new float3(lot.x / 2f, 0f, 0f)).xz)
                        + math.abs(math.mul(transform.m_Rotation, new float3(0f, 0f, lot.y / 2f)).xz);
                    float2 center = transform.m_Position.xz;
                    if (math.any(center + projectedHalfExtent < new float2(xMin, zMin))
                        || math.any(center - projectedHalfExtent > new float2(xMax, zMax)))
                    {
                        continue;
                    }
                    buildings.Add(new CityOsmBuildingSource
                    {
                        Index = entity.Index,
                        Entity = OsmEntityIdentity(entity),
                        Prefab = PrefabDisplayName(prefabs, prefab),
                        Name = names.TryGetCustomName(entity, out string buildingName) ? buildingName : null,
                        Building = OsmBuildingKind(prefab),
                        Position = transform.m_Position,
                        Rotation = transform.m_Rotation,
                        LotSize = lot,
                    });
                }
            }
            BridgeResponse response = BridgeResponse.Text(CityOsm.Export(roads, buildings).Write());
            response.Files = new Dictionary<string, byte[]> { ["district.osm"] = response.Body };
            return response;
        }

        private CityOsmJunction OsmJunction(Entity node)
        {
            return new CityOsmJunction
            {
                Index = node.Index,
                Entity = OsmEntityIdentity(node),
                Position = EntityManager.GetComponentData<Node>(node).m_Position,
            };
        }

        private static string OsmEntityIdentity(Entity entity) => $"{entity.Index}:{entity.Version}";

        private static string OsmHighway(string roadClass)
        {
            switch (roadClass)
            {
                case RoadFactsMath.RoadClassHighway: return "motorway";
                case RoadFactsMath.RoadClassLarge: return "primary";
                case RoadFactsMath.RoadClassMedium: return "secondary";
                case RoadFactsMath.RoadClassMinor: return "residential";
                default: return "road";
            }
        }

        private (int? Count, string Direction) OsmCarLanes(Entity composition)
        {
            if (!EntityManager.HasBuffer<NetCompositionLane>(composition))
            {
                return (null, null);
            }
            int count = 0;
            DynamicBuffer<NetCompositionLane> lanes = EntityManager.GetBuffer<NetCompositionLane>(composition, true);
            bool forward = false;
            bool backward = false;
            foreach (NetCompositionLane lane in lanes)
            {
                LaneFlags flags = lane.m_Flags;
                if ((flags & LaneFlags.Road) == 0 || (flags & (LaneFlags.Master | LaneFlags.Parking | LaneFlags.Virtual | LaneFlags.BicyclesOnly)) != 0)
                {
                    continue;
                }
                count++;
                forward |= (flags & LaneFlags.Invert) == 0 || (flags & LaneFlags.Twoway) != 0;
                backward |= (flags & (LaneFlags.Invert | LaneFlags.Twoway)) != 0;
            }
            if (forward && backward)
            {
                return (count, "no");
            }
            return (count, forward ? "yes" : backward ? "-1" : null);
        }

        private string OsmBuildingKind(Entity prefab)
        {
            if (EntityManager.HasComponent<SchoolData>(prefab))
            {
                return "school";
            }
            switch (ClassifyNativeBuildingFill(prefab))
            {
                case MapFillKind.Residential: return "residential";
                case MapFillKind.Commercial: return "commercial";
                case MapFillKind.Industrial: return "industrial";
                case MapFillKind.Office: return "office";
                default: return "yes";
            }
        }
    }
}
