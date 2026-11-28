using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private struct OsmRouteIterator : INativeQuadTreeIterator<RouteSearchItem, QuadTreeBoundsXZ>
        {
            public Bounds2 Bounds;
            public HashSet<Entity> Parts;

            public bool Intersect(QuadTreeBoundsXZ item) => MathUtils.Intersect(Bounds, item.m_Bounds.xz);
            public void Iterate(QuadTreeBoundsXZ item, RouteSearchItem value)
            {
                if (Intersect(item)) Parts.Add(value.m_Entity);
            }
        }

        private OsmNode ReadOsmStationStop(Entity stop, Bounds2 district, PrefabSystem prefabs, Game.UI.NameSystem names)
        {
            if (!TransitStopQuery.MatchesIgnoreFilter(stop)) return null;
            float3 position = EntityManager.GetComponentData<Transform>(stop).m_Position;
            if (!MathUtils.Intersect(district, position.xz)) return null;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
            if (!EntityManager.HasComponent<TransportStopData>(prefab)) return null;
            var data = EntityManager.GetComponentData<TransportStopData>(prefab);
            Entity station = StationOwningPassengerStop(stop, data);
            if (station == Entity.Null) return null;
            var node = CityOsm.ToNode(CityOsm.NativeId(stop.Index), position);
            node.Tags["public_transport"] = "platform";
            node.Tags[FormatTransportType(data.m_TransportType)] = "yes";
            node.Tags["airimayor:entity"] = OsmEntityIdentity(stop);
            node.Tags["airimayor:station"] = CityOsm.NativeId(station.Index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            node.Tags["airimayor:prefab"] = PrefabDisplayName(prefabs, prefab);
            if (names.TryGetCustomName(stop, out string name)) node.Tags["name"] = name;
            return node;
        }

        private void ReadOsmTransit(OsmDocument document, float4 bounds)
        {
            var district = new Bounds2(bounds.xy, bounds.zw);
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            var stations = document.Ways.Values.Where(way => way.Tags.ContainsKey("building")).Select(OsmOriginal)
                .Where(entity => EntityManager.HasComponent<Game.Buildings.TransportStation>(entity));
            foreach (Entity child in TransitObjects(stations))
            {
                OsmNode platform = ReadOsmStationStop(child, district, prefabs, names);
                if (platform != null) document.Nodes[platform.Id] = platform;
            }
            foreach (Entity line in OsmRouteCandidates(district))
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                if (!EntityManager.HasComponent<TransportLineData>(prefab)) continue;
                var data = EntityManager.GetComponentData<TransportLineData>(prefab);
                if (!IsMappedPassengerRoute(data)) continue;
                var relation = new OsmRelation(CityOsm.NativeId(line.Index));
                var extent = new Bounds2(float.PositiveInfinity, float.NegativeInfinity);
                bool intersects = false;
                bool complete = true;
                foreach (RouteWaypoint entry in EntityManager.GetBuffer<RouteWaypoint>(line, true))
                {
                    Entity waypoint = entry.m_Waypoint;
                    if (!TryReadOsmRoutePoint(waypoint, out Entity stop, out float3 position))
                    {
                        complete = false;
                        continue;
                    }
                    extent |= position.xz;
                    if (!MathUtils.Intersect(district, position.xz)) continue;
                    intersects = true;
                    long id = CityOsm.NativeId(stop.Index);
                    if (stop != Entity.Null && !document.Nodes.ContainsKey(id))
                    {
                        OsmNode platform = ReadOsmStationStop(stop, district, prefabs, names);
                        if (platform != null) document.Nodes[platform.Id] = platform;
                    }
                    if (stop != Entity.Null && document.Nodes.TryGetValue(id, out OsmNode node) && CityOsm.IsPlatform(node))
                    {
                        relation.Members.Add(new OsmMember(OsmKind.Node, id, "platform"));
                    }
                    else
                    {
                        var via = CityOsm.ToNode(CityOsm.NativeId(waypoint.Index), position);
                        via.Tags["airimayor:entity"] = OsmEntityIdentity(waypoint);
                        via.Tags["airimayor:waypoint"] = "yes";
                        document.Nodes[via.Id] = via;
                        relation.Members.Add(new OsmMember(OsmKind.Node, via.Id, "via"));
                    }
                }
                foreach (Bezier4x3 curve in OsmTransitPath.Curves(EntityManager, line))
                {
                    extent |= MathUtils.Bounds(curve.xz);
                    intersects = intersects || CityOsmSpatial.Intersects(district, curve.xz);
                }
                if (!intersects) continue;
                relation.Tags["type"] = "route";
                relation.Tags["route"] = FormatTransportType(data.m_TransportType);
                relation.Tags["airimayor:entity"] = OsmEntityIdentity(line);
                if (!complete) relation.Tags["airimayor:incomplete"] = "yes";
                if (math.any(extent.min < bounds.xy) || math.any(extent.max > bounds.zw))
                    relation.Tags[CityOsmTransit.RequiredBounds] = CityOsmTransit.Extent(new float4(extent.min, extent.max));
                if (names.TryGetCustomName(line, out string name)) relation.Tags["name"] = name;
                document.Relations.Add(relation.Id, relation);
            }
        }

        private HashSet<Entity> OsmRouteCandidates(Bounds2 district)
        {
            var iterator = new OsmRouteIterator { Bounds = district, Parts = new HashSet<Entity>() };
            var tree = World.GetOrCreateSystemManaged<Game.Routes.SearchSystem>().GetSearchTree(true, out JobHandle dependencies);
            dependencies.Complete();
            tree.Iterate(ref iterator);
            var lines = new HashSet<Entity>();
            foreach (Entity part in iterator.Parts)
            {
                if (!EntityManager.HasComponent<Owner>(part)) continue;
                Entity line = EntityManager.GetComponentData<Owner>(part).m_Owner;
                if (EntityManager.Exists(line) && TransitLineQuery.MatchesIgnoreFilter(line)) lines.Add(line);
            }
            return lines;
        }

        private bool TryReadOsmRoutePoint(Entity waypoint, out Entity stop, out float3 position)
        {
            stop = Entity.Null;
            position = default;
            if (!EntityManager.HasComponent<Position>(waypoint)) return false;
            if (EntityManager.HasComponent<Connected>(waypoint))
                stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            position = EntityManager.HasComponent<Transform>(stop)
                ? EntityManager.GetComponentData<Transform>(stop).m_Position
                : EntityManager.GetComponentData<Position>(waypoint).m_Position;
            return true;
        }

        private static bool IsMappedPassengerRoute(TransportLineData data) =>
            data.m_PassengerTransport && !data.m_CargoTransport;

        private Entity StationOwningPassengerStop(Entity stop, TransportStopData data) =>
            data.m_PassengerTransport ? StationOwningStop(stop) : Entity.Null;

        private Entity StationOwningStop(Entity stop)
        {
            Entity current = stop;
            while (EntityManager.HasComponent<Owner>(current))
            {
                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (EntityManager.HasComponent<Game.Buildings.Building>(current)) return current;
            }
            return Entity.Null;
        }

        private IEnumerable<Entity> TransitObjects(IEnumerable<Entity> roots)
        {
            var children = new Stack<Entity>(roots);
            var visited = new HashSet<Entity>();
            while (children.Count > 0)
            {
                Entity child = children.Pop();
                if (!EntityManager.Exists(child) || !visited.Add(child)) continue;
                yield return child;
                if (EntityManager.HasBuffer<Game.Objects.SubObject>(child))
                    foreach (var obj in EntityManager.GetBuffer<Game.Objects.SubObject>(child, true)) children.Push(obj.m_SubObject);
                if (EntityManager.HasBuffer<Game.Buildings.InstalledUpgrade>(child))
                    foreach (var upgrade in EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(child, true)) children.Push(upgrade.m_Upgrade);
            }
        }

        private OsmTransitEdit PlanOsmTransit(OsmRelation original, OsmRelation edited,
            OsmDocument before, OsmDocument after, float4 bounds)
        {
            CityOsmTransit.CheckScope(original);
            if (CityOsm.Tag(original, "airimayor:incomplete") != null)
                throw new FormatException($"Route {original.Id} has unresolved city waypoints. Read it again before editing.");
            Entity line = OsmOriginal(original);
            var plan = new OsmTransitEdit { RelationId = (original ?? edited).Id, Original = line, Remove = edited == null, Bounds = bounds };
            if (line != Entity.Null)
            {
                if (!TransitLineQuery.MatchesIgnoreFilter(line))
                    throw new FormatException($"Route {original.Id} no longer identifies a passenger line.");
                plan.Prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            }
            if (plan.Remove) return plan;
            CityOsmEdit.CheckEditedTags(original, edited, "type", "route", "name");
            string mode = CityOsm.Tag(edited, "route");
            if (!CityOsmTransit.IsRoute(edited) || !Enum.TryParse(mode, true, out TransportType type) ||
                mode != FormatTransportType(type) || (original != null && CityOsm.Tag(original, "route") != mode))
                throw new FormatException($"Route {edited.Id} needs a passenger mode matching its city service.");
            if (original != null && original.Members.Any(member => member.Role != "platform"))
                throw new FormatException($"Route {edited.Id} includes city navigation waypoints. Its boarding order cannot be replaced.");
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            if (line == Entity.Null) plan.Prefab = UnlockedOsmPassengerLine(type, prefabs);
            long[] members = CityOsmTransit.Stops(edited, before, after, bounds);
            plan.Stops = members.Select(id => OsmOriginal(before.Nodes[id])).ToArray();
            plan.Name = OsmRequestedName(original, edited);
            float minDistance = RouteUtils.GetMinWaypointDistance(EntityManager.GetComponentData<RouteData>(plan.Prefab));
            for (int i = 0; i < plan.Stops.Length; i++)
            {
                Entity stop = plan.Stops[i];
                Entity stopPrefab = EntityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
                if (!TransitStopQuery.MatchesIgnoreFilter(stop) || !EntityManager.HasComponent<TransportStopData>(stopPrefab) ||
                    EntityManager.HasComponent<TaxiStand>(stop) || !EntityManager.HasBuffer<ConnectedRoute>(stop))
                    throw new FormatException($"Platform {members[i]} cannot serve passenger route {edited.Id}.");
                var data = EntityManager.GetComponentData<TransportStopData>(stopPrefab);
                if (!data.m_PassengerTransport || data.m_TransportType != type)
                    throw new FormatException($"Platform {members[i]} cannot board {mode} passengers.");
                float3 a = EntityManager.GetComponentData<Transform>(stop).m_Position;
                float3 b = EntityManager.GetComponentData<Transform>(plan.Stops[(i + 1) % plan.Stops.Length]).m_Position;
                if (math.distance(a, b) < minDistance)
                    throw new FormatException($"Route {edited.Id} needs {minDistance:F1} m between successive boarding points, including its return leg.");
            }
            return plan;
        }

        private Entity UnlockedOsmPassengerLine(TransportType type, PrefabSystem prefabs)
        {
            using (var candidates = TransportLinePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in candidates)
                {
                    var data = EntityManager.GetComponentData<TransportLineData>(entity);
                    if (data.m_TransportType == type && data.m_PassengerTransport && !data.m_CargoTransport &&
                        !IsLocked(entity) && prefabs.GetPrefab<PrefabBase>(entity) is TransportLinePrefab) return entity;
                }
            }
            throw new FormatException($"No unlocked passenger {FormatTransportType(type)} service is available.");
        }

        private void CheckOsmTransitBatch(IReadOnlyList<OsmTransitEdit> routes, IReadOnlyList<OsmBuildingPlacement> buildings,
            IReadOnlyList<Entity> deletions)
        {
            if (routes.Count > 1) throw new FormatException("The city previews one passenger service at a time. Submit each route relation in its own edit.");
            var sites = deletions.Where(entity => EntityManager.HasComponent<Game.Buildings.Building>(entity) ||
                EntityManager.HasComponent<Game.Routes.TransportStop>(entity));
            foreach (Entity stop in TransitObjects(sites))
            {
                if (!EntityManager.HasBuffer<ConnectedRoute>(stop)) continue;
                foreach (ConnectedRoute connection in EntityManager.GetBuffer<ConnectedRoute>(stop, true))
                {
                    Entity waypoint = connection.m_Waypoint;
                    if (!EntityManager.HasComponent<Owner>(waypoint)) continue;
                    Entity line = EntityManager.GetComponentData<Owner>(waypoint).m_Owner;
                    if (routes.Count == 0 || routes[0].Original != line || routes[0].Stops.Contains(stop))
                        throw new FormatException($"Platform {CityOsm.NativeId(stop.Index)} serves route {CityOsm.NativeId(line.Index)}. Update the service before removing the stop or station.");
                }
            }
            if (routes.Count == 0 || routes[0].Remove) return;
            var changed = new HashSet<Entity>(deletions);
            foreach (var building in buildings) if (building.Original != Entity.Null) changed.Add(building.Original);
            foreach (Entity stop in routes[0].Stops)
                if (changed.Contains(stop) || changed.Contains(StationOwningStop(stop)))
                    throw new FormatException("Complete the platform or station change before planning the passenger service.");
        }
    }
}
