using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse QueryOsm(BridgeRequest request)
        {
            float4 bounds;
            if (request.Query.ContainsKey("xMin") || request.Query.ContainsKey("zMin")
                || request.Query.ContainsKey("xMax") || request.Query.ContainsKey("zMax"))
            {
                if (!TryGetOsmBounds(request, out bounds, out BridgeResponse error)) return error;
            }
            else
            {
                if (!TryGetCity(out _, out BridgeResponse error)) return error;
                float half = CellMapSystem<NaturalResourceCell>.kMapSize * 0.5f;
                bounds = new float4(-half, -half, half, half);
            }
            request.Query.TryGetValue("kind", out string kind);
            bool buildings = kind == "building";
            bool districts = kind == "district";
            bool transit = kind == "stop" || kind == "route";
            TypedNetworkKinds filter = CityOsmTransportWays.Kind(kind);
            if (!buildings && !districts && !transit && filter == TypedNetworkKinds.None && !TypedNetworkMath.TryParseKind(kind, out filter, out _))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Choose building, district, stop, route, road, footway, rail, tram, subway, water, sewage or low_voltage.");
            string view = request.Query.TryGetValue("view", out string selectedView) ? selectedView : "features";
            if (view != "features" && (view != "topology" || buildings || districts || transit || CityOsmTransportWays.IsTransport(filter)))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Choose features, or topology for a street or utility network.");
            if (request.Query.TryGetValue("road_classes", out string requestedRoadClasses)
                && (filter != TypedNetworkKinds.Road || view != "features"))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Use road_classes when finding roads.");
            if (request.Query.TryGetValue("building_types", out string requestedBuildingTypes) && !buildings)
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Use building_types when finding buildings.");
            CityOsmQueryTypes roadClasses;
            CityOsmQueryTypes buildingTypes;
            try
            {
                roadClasses = CityOsmQueryTypes.RoadClasses(requestedRoadClasses);
                buildingTypes = CityOsmQueryTypes.BuildingTypes(requestedBuildingTypes);
            }
            catch (FormatException error)
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, error.Message);
            }
            TransportType? transportType = null;
            if (request.Query.TryGetValue("transport_type", out string mode))
            {
                if (!transit || !CityOsmTransit.TryReadTransportType(mode, out var selectedType))
                    return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Use a named transport_type such as bus, tram or train with kind=stop or kind=route.");
                transportType = selectedType;
            }
            request.Query.TryGetValue("query", out string search);
            request.Query.TryGetValue("role", out string role);
            request.Query.TryGetValue("operational_area", out string area);
            request.Query.TryGetValue("sort", out string sort);
            if (role != null && (!buildings || !kPrefabRoles.Contains(role)))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, $"Choose a building role from {string.Join(", ", kPrefabRoles)}.");
            if (area != null && (!buildings || (area != "any" && area != "extractor" && area != "storage")))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "The operational area filter selects buildings with any, storage or extractor areas.");
            if (view == "topology" && search != null)
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Name searches apply to features.");
            if (sort != null && sort != "distance" && (buildings || districts || transit || view == "topology"))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "These results are ranked by distance from the center of the search area.");
            if (!buildings && !districts && !transit && !TypedNetworkMath.TryParseNetworkSort(sort, filter, out sort, out string sortError))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, sortError);
            bool deadEnds = false;
            if (request.Query.ContainsKey("include_dead_ends") && (view != "topology" || filter != TypedNetworkKinds.Road
                || !request.TryGetBool("include_dead_ends", out deadEnds)))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "Use include_dead_ends=true or false with road topology.");
            int limit = 16;
            if (request.Query.ContainsKey("limit") && (!request.TryGetInt("limit", out limit) || limit < 1 || limit > 64))
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "The result limit is 1 to 64.");
            var district = new Bounds2(bounds.xy, bounds.zw);
            IEnumerable<(float Rank, int Id, object Item)> matches;
            if (buildings) matches = OsmBuildingRows(district, buildingTypes, search, role, area);
            else if (districts) matches = OsmAdministrativeRows(bounds, search);
            else if (kind == "stop") matches = OsmStopRows(district, search, transportType);
            else if (kind == "route") matches = OsmRouteRows(district, search, transportType);
            else if (view == "topology") matches = OsmTopologyRows(district, filter, deadEnds);
            else matches = OsmNetworkRows(district, filter, roadClasses, search, sort);
            var rows = new List<(float Rank, int Id, object Item)>(limit);
            int total = 0;
            foreach (var row in matches)
            {
                total++;
                int index = rows.Count;
                while (index > 0 && (row.Rank, row.Id).CompareTo((rows[index - 1].Rank, rows[index - 1].Id)) < 0)
                {
                    index--;
                }
                if (index >= limit) continue;
                if (rows.Count == limit) rows.RemoveAt(limit - 1);
                rows.Insert(index, row);
            }
            var stamp = request.Applied;
            return BridgeResponse.Json(new
            {
                bounds = OsmQueryBounds(district), kind, view, sort = sort ?? "distance",
                observedAt = stamp == null ? null : new { frame = stamp.Frame, gameTime = stamp.GameTime },
                totalMatches = total, returned = rows.Count, truncated = total > rows.Count,
                items = rows.Select(row => row.Item).ToArray(),
            });
        }

        private IEnumerable<(float Rank, int Id, object Item)> OsmBuildingRows(
            Bounds2 bounds, CityOsmQueryTypes types, string search, string role, string area)
        {
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var operationalAreas = new PrefabOperationalAreaClassifier(EntityManager);
            EntityQuery lots = PlacedBuildingQuery;
            foreach (Entity entity in OsmDistrictObjects(bounds))
            {
                if (!EntityManager.Exists(entity) || !lots.MatchesIgnoreFilter(entity)
                    || !TryOsmBuildingLot(entity, bounds, out Entity prefab, out Transform transform, out float2 lot, out Quad2 footprint)) continue;
                string buildingType = CityOsmQueryTypes.BuildingType(OsmBuildingKind(prefab));
                if (!types.Includes(buildingType)) continue;
                string asset = PrefabDisplayName(prefabs, prefab);
                names.TryGetCustomName(entity, out string name);
                if (!MatchesOsmName(search, name, asset)) continue;
                List<string> roles = GetPrefabRoles(prefab, operationalAreas);
                if (role != null && !roles.Contains(role)) continue;
                GetBuildingOperationalCapabilities(entity, out bool storage, out bool extractor, out bool expandableStorageArea, out bool expandableExtractorArea);
                if ((area == "any" && !storage && !extractor) || (area == "storage" && !storage) || (area == "extractor" && !extractor)) continue;
                float3 position = transform.m_Position;
                float distance = math.distance(position.xz, (bounds.min + bounds.max) * 0.5f);
                yield return (distance, entity.Index, new
                {
                    wayId = CityOsm.NativeId(entity.Index),
                    entity = new { index = entity.Index, version = entity.Version },
                    prefab = asset,
                    name, buildingType, roles, bounds = OsmQueryBounds(MathUtils.Bounds(footprint)),
                    capabilities = new
                    {
                        operationalArea = storage || extractor, storageArea = storage, extractorArea = extractor,
                        expandableStorageArea, expandableExtractorArea,
                    },
                    isSubBuilding = EntityManager.HasComponent<Game.Common.Owner>(entity),
                    at = new { x = position.x, y = position.y, z = position.z },
                    footprintMeters = new { x = lot.x, z = lot.y }, distanceM = Math.Round(distance, 1),
                });
            }
        }

        private IEnumerable<(float Rank, int Id, object Item)> OsmNetworkRows(
            Bounds2 bounds, TypedNetworkKinds kind, CityOsmQueryTypes roadClasses, string search, string sort)
        {
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var streets = new Dictionary<Entity, string>();
            var assets = new Dictionary<Entity, (string Name, string RoadClass, object Road)>();
            foreach (var (entity, prefab, kinds, curve) in OsmDistrictNetworks(bounds))
            {
                if (kinds != kind && !TypedNetworkMath.MatchesFilter(kinds, kind)) continue;
                if (!assets.TryGetValue(prefab, out var asset))
                {
                    asset = (PrefabDisplayName(prefabs, prefab), null, null);
                    if (kind == TypedNetworkKinds.Road)
                    {
                        RoadPrefabFacts facts = ReadRoadPrefabFacts(prefab, prefabs);
                        asset.RoadClass = facts.RoadClass;
                        asset.Road = new
                        {
                            roadClass = facts.RoadClass, speedKmh = facts.SpeedKmh, carLanes = facts.CarLanes,
                            highwayRules = facts.HighwayRules, zonable = facts.Zonable,
                        };
                    }
                    assets.Add(prefab, asset);
                }
                if (!roadClasses.Includes(asset.RoadClass)) continue;
                string name = kind == TypedNetworkKinds.Road ? OsmStreetName(entity, names, streets) : null;
                if (!MatchesOsmName(search, name, asset.Name)) continue;
                float distance = MathUtils.Distance(curve.m_Bezier.xz, (bounds.min + bounds.max) * 0.5f, out float t);
                float volume = 0f, congestion = 0f, load = 0f;
                object traffic = kind == TypedNetworkKinds.Road ? ReadRoadTraffic(entity, out volume, out congestion) : null;
                object electricity = kind == TypedNetworkKinds.LowVoltage ? ReadLowVoltageElectricity(entity, out load) : null;
                float rank = (sort == "load" && electricity == null) || ((sort == "traffic_volume" || sort == "congestion") && traffic == null)
                    ? float.PositiveInfinity : TypedNetworkMath.NetworkListRank(sort, distance, volume, congestion, load);
                var edge = EntityManager.GetComponentData<Game.Net.Edge>(entity);
                float2 start = EntityManager.GetComponentData<Game.Net.Node>(edge.m_Start).m_Position.xz;
                float2 end = EntityManager.GetComponentData<Game.Net.Node>(edge.m_End).m_Position.xz;
                yield return (rank, entity.Index, new
                {
                    wayId = CityOsm.NativeId(entity.Index), entity = new { index = entity.Index, version = entity.Version },
                    prefab = asset.Name, name, at = OsmQuerySite(MathUtils.Position(curve.m_Bezier.xz, t)),
                    bounds = OsmQueryBounds(CityOsmSpatial.NetworkReadBounds(curve.m_Bezier.xz, start, end)),
                    start = new { nodeId = CityOsm.NativeId(edge.m_Start.Index), x = start.x, z = start.y },
                    end = new { nodeId = CityOsm.NativeId(edge.m_End.Index), x = end.x, z = end.y }, length = curve.m_Length,
                    widthM = kind == TypedNetworkKinds.Road ? NetworkWidthM(EntityManager, prefab) : null,
                    distanceM = Math.Round(distance, 1), road = asset.Road, traffic, electricity,
                });
            }
        }

        private IEnumerable<(float Rank, int Id, object Item)> OsmTopologyRows(Bounds2 bounds, TypedNetworkKinds kind, bool includeDeadEnds)
        {
            var snapshot = SnapshotTypedNetworks();
            var findings = kind == TypedNetworkKinds.Road ? TypedNetworkMath.FindRoadIssues(snapshot, bounds)
                : TypedNetworkMath.FindUtilityIsolatedFindings(snapshot, kind, bounds);
            float2 center = (bounds.min + bounds.max) * 0.5f;
            foreach (NetworkTopologyFinding finding in findings)
            {
                float2 site = finding.Anchor;
                int first = snapshot[finding.EdgeA].EntityIndex;
                var ways = finding.EdgeB >= 0 ? new[] { CityOsm.NativeId(first), CityOsm.NativeId(snapshot[finding.EdgeB].EntityIndex) } : new[] { CityOsm.NativeId(first) };
                var nodes = new List<long>(2);
                if (finding.NodeA >= 0) nodes.Add(CityOsm.NativeId(finding.NodeA));
                if (finding.NodeB >= 0) nodes.Add(CityOsm.NativeId(finding.NodeB));
                yield return (math.distance(site, center), first, new
                {
                    type = TypedNetworkMath.TopologyClassName(finding.Class), wayIds = ways, nodeIds = nodes, at = OsmQuerySite(site),
                    componentSize = finding.ComponentSize > 0 ? (int?)finding.ComponentSize : null, distanceM = Math.Round(finding.DistanceM, 1),
                });
            }
            if (!includeDeadEnds) yield break;
            foreach (NetworkDeadEnd end in TypedNetworkMath.FindRoadDeadEnds(snapshot))
            {
                TypedNetworkEdge edge = snapshot[end.Edge];
                float2 site = (end.Node == edge.StartNode ? edge.Points[0] : edge.Points[edge.Points.Length - 1]).xz;
                if (!MathUtils.Intersect(bounds, site)) continue;
                yield return (math.distance(site, center), edge.EntityIndex, new
                {
                    type = "dead_end", wayIds = new[] { CityOsm.NativeId(edge.EntityIndex) },
                    nodeIds = new[] { CityOsm.NativeId(end.Node) }, degree = end.Degree, at = OsmQuerySite(site),
                });
            }
        }

        private static bool MatchesOsmName(string query, string name, string prefab) => string.IsNullOrEmpty(query)
            || name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || prefab?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private static object OsmQuerySite(float2 site) => new { x = site.x, z = site.y };

        private static object OsmQueryBounds(Bounds2 bounds) => new { xMin = bounds.min.x, zMin = bounds.min.y, xMax = bounds.max.x, zMax = bounds.max.y };
    }
}
