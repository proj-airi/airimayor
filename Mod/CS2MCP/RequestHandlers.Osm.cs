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
            if (!airimayor.Setting.StaticAllowConstruction)
            {
                return BridgeResponse.Error(BridgeErrorKind.Conflict, "City construction is disabled.");
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
                if (!airimayor.Setting.StaticAllowDemolition
                    && (current.Ways.Keys.Any(id => !edited.Ways.ContainsKey(id))
                        || current.Nodes.Values.Any(node => CityOsm.IsPlatform(node) && !edited.Nodes.ContainsKey(node.Id))))
                {
                    return BridgeResponse.Error(BridgeErrorKind.Conflict, "Removing a mapped feature requires demolition permission.");
                }
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

        private BridgeResponse OsmFileResponse(float4 bounds, OsmDocument document)
        {
            string content = document.Write();
            string revision = CityOsmFile.Revision(m_System.CityLoadId, bounds, content);
            BridgeResponse response = BridgeResponse.Json(new { revision, content });
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
            var iterator = new OsmDistrictIterator { Bounds = district, Entities = new List<Entity>() };
            var netTree = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>()
                .GetNetSearchTree(true, out JobHandle netDependencies);
            netDependencies.Complete();
            netTree.Iterate(ref iterator);
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var streetNames = new Dictionary<Entity, string>();
            var roads = new List<CityOsmRoadSource>();
            var roadPrefabs = new Dictionary<Entity, (string Name, RoadPrefabFacts Facts)>();
            EntityQuery edges = PlacedRoadQuery;
            foreach (Entity entity in iterator.Entities)
            {
                if (!EntityManager.Exists(entity) || !edges.MatchesIgnoreFilter(entity)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                TypedNetworkKinds kinds = ClassifyNetPrefab(prefab);
                if (kinds == TypedNetworkKinds.None)
                {
                    continue;
                }
                Curve curve = EntityManager.GetComponentData<Curve>(entity);
                if (!CityOsmSpatial.Intersects(district, curve.m_Bezier.xz))
                {
                    continue;
                }
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
                    source.Utility = kinds;
                    roads.Add(source);
                    continue;
                }
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
                source.Prefab = asset.Name;
                source.Name = streetName;
                source.Highway = OsmHighway(asset.Facts.RoadClass);
                source.OneWay = traffic.Direction;
                source.Lanes = traffic.Count;
                source.SpeedKmh = asset.Facts.SpeedKmh;
                source.WidthMeters = width;
                source.Grade = ReadRoadGrade(EntityManager, composition) ?? MapGrade.Ground;
                roads.Add(source);
            }
            iterator.Entities.Clear();
            float padding = OsmObjectSearchPadding();
            iterator.Bounds = new Bounds2(district.min - padding, district.max + padding);
            var objectTree = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>()
                .GetStaticSearchTree(true, out JobHandle objectDependencies);
            objectDependencies.Complete();
            objectTree.Iterate(ref iterator);
            var buildings = new List<CityOsmBuildingSource>();
            var stops = new List<OsmNode>();
            EntityQuery lots = PlacedBuildingQuery;
            foreach (Entity entity in iterator.Entities)
            {
                if (!EntityManager.Exists(entity)) continue;
                if (!lots.MatchesIgnoreFilter(entity))
                {
                    if (TransitStopQuery.MatchesIgnoreFilter(entity))
                    {
                        OsmNode stop = ReadOsmStop(entity, district, prefabs, names);
                        if (stop != null) stops.Add(stop);
                    }
                    continue;
                }
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
                var corners = CityOsm.Corners(transform.m_Position, transform.m_Rotation, lot);
                if (!MathUtils.Intersect(district, new Quad2(corners[0].xz, corners[1].xz, corners[2].xz, corners[3].xz)))
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
            OsmDocument document = CityOsm.Export(roads, buildings);
            foreach (OsmNode node in stops) document.Nodes.Add(node.Id, node);
            CityOsmFile.AddBounds(document, bounds);
            return document;
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
                if (!IsOsmCarLane(flags))
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
