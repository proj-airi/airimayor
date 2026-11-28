using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Jobs;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse ReadOsm(BridgeRequest request)
        {
            if (!TryGetOsmBounds(request, out float4 bounds, out BridgeResponse error))
            {
                return error;
            }
            return OsmFileResponse(bounds, ReadOsmDocument(bounds));
        }

        private BridgeResponse EditOsm(BridgeRequest request)
        {
            if (!TryGetOsmBounds(request, out float4 bounds, out BridgeResponse error))
            {
                return error;
            }
            if (!airimayor.Setting.StaticAllowConstruction && !airimayor.Setting.StaticAllowDemolition)
            {
                return BridgeResponse.Error(BridgeErrorKind.Conflict, "City construction and demolition are disabled.");
            }
            OsmDocument current = ReadOsmDocument(bounds);
            if (!request.Query.TryGetValue("revision", out string revision)
                || CityOsmFile.Revision(m_System.CityLoadId, bounds, current.Write()) != revision)
            {
                return BridgeResponse.Error(BridgeErrorKind.Conflict, "Read these coordinates again and edit the current revision.");
            }
            try
            {
                OsmDocument edited = current.Edit(request.Body);
                return ApplyOsmEdit(request, bounds, current, edited);
            }
            catch (FormatException exception)
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, exception.Message);
            }
        }

        private bool TryGetOsmBounds(BridgeRequest request, out float4 bounds, out BridgeResponse error)
        {
            bounds = default;
            if (!TryGetCity(out _, out error))
            {
                return false;
            }
            if (!request.TryGetFloat("xMin", out bounds.x) || !request.TryGetFloat("zMin", out bounds.y)
                || !request.TryGetFloat("xMax", out bounds.z) || !request.TryGetFloat("zMax", out bounds.w)
                || !CityOsmFile.ValidBounds(bounds))
            {
                error = BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Give four finite city coordinates with xMin < xMax and zMin < zMax.");
                return false;
            }
            return true;
        }

        private BridgeResponse CheckOsmRevision(BridgeRequest request, float4 bounds)
        {
            return CityOsmFile.Revision(m_System.CityLoadId, bounds, ReadOsmDocument(bounds).Write()) == request.Query["revision"]
                ? null : BridgeResponse.Error(BridgeErrorKind.Conflict, "The district changed. Read it again before editing.");
        }

        private BridgeResponse OsmFileResponse(float4 bounds, OsmDocument document, object realized = null)
        {
            string content = document.Write();
            string revision = CityOsmFile.Revision(m_System.CityLoadId, bounds, content);
            BridgeResponse response = BridgeResponse.Json(new { revision, content, realized });
            response.Files = new Dictionary<string, byte[]> { ["district.osm"] = Encoding.UTF8.GetBytes(content) };
            return response;
        }

        private struct OsmDistrictIterator : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds2 Bounds;
            public List<Entity> Entities;

            public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(bounds.m_Bounds.xz, Bounds);

            public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
            {
                if (Intersect(bounds)) Entities.Add(entity);
            }
        }

        private OsmDocument ReadOsmDocument(float4 bounds)
        {
            var district = new Bounds2(bounds.xy, bounds.zw);
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var streetNames = new Dictionary<Entity, string>();
            var roads = new List<CityOsmRoadSource>();
            var roadPrefabs = new Dictionary<Entity, (string Name, RoadPrefabFacts Facts)>();
            foreach (var (entity, prefab, kinds, curve) in OsmDistrictNetworks(district))
            {
                Edge edge = EntityManager.GetComponentData<Edge>(entity);
                var source = new CityOsmRoadSource
                {
                    Index = entity.Index,
                    Entity = OsmEntityIdentity(entity),
                    Start = OsmJunction(edge.m_Start),
                    End = OsmJunction(edge.m_End),
                    Curve = new RoadPath(curve.m_Bezier.a, curve.m_Bezier.b, curve.m_Bezier.c, curve.m_Bezier.d),
                };
                if (kinds != TypedNetworkKinds.Road)
                {
                    source.Prefab = PrefabDisplayName(prefabs, prefab);
                    if (CityOsmTransportWays.IsTransport(kinds))
                    {
                        source.Transport = kinds;
                        if (EntityManager.HasComponent<Composition>(entity))
                        {
                            Entity deck = EntityManager.GetComponentData<Composition>(entity).m_Edge;
                            source.Grade = ReadRoadGrade(EntityManager, deck) ?? MapGrade.Ground;
                        }
                    }
                    else source.Utility = kinds;
                    roads.Add(source);
                    continue;
                }
                if (!roadPrefabs.TryGetValue(prefab, out var asset))
                {
                    string name = PrefabDisplayName(prefabs, prefab);
                    asset = (name, ReadRoadPrefabFacts(prefab, prefabs));
                    roadPrefabs.Add(prefab, asset);
                }
                var composition = EntityManager.HasComponent<Composition>(entity)
                    ? EntityManager.GetComponentData<Composition>(entity).m_Edge : Entity.Null;
                var traffic = OsmCarLanes(composition);
                float width = EntityManager.HasComponent<NetCompositionData>(composition)
                    ? EntityManager.GetComponentData<NetCompositionData>(composition).m_Width : 0f;
                source.Prefab = asset.Name;
                source.Name = OsmStreetName(entity, names, streetNames);
                source.Highway = OsmHighway(asset.Facts.RoadClass);
                if (EntityManager.HasComponent<Upgraded>(entity))
                    source.UpgradeTags = OsmRoadUpgrades.Export(EntityManager.GetComponentData<Upgraded>(entity).m_Flags);
                source.OneWay = traffic.Direction;
                source.Lanes = traffic.Count;
                source.SpeedKmh = asset.Facts.SpeedKmh;
                source.WidthMeters = width;
                source.Grade = ReadRoadGrade(EntityManager, composition) ?? MapGrade.Ground;
                roads.Add(source);
            }
            var buildings = new List<CityOsmBuildingSource>();
            var points = new List<OsmNode>();
            EntityQuery lots = PlacedBuildingQuery;
            foreach (Entity entity in OsmDistrictObjects(district))
            {
                if (!EntityManager.Exists(entity)) continue;
                if (!lots.MatchesIgnoreFilter(entity))
                {
                    if (TransitStopQuery.MatchesIgnoreFilter(entity))
                    {
                        OsmNode stop = ReadOsmStationStop(entity, district, prefabs, names) ?? ReadOsmStop(entity, district, prefabs, names);
                        if (stop != null) points.Add(stop);
                    }
                    else
                    {
                        OsmNode point = ReadOsmStandalonePoint(entity, district, prefabs);
                        if (point != null) points.Add(point);
                    }
                    continue;
                }
                if (!TryOsmBuildingLot(entity, district, out Entity prefab, out Transform transform, out float2 lot, out _)) continue;
                buildings.Add(new CityOsmBuildingSource
                {
                    Index = entity.Index,
                    Entity = OsmEntityIdentity(entity),
                    Prefab = PrefabDisplayName(prefabs, prefab),
                    Name = names.TryGetCustomName(entity, out string buildingName) ? buildingName : null,
                    Building = OsmBuildingKind(prefab),
                    FacilityUpgrades = ReadOsmFacilityUpgrades(entity, prefabs),
                    Position = transform.m_Position,
                    Rotation = transform.m_Rotation,
                    LotSize = lot,
                });
            }
            OsmDocument document = CityOsm.Export(roads, buildings);
            foreach (OsmNode node in points) document.Nodes.Add(node.Id, node);
            ReadOsmTransit(document, bounds);
            AddOsmAreas(document, bounds);
            CityOsmFile.AddBounds(document, bounds);
            return document;
        }

        private IEnumerable<(Entity Entity, Entity Prefab, TypedNetworkKinds Kinds, Curve Curve)> OsmDistrictNetworks(Bounds2 district)
        {
            var iterator = new OsmDistrictIterator { Bounds = district, Entities = new List<Entity>() };
            var netTree = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>()
                .GetNetSearchTree(true, out JobHandle netDependencies);
            netDependencies.Complete();
            netTree.Iterate(ref iterator);
            EntityQuery edges = PlacedRoadQuery;
            foreach (Entity entity in iterator.Entities)
            {
                if (!EntityManager.Exists(entity) || !edges.MatchesIgnoreFilter(entity)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                TypedNetworkKinds kinds = ClassifyNetPrefab(prefab);
                if (kinds == TypedNetworkKinds.None) kinds = OsmTransportPrefabKind(prefab);
                if (kinds == TypedNetworkKinds.None)
                {
                    continue;
                }
                Curve curve = EntityManager.GetComponentData<Curve>(entity);
                if (!CityOsmSpatial.Intersects(district, curve.m_Bezier.xz))
                {
                    continue;
                }
                yield return (entity, prefab, kinds, curve);
            }
        }

        private List<Entity> OsmDistrictObjects(Bounds2 district)
        {
            float padding = OsmObjectSearchPadding();
            var iterator = new OsmDistrictIterator
            {
                Bounds = new Bounds2(district.min - padding, district.max + padding),
                Entities = new List<Entity>(),
            };
            var tree = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>().GetStaticSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();
            tree.Iterate(ref iterator);
            return iterator.Entities;
        }

        private bool TryOsmBuildingLot(Entity entity, Bounds2 district, out Entity prefab, out Transform transform, out float2 lot, out Quad2 footprint)
        {
            prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            transform = EntityManager.GetComponentData<Transform>(entity);
            lot = EntityManager.HasComponent<BuildingData>(prefab)
                ? (float2)EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize * Game.Zones.ZoneUtils.CELL_SIZE : default;
            footprint = default;
            if (math.any(lot <= 0f)) return false;
            float3[] corners = CityOsm.Corners(transform.m_Position, transform.m_Rotation, lot);
            footprint = new Quad2(corners[0].xz, corners[1].xz, corners[2].xz, corners[3].xz);
            return MathUtils.Intersect(district, footprint);
        }

        private string OsmStreetName(Entity entity, Game.UI.NameSystem names, Dictionary<Entity, string> streetNames)
        {
            if (!EntityManager.HasComponent<Aggregated>(entity)) return null;
            Entity street = EntityManager.GetComponentData<Aggregated>(entity).m_Aggregate;
            if (!EntityManager.Exists(street)) return null;
            if (!streetNames.TryGetValue(street, out string name))
            {
                name = names.GetRenderedLabelName(street);
                streetNames.Add(street, name);
            }
            return name;
        }

        private (Guid City, int Buildings, int Stops) m_OsmAssetVersion;
        private float m_OsmObjectPadding;

        private float OsmObjectSearchPadding()
        {
            var version = (m_System.CityLoadId, BuildingPrefabQuery.GetCombinedComponentOrderVersion(false),
                CurbObjectPrefabQuery.GetCombinedComponentOrderVersion(false));
            if (version == m_OsmAssetVersion) return m_OsmObjectPadding;
            float padding = 0f;
            foreach (EntityQuery query in new[] { BuildingPrefabQuery, CurbObjectPrefabQuery })
            {
                using (var assets = query.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity entity in assets)
                    {
                        if (!EntityManager.HasComponent<ObjectGeometryData>(entity)) continue;
                        ObjectGeometryData geometry = EntityManager.GetComponentData<ObjectGeometryData>(entity);
                        float2 halfLot = EntityManager.HasComponent<BuildingData>(entity)
                            ? (float2)EntityManager.GetComponentData<BuildingData>(entity).m_LotSize * (Game.Zones.ZoneUtils.CELL_SIZE * 0.5f)
                            : float2.zero;
                        float extra = (geometry.m_Flags & Game.Objects.GeometryFlags.Circular) != 0
                            ? math.length(halfLot) - geometry.m_Size.x * 0.5f
                            : CityOsmSpatial.LotOverhang(halfLot, geometry.m_Bounds);
                        padding = math.max(padding, extra);
                    }
                }
            }
            m_OsmAssetVersion = version;
            m_OsmObjectPadding = padding;
            return padding;
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
                if (!IsCarTravelLane(lane.m_Lane, flags))
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
            if (EntityManager.HasComponent<SpawnableBuildingData>(prefab))
            {
                Entity zone = EntityManager.GetComponentData<SpawnableBuildingData>(prefab).m_ZonePrefab;
                if (EntityManager.HasComponent<ZoneData>(zone))
                {
                    ZoneData data = EntityManager.GetComponentData<ZoneData>(zone);
                    if (data.IsOffice()) return "office";
                    switch (data.m_AreaType)
                    {
                        case Game.Zones.AreaType.Residential: return "residential";
                        case Game.Zones.AreaType.Commercial: return "commercial";
                        case Game.Zones.AreaType.Industrial: return "industrial";
                    }
                }
            }
            return "yes";
        }

        internal static MapGrade? ReadRoadGrade(EntityManager manager, Entity composition)
        {
            if (!manager.HasComponent<NetCompositionData>(composition)) return null;
            CompositionFlags.General flags = manager.GetComponentData<NetCompositionData>(composition).m_Flags.m_General;
            if ((flags & CompositionFlags.General.Tunnel) != 0) return MapGrade.Tunnel;
            return (flags & CompositionFlags.General.Elevated) != 0 ? MapGrade.Bridge : MapGrade.Ground;
        }
    }
}
