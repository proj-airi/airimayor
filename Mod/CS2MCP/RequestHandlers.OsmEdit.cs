using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse ApplyOsmEdit(BridgeRequest request, float4 bounds, OsmDocument before, OsmDocument after)
        {
            CityOsmGeometry current = CityOsm.Import(before);
            CityOsmGeometry target = CityOsm.Import(after);
            List<CityOsmEditItem> changes = CityOsmEdit.Changes(current, target, bounds);
            if (changes.Count == 0)
            {
                return OsmFileResponse(bounds, before);
            }
            var areas = PlanOsmAreas(bounds, before, after, changes);

            var roads = new List<OsmRoadPlacement>();
            var buildings = new List<OsmBuildingPlacement>();
            var additionalPlacements = new List<OsmBuildingPlacement>();
            var deletions = new List<Entity>();
            var routes = new List<OsmTransitEdit>();
            var changedRoadAssets = new Dictionary<long, (Entity Prefab, bool Reverse)>();
            List<OsmEditRoadAsset> assets = null;
            TerrainHeightData terrain = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            var elevations = new Dictionary<long, float>();
            foreach (CityOsmEditItem change in changes)
            {
                if (!(change.Edited is OsmWay way) || !CityOsm.IsLinearWay(way))
                {
                    continue;
                }
                var oldWay = (OsmWay)change.Original;
                Entity original = OsmOriginal(oldWay);
                Entity prefab;
                bool reverse = false;
                TypedNetworkKinds transport = CityOsmTransportWays.Kind(way);
                if (transport != TypedNetworkKinds.None) prefab = ResolveOsmTransportWay(oldWay, way, original, transport);
                else if (CityOsm.IsUtility(way))
                {
                    prefab = ResolveOsmUtility(oldWay, way, original);
                }
                else
                {
                    CityOsmEdit.CheckEditedTags(oldWay, way, OsmRoadUpgrades.EditableRoadTags);
                    prefab = ResolveOsmRoad(ref assets, oldWay, way, original, out reverse);
                    CheckOsmStreetName(oldWay, way, original, after);
                }
                if (!CityOsm.IsUtility(way) && OsmRoadGrade(way) != MapGrade.Ground)
                    AddOsmDeckElevations(way, prefab, target, elevations, ref terrain);
                changedRoadAssets.Add(way.Id, (prefab, reverse));
            }
            foreach (var pair in elevations)
            {
                float3 position = target.Positions[pair.Key];
                position.y = pair.Value;
                target.MoveNode(pair.Key, position);
            }
            foreach (var change in changes)
            {
                if (!(change.Edited is OsmWay way) || !changedRoadAssets.TryGetValue(way.Id, out var asset))
                {
                    continue;
                }
                foreach (long id in way.NodeIds)
                {
                    float3 point = target.Positions[id];
                    if (math.isfinite(point.y))
                    {
                        continue;
                    }
                    if (target.Document.Nodes[id].Tags.ContainsKey("ele"))
                    {
                        throw new FormatException($"Node {id} needs an elevation in meters.");
                    }
                    point.y = CityOsm.IsUtility(way) ? OsmUtilityHeight(asset.Prefab, point, ref terrain)
                        : TerrainUtils.SampleHeight(ref terrain, point);
                    target.MoveNode(id, point);
                }
            }

            var deckNodes = new HashSet<long>(after.Ways.Values.Where(way => CityOsm.IsLinearWay(way) && !CityOsm.IsUtility(way)
                && OsmRoadGrade(way) != MapGrade.Ground).SelectMany(way => way.NodeIds));
            var targetRoads = target.Roads.ToDictionary(way => way.Id);
            var targetBuildings = target.Buildings.ToDictionary(way => way.Id);
            var affected = new HashSet<Entity>();
            foreach (CityOsmEditItem change in changes)
            {
                if (change.Edited is OsmRelation || change.Original is OsmRelation)
                {
                    routes.Add(PlanOsmTransit(change.Original as OsmRelation, change.Edited as OsmRelation, before, after, bounds));
                    continue;
                }
                if (change.Edited is OsmNode || change.Original is OsmNode)
                {
                    if (TryPlanOsmStandalonePoint(change.Original as OsmNode, change.Edited as OsmNode, ref terrain, additionalPlacements, deletions)) continue;
                    if (!TryPlanOsmStop(change.Original, change.Edited, current, target, buildings, deletions))
                    {
                        throw new FormatException($"Node {(change.Edited ?? change.Original).Id} does not describe a supported point feature.");
                    }
                    continue;
                }
                var originalWay = (OsmWay)change.Original;
                var editedWay = (OsmWay)change.Edited;
                Entity original = OsmOriginal(originalWay);
                if (original != Entity.Null)
                {
                    affected.Add(original);
                }
                bool wasRoad = originalWay != null && CityOsm.IsLinearWay(originalWay);
                bool wasBuilding = originalWay != null && originalWay.Tags.ContainsKey("building");
                if (editedWay == null)
                {
                    if (!wasRoad && !wasBuilding)
                    {
                        throw new FormatException($"Way {originalWay.Id} has no supported city object to remove.");
                    }
                    deletions.Add(original);
                }
                else if (targetRoads.TryGetValue(editedWay.Id, out CityOsmRoad road))
                {
                    if (wasBuilding || (originalWay != null && (CityOsm.IsUtility(originalWay) != CityOsm.IsUtility(editedWay)
                        || CityOsmTransportWays.Kind(originalWay) != CityOsmTransportWays.Kind(editedWay))))
                    {
                        throw new FormatException($"Way {editedWay.Id} cannot change its kind while retaining its city identity.");
                    }
                    var asset = changedRoadAssets[editedWay.Id];
                    AddOsmRoadCourses(originalWay, editedWay, original, asset.Prefab, asset.Reverse, road, target, deckNodes, bounds, ref terrain, roads);
                }
                else if (targetBuildings.TryGetValue(editedWay.Id, out CityOsmBuilding building) && !wasRoad)
                {
                    var upgrades = CityOsmFacilityUpgrades.Additions(before, after, originalWay, editedWay);
                    if (upgrades.Count > 0)
                    {
                        foreach (string upgrade in upgrades)
                            additionalPlacements.Add(PlanOsmFacilityUpgrade(original, editedWay.Id, upgrade, bounds));
                        continue;
                    }
                    CityOsmEdit.CheckEditedTags(originalWay, editedWay, "building", "amenity", "airimayor:prefab", "name");
                    buildings.Add(ResolveOsmBuilding(originalWay, editedWay, original, building, ref terrain));
                }
                else
                {
                    throw new FormatException($"Way {editedWay.Id} must describe a supported road, utility or building.");
                }
            }
            foreach (OsmBuildingPlacement placement in buildings)
            {
                if (placement.AttachParent != Entity.Null && affected.Contains(placement.AttachParent))
                {
                    throw new FormatException($"Complete the road edit and read the new curb before attaching stop {placement.WayId}.");
                }
            }
            CheckOsmMovedJunctions(current, target, affected);
            buildings.AddRange(additionalPlacements);
            CheckOsmTransitBatch(routes, buildings, deletions);
            CheckOsmAreaDependencies(areas, roads, buildings, deletions);
            bool needsConstruction = roads.Count + buildings.Count + routes.Count > 0 || areas.HasEdits;
            BridgeResponse CheckPermissions()
            {
                if (needsConstruction && !airimayor.Setting.StaticAllowConstruction)
                    return BridgeResponse.Error(BridgeErrorKind.Conflict, "This edit requires construction permission.");
                if (deletions.Count > 0 && !airimayor.Setting.StaticAllowDemolition)
                    return BridgeResponse.Error(BridgeErrorKind.Conflict, "Removing a mapped feature requires demolition permission.");
                return null;
            }
            BridgeResponse denied = CheckPermissions();
            if (denied != null) return denied;
            BridgeResponse CheckCurrent()
            {
                return CheckPermissions() ?? CheckOsmRevision(request, bounds);
            }
            var tool = World.GetOrCreateSystemManaged<BridgeToolSystem>();
            BridgeResponse Readback(IEnumerable<(OsmKind Kind, long Source, Entity Entity)> realized)
            {
                OsmDocument district = ReadOsmDocument(bounds);
                var features = district.Elements.Where(element => element.Tags.ContainsKey("airimayor:entity"))
                    .ToLookup(element => (element.Kind, CityOsm.Tag(element, "airimayor:entity")));
                var matches = new Dictionary<string, List<string>>();
                foreach (var result in realized.Distinct())
                {
                    OsmElement mapped = features[(result.Kind, OsmEntityIdentity(result.Entity))].FirstOrDefault();
                    if (mapped == null) return null;
                    string prefix = result.Kind.ToString().ToLowerInvariant() + "/";
                    string source = prefix + result.Source.ToString(CultureInfo.InvariantCulture);
                    if (!matches.TryGetValue(source, out var refs)) matches.Add(source, refs = new List<string>());
                    refs.Add(prefix + mapped.Id.ToString(CultureInfo.InvariantCulture));
                }
                var remaining = new HashSet<string>(features.Select(group => group.Key.Item2));
                if (!MapOsmAreaResults(areas, district, matches)) return null;
                return deletions.Concat(routes.Where(route => route.Remove).Select(route => route.Original))
                    .Any(entity => remaining.Contains(OsmEntityIdentity(entity))) ? null : OsmFileResponse(bounds, district, matches);
            }
            BridgeResponse ReadFailure(BridgeErrorKind kind, string message)
            {
                OsmDocument district = ReadOsmDocument(bounds);
                string content = district.Write();
                BridgeResponse response = BridgeResponse.Json(new
                {
                    error = message, kind = kind.ToString().ToLowerInvariant(),
                    revision = CityOsmFile.Revision(m_System.CityLoadId, bounds, content),
                    observedChanges = OsmChange.Between(before, district).Write()
                });
                response.Success = false;
                response.ErrorKind = kind;
                response.Files = new Dictionary<string, byte[]> { ["district.osm"] = System.Text.Encoding.UTF8.GetBytes(content) };
                return response;
            }
            return tool.TryQueueOsmEdit(request, bounds, roads, buildings, deletions, routes, areas, CheckCurrent, Readback, ReadFailure)
                ? null : BridgeResponse.Error(BridgeErrorKind.Conflict, "Another city operation is in progress.");
        }

        private Entity OsmOriginal(OsmElement original)
        {
            if (original == null) return Entity.Null;
            string identity = CityOsm.Tag(original, "airimayor:entity");
            string[] parts = identity?.Split(':');
            if (parts == null || parts.Length != 2 || !int.TryParse(parts[0], out int index)
                || !int.TryParse(parts[1], out int version) || !TryResolveExistingEntity(index, version, out Entity entity))
            {
                throw new FormatException($"{original.Kind} {original.Id} no longer identifies the same city object.");
            }
            return entity;
        }

        private void CheckOsmMovedJunctions(CityOsmGeometry current, CityOsmGeometry target, HashSet<Entity> affected)
        {
            foreach (OsmNode node in current.Document.Nodes.Values)
            {
                if (!node.Tags.ContainsKey("airimayor:entity") || (target.Document.Nodes.TryGetValue(node.Id, out OsmNode edited)
                    && CityOsmEdit.SameNodePosition(node, edited)))
                {
                    continue;
                }
                Entity entity = OsmOriginal(node);
                if (!EntityManager.HasBuffer<ConnectedEdge>(entity))
                {
                    continue;
                }
                foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(entity, true))
                {
                    if (!affected.Contains(connection.m_Edge))
                    {
                        throw new FormatException($"Junction {node.Id} also serves an unedited connection. Include every connected road before moving it.");
                    }
                }
            }
        }

        private void CheckOsmStreetName(OsmWay original, OsmWay edited, Entity edge, OsmDocument after)
        {
            if (edge == Entity.Null || OsmRequestedName(original, edited) == null || !EntityManager.HasComponent<Aggregated>(edge))
            {
                return;
            }
            Entity aggregate = EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate;
            if (!EntityManager.HasBuffer<AggregateElement>(aggregate))
            {
                throw new FormatException($"Read the entire named street before renaming way {edited.Id}.");
            }
            string target = CityOsm.Tag(edited, "name");
            foreach (AggregateElement part in EntityManager.GetBuffer<AggregateElement>(aggregate, true))
            {
                if (!after.Ways.TryGetValue(1L + part.m_Edge.Index, out OsmWay way)
                    || CityOsm.Tag(way, "airimayor:entity") != OsmEntityIdentity(part.m_Edge)
                    || CityOsm.Tag(way, "name") != target)
                {
                    throw new FormatException($"Name all parts of the existing street together when renaming way {edited.Id}.");
                }
            }
        }

        private static string OsmRequestedName(OsmElement original, OsmElement edited)
        {
            string name = CityOsm.Tag(edited, "name");
            return name == CityOsm.Tag(original, "name") ? null : name ?? string.Empty;
        }

        private static MapGrade OsmRoadGrade(OsmWay way)
        {
            string layer = CityOsm.Tag(way, "layer");
            string bridge = CityOsm.Tag(way, "bridge");
            string tunnel = CityOsm.Tag(way, "tunnel");
            bool raised = bridge != null && bridge != "no";
            bool buried = tunnel != null && tunnel != "no";
            if ((raised && buried) || (layer != null && (!int.TryParse(layer, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int rank) || math.sign(rank) != (raised ? 1 : buried ? -1 : 0))))
            {
                throw new FormatException($"Way {way.Id} needs compatible bridge, tunnel and relative layer tags.");
            }
            return raised ? MapGrade.Bridge : buried ? MapGrade.Tunnel : MapGrade.Ground;
        }

        private void AddOsmDeckElevations(OsmWay way, Entity prefab, CityOsmGeometry city,
            Dictionary<long, float> elevations, ref TerrainHeightData terrain)
        {
            var geometry = EntityManager.GetComponentData<NetGeometryData>(prefab);
            var placeable = EntityManager.GetComponentData<PlaceableNetData>(prefab);
            float step = World.GetOrCreateSystemManaged<NetToolSystem>().elevationStep;

            int layer = CityOsm.Tag(way, "layer") is string raw
                ? int.Parse(raw, CultureInfo.InvariantCulture) : OsmRoadGrade(way) == MapGrade.Tunnel ? -1 : 1;
            TerrainHeightData site = terrain;
            CityOsmEdit.SupplyDeckHeights(way, city, elevations, layer, geometry.m_ElevationLimit, step,
                new float2(placeable.m_ElevationRange.min, placeable.m_ElevationRange.max),
                point => TerrainUtils.SampleHeight(ref site, point));
        }

        private void AddOsmRoadCourses(OsmWay originalWay, OsmWay way, Entity original, Entity prefab,
            bool reverse, CityOsmRoad road, CityOsmGeometry target, HashSet<long> raisedNodes, float4 bounds,
            ref TerrainHeightData terrain, List<OsmRoadPlacement> placements)
        {
            bool isUtility = CityOsm.IsUtility(way);
            bool roadUpgrades = !isUtility && CityOsmTransportWays.Kind(way) == TypedNetworkKinds.None;
            Entity source = original;
            bool invertedWay = originalWay != null && (way.NodeIds[0] == originalWay.NodeIds.Last()
                || way.NodeIds.Last() == originalWay.NodeIds[0]);
            bool invertNative = invertedWay != reverse;
            CompositionFlags existing = source != Entity.Null && EntityManager.HasComponent<Upgraded>(source)
                ? EntityManager.GetComponentData<Upgraded>(source).m_Flags : default;
            CompositionFlags requested = !roadUpgrades ? existing : OsmRoadUpgrades.Read(way,
                invertedWay ? NetCompositionHelpers.InvertCompositionFlags(existing) : existing);
            if (reverse) requested = NetCompositionHelpers.InvertCompositionFlags(requested);
            CompositionFlags previous = invertNative ? NetCompositionHelpers.InvertCompositionFlags(existing) : existing;
            CompositionFlags added = requested & ~previous;
            if (roadUpgrades && added != default) CheckOsmRoadUpgrades(way.Id, prefab, added);
            MapGrade? grade = isUtility ? (MapGrade?)null : OsmRoadGrade(way);
            bool explicitHeights = way.NodeIds.Any(id => target.Document.Nodes[id].Tags.ContainsKey("ele"));
            int start = 0;
            foreach (CityOsmSection section in road.Sections)
            {
                int end = way.NodeIds.IndexOf(section.EndNode, start + 1);
                foreach (var piece in CityOsmEdit.Courses(way.NodeIds, start, end, section))
                {
                    long startId = reverse ? piece.End : piece.Start;
                    long endId = reverse ? piece.Start : piece.End;
                    RoadPath curve = reverse ? new RoadPath(piece.Curve.D, piece.Curve.C, piece.Curve.B, piece.Curve.A) : piece.Curve;
                    if (startId == endId || !math.all(math.isfinite(curve.A)) || !math.all(math.isfinite(curve.D))
                        || math.distancesq(curve.A.xz, curve.D.xz) < 0.01f)
                    {
                        throw new FormatException($"Way {way.Id} needs distinct connected points and a finite course.");
                    }
                    bool approach = grade == MapGrade.Ground
                        && (raisedNodes.Contains(startId) || raisedNodes.Contains(endId))
                        && !way.Tags.ContainsKey("bridge") && !way.Tags.ContainsKey("tunnel") && !way.Tags.ContainsKey("layer");
                    bool ground = grade == MapGrade.Ground && !approach && !explicitHeights;
                    if (ground)
                    {
                        var native = new Curve { m_Bezier = new Bezier4x3(curve.A, curve.B, curve.C, curve.D) };
                        var fitted = NetUtils.AdjustPosition(native, true, false, true, ref terrain).m_Bezier;
                        curve = new RoadPath(fitted.a, fitted.b, fitted.c, fitted.d);
                    }
                    CityOsmEdit.RequireCourseInside(way.Id, curve, bounds);
                    placements.Add(new OsmRoadPlacement
                    {
                        WayId = way.Id, StartId = startId, EndId = endId, Curve = curve, Original = original,
                        UpgradeSource = source, InvertUpgrades = invertNative, Upgrades = requested,
                        WriteUpgrades = roadUpgrades && (original == Entity.Null || requested != previous),
                        AddedUpgrades = added,
                        StartPosition = target.Positions[startId], EndPosition = target.Positions[endId],
                        Prefab = prefab, Start = OsmEndpoint(target.Document.Nodes[startId]), End = OsmEndpoint(target.Document.Nodes[endId]),
                        Ground = ground, Grade = approach ? null : grade, Name = OsmRequestedName(originalWay, way),
                    });
                    original = Entity.Null;
                }
                start = end;
            }
        }

        private Entity OsmEndpoint(OsmNode node) => node.Tags.ContainsKey("airimayor:entity") ? OsmOriginal(node) : Entity.Null;

        private void CheckOsmRoadUpgrades(long way, Entity prefab, CompositionFlags added)
        {
            var road = EntityManager.GetComponentData<NetData>(prefab);
            if ((added.m_General & ~road.m_GeneralFlagMask) != 0 ||
                ((added.m_Left | added.m_Right) & ~road.m_SideFlagMask) != 0)
                throw new FormatException($"The road asset for way {way} does not support the requested upgrades.");
            CompositionFlags available = default;
            using (var assets = NetPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity asset in assets)
                {
                    if (!EntityManager.HasComponent<PlaceableNetData>(asset) || IsLocked(asset)) continue;
                    var upgrade = EntityManager.GetComponentData<PlaceableNetData>(asset);
                    if ((upgrade.m_PlacementFlags & (PlacementFlags.IsUpgrade | PlacementFlags.UpgradeOnly)) == 0) continue;
                    available |= upgrade.m_SetUpgradeFlags;
                }
            }
            CompositionFlags.Side sides = available.m_Left | available.m_Right;
            if ((added.m_General & ~available.m_General) != 0 || ((added.m_Left | added.m_Right) & ~sides) != 0)
                throw new FormatException($"The city has no unlocked upgrade for the requested features on way {way}.");
        }
    }
}
