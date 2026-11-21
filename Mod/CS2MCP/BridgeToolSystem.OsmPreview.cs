using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class BridgeToolSystem
    {
        private struct OsmRoadWitness
        {
            public long WayId;
            public CompositionFlags Upgrades;
            public Entity Entity, Preview, Prefab, Start, End;
            public Bezier4x3 Curve;
            public MapGrade? Grade;
            public string Name;
        }

        private void CaptureOsmPreview()
        {
            var used = new HashSet<Entity>();
            var nodes = new Dictionary<long, Entity>();
            var nodeIds = new Dictionary<Entity, long>();
            var interiorNodes = new HashSet<Entity>();
            var deleted = new HashSet<Entity>();
            var allowedRemovals = new HashSet<Entity>(m_OsmEdit.Deletions);
            foreach (OsmRoadPlacement road in m_OsmEdit.Roads) allowedRemovals.Add(road.Original);
            foreach (OsmBuildingPlacement building in m_OsmEdit.Buildings) allowedRemovals.Add(building.Original);

            using (NativeArray<Entity> edges = m_OsmTempEdges.ToEntityArray(Allocator.Temp))
            using (NativeArray<Entity> objects = m_OsmTempObjects.ToEntityArray(Allocator.Temp))
            {
                var edgeStarts = edges.ToLookup(entity => PreviewLocation(entity, EntityManager.GetComponentData<Curve>(entity).m_Bezier.a));
                var objectPositions = objects.ToLookup(entity => PreviewLocation(entity, EntityManager.GetComponentData<Transform>(entity).m_Position));
                foreach (Entity edge in edges) CheckOsmDeletion(edge, allowedRemovals, deleted);
                foreach (Entity obj in objects)
                {
                    if (EntityManager.HasComponent<Game.Buildings.Building>(obj) ||
                        EntityManager.HasComponent<Game.Routes.TransportStop>(obj))
                        CheckOsmDeletion(obj, allowedRemovals, deleted);
                }
                foreach (Entity target in m_OsmEdit.Deletions)
                    if (!deleted.Contains(target)) throw new InvalidOperationException("An intended removal is missing from the preview.");

                foreach (OsmRoadPlacement road in m_OsmEdit.Roads)
                {
                    MatchOsmCourse(road, edgeStarts, used, nodes, nodeIds, interiorNodes);
                }
                if (interiorNodes.Overlaps(nodeIds.Keys))
                    throw new InvalidOperationException("The preview connected a street between its declared OSM junctions.");
                interiorNodes.UnionWith(nodeIds.Keys);
                foreach (Entity entity in edges)
                {
                    if (used.Contains(entity)) continue;
                    Temp temp = EntityManager.GetComponentData<Temp>(entity);
                    if ((temp.m_Flags & (TempFlags.Cancel | TempFlags.Delete)) != 0) continue;
                    Edge edge = EntityManager.GetComponentData<Edge>(entity);
                    if (HasUnmappedConnection(edge.m_Start, interiorNodes) || HasUnmappedConnection(edge.m_End, interiorNodes))
                        throw new InvalidOperationException("Another street or utility would meet the new route without a shared OSM node.");
                }
                foreach (OsmBuildingPlacement obj in m_OsmEdit.Buildings)
                {
                    Entity found = Entity.Null;
                    foreach (Entity candidate in NearbyPreviews(objectPositions, obj.Prefab, obj.Position))
                    {
                        Temp temp = EntityManager.GetComponentData<Temp>(candidate);
                        if (temp.m_Original != obj.Original || (temp.m_Flags & (TempFlags.Cancel | TempFlags.Delete)) != 0 ||
                            !MatchesOsmObject(obj, candidate) || used.Contains(candidate)) continue;
                        if (found != Entity.Null) throw new InvalidOperationException("Ambiguous object preview for OSM feature " + obj.WayId + ".");
                        found = candidate;
                    }
                    if (found == Entity.Null) throw new InvalidOperationException("Missing placement or attachment for OSM feature " + obj.WayId + ".");
                    used.Add(found);
                    m_OsmEdit.Objects.Add((obj, obj.Original == Entity.Null ? found : obj.Original));
                }
            }
        }

        private void CheckOsmDeletion(Entity candidate, HashSet<Entity> allowed, HashSet<Entity> deleted)
        {
            Temp temp = EntityManager.GetComponentData<Temp>(candidate);
            if (temp.m_Original == Entity.Null || (temp.m_Flags & (TempFlags.Delete | TempFlags.Replace | TempFlags.Combine)) == 0) return;
            if (!allowed.Contains(temp.m_Original))
                throw new InvalidOperationException("The preview would remove another mapped feature.");
            if ((temp.m_Flags & TempFlags.Delete) != 0) deleted.Add(temp.m_Original);
        }

        private (Entity, int2) PreviewLocation(Entity entity, float3 position) =>
            (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab, (int2)math.floor(position.xz));

        private static IEnumerable<Entity> NearbyPreviews(ILookup<(Entity, int2), Entity> locations, Entity prefab, float3 position)
        {
            int2 cell = (int2)math.floor(position.xz);
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    foreach (Entity entity in locations[(prefab, cell + new int2(x, y))])
                        yield return entity;
        }

        private void MatchOsmCourse(OsmRoadPlacement road, ILookup<(Entity, int2), Entity> edgeStarts,
            HashSet<Entity> used, Dictionary<long, Entity> nodes, Dictionary<Entity, long> nodeIds, HashSet<Entity> interiorNodes)
        {
            Bezier4x3 expected = road.Bezier;
            float progress = 0f;
            Entity nextNode = Entity.Null;
            bool foundOriginal = road.Original == Entity.Null;
            while (progress < 1f)
            {
                Entity found = Entity.Null;
                float end = progress;
                foreach (Entity candidate in NearbyPreviews(edgeStarts, road.Prefab, MathUtils.Position(expected, progress)))
                {
                    if (used.Contains(candidate)) continue;
                    Temp temp = EntityManager.GetComponentData<Temp>(candidate);
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != 0 ||
                        (temp.m_Original != Entity.Null && temp.m_Original != road.Original)) continue;
                    Edge edge = EntityManager.GetComponentData<Edge>(candidate);
                    if (nextNode != Entity.Null && edge.m_Start != nextNode) continue;
                    Bezier4x3 curve = EntityManager.GetComponentData<Curve>(candidate).m_Bezier;
                    if (!MatchesCourseSpan(expected, curve, progress, out float next)) continue;
                    if (found != Entity.Null) throw new InvalidOperationException("Ambiguous street or utility route for OSM way " + road.WayId + ".");
                    found = candidate;
                    end = next;
                }
                if (found == Entity.Null) throw new InvalidOperationException("The preview changed the course or its junctions for OSM way " + road.WayId + ".");
                if (road.Grade.HasValue && !MatchesOsmGrade(found, road.Grade.Value))
                    throw new InvalidOperationException("The preview changed the road grade for OSM way " + road.WayId + ".");
                if (RoadUpgrades(found) != road.Upgrades)
                    throw new InvalidOperationException("The preview changed road upgrades for OSM way " + road.WayId + ".");
                if (road.AddedUpgrades != default)
                {
                    Entity composition = EntityManager.GetComponentData<Composition>(found).m_Edge;
                    var effective = EntityManager.GetComponentData<NetCompositionData>(composition).m_Flags;
                    if ((effective & road.AddedUpgrades) != road.AddedUpgrades)
                        throw new InvalidOperationException("The requested upgrades do not appear on the preview of way " + road.WayId + ".");
                }
                Edge match = EntityManager.GetComponentData<Edge>(found);
                Entity start = OsmAppliedIdentity(match.m_Start);
                Entity finish = OsmAppliedIdentity(match.m_End);
                if (progress == 0f) CheckOsmJunction(road.StartId, road.Start, match.m_Start, road.StartPosition, nodes, nodeIds);
                else if (!EntityManager.HasComponent<Temp>(start) || !interiorNodes.Add(start))
                    throw new InvalidOperationException("The preview added a junction that is missing from the OSM document.");
                if (end == 1f) CheckOsmJunction(road.EndId, road.End, match.m_End, road.EndPosition, nodes, nodeIds);
                used.Add(found);
                foundOriginal |= EntityManager.GetComponentData<Temp>(found).m_Original == road.Original;
                m_OsmEdit.Edges.Add(new OsmRoadWitness
                {
                    WayId = road.WayId, Upgrades = road.Upgrades, Entity = OsmAppliedIdentity(found), Preview = found, Prefab = road.Prefab, Start = start, End = finish,
                    Curve = EntityManager.GetComponentData<Curve>(found).m_Bezier, Grade = road.Grade, Name = road.Name
                });
                nextNode = match.m_End;
                progress = end;
            }
            if (!foundOriginal) throw new InvalidOperationException("The preview did not replace the original OSM way " + road.WayId + ".");
        }

        private static bool MatchesCourseSpan(Bezier4x3 expected, Bezier4x3 actual, float start, out float end)
        {
            end = start;
            float3 startPosition = MathUtils.Position(expected, start);
            if (math.distance(actual.a.xz, startPosition.xz) > 0.5f || math.abs(actual.a.y - startPosition.y) > 1f)
                return false;
            for (int i = 1; i <= 4; i++)
            {
                float3 point = MathUtils.Position(actual, i * 0.25f);
                float distance = MathUtils.Distance(expected.xz, point.xz, out float t);
                if (i == 4 && math.distance(point.xz, expected.d.xz) <= 0.5f) t = 1f;
                if (distance > 0.5f || t <= end || math.abs(point.y - MathUtils.Position(expected, t).y) > 1f)
                    return false;
                end = t;
            }
            return true;
        }

        private void CheckOsmJunction(long id, Entity anchor, Entity preview, float3 expectedPosition,
            Dictionary<long, Entity> nodes, Dictionary<Entity, long> nodeIds)
        {
            Entity node = OsmAppliedIdentity(preview);
            float3 position = EntityManager.GetComponentData<Node>(preview).m_Position;
            if ((anchor != Entity.Null && anchor != node) ||
                (anchor == Entity.Null && !EntityManager.HasComponent<Temp>(node)) ||
                (nodes.TryGetValue(id, out Entity existing) && existing != node) ||
                (nodeIds.TryGetValue(node, out long otherId) && otherId != id) ||
                math.distance(position.xz, expectedPosition.xz) > 0.5f || math.abs(position.y - expectedPosition.y) > 1f)
                throw new InvalidOperationException("The preview changed the connection at OSM node " + id + ".");
            nodes[id] = node;
            nodeIds[node] = id;
            m_OsmEdit.Junctions[node] = (id, position);
        }

        private bool HasUnmappedConnection(Entity node, HashSet<Entity> courseNodes)
        {
            Entity live = OsmAppliedIdentity(node);
            return courseNodes.Contains(live) && EntityManager.HasComponent<Temp>(live);
        }

        private Entity OsmAppliedIdentity(Entity entity)
        {
            if (!EntityManager.HasComponent<Temp>(entity)) return entity;
            Temp temp = EntityManager.GetComponentData<Temp>(entity);
            return temp.m_Original != Entity.Null && (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) == 0
                ? temp.m_Original : entity;
        }

        private bool MatchesOsmObject(OsmBuildingPlacement obj, Entity entity)
        {
            if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab != obj.Prefab) return false;
            Transform transform = EntityManager.GetComponentData<Transform>(entity);
            if (math.distance(obj.Position.xz, transform.m_Position.xz) > 0.5f ||
                (math.isfinite(obj.Position.y) && math.abs(obj.Position.y - transform.m_Position.y) > 1f) ||
                math.abs(math.dot(obj.Rotation.value, transform.m_Rotation.value)) < 0.9999f) return false;
            if (obj.AttachParent == Entity.Null) return true;
            return EntityManager.HasComponent<Game.Objects.Attached>(entity) &&
                OsmAppliedIdentity(EntityManager.GetComponentData<Game.Objects.Attached>(entity).m_Parent) == obj.AttachParent;
        }

        private bool MatchesOsmGrade(Entity entity, MapGrade grade)
        {
            if (!EntityManager.HasComponent<Composition>(entity)) return false;
            Entity edge = EntityManager.GetComponentData<Composition>(entity).m_Edge;
            return RequestHandlers.ReadRoadGrade(EntityManager, edge) == grade;
        }

        private void VerifyAppliedOsmEdit()
        {
            foreach (var junction in m_OsmEdit.Junctions)
            {
                if (!IsAppliedOsmFeature(junction.Key) || !EntityManager.HasComponent<Node>(junction.Key) ||
                    math.distance(junction.Value.Position, EntityManager.GetComponentData<Node>(junction.Key).m_Position) > 0.5f)
                    throw new InvalidOperationException("A junction differs from the accepted preview.");
            }
            foreach (Entity target in m_OsmEdit.Deletions)
                if (EntityManager.Exists(target) && !EntityManager.HasComponent<Deleted>(target))
                    throw new InvalidOperationException("A requested removal could not be confirmed.");
            foreach (OsmRoadWitness item in m_OsmEdit.Edges)
            {
                Entity entity = item.Entity;
                if (!IsAppliedOsmFeature(entity) || EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab != item.Prefab ||
                    !EntityManager.HasComponent<Edge>(entity) || !EntityManager.HasComponent<Curve>(entity))
                    throw new InvalidOperationException("A street or utility segment could not be confirmed.");
                Edge edge = EntityManager.GetComponentData<Edge>(entity);
                Bezier4x3 curve = EntityManager.GetComponentData<Curve>(entity).m_Bezier;
                if (edge.m_Start != item.Start || edge.m_End != item.End || RoadUpgrades(entity) != item.Upgrades ||
                    math.distance(curve.a, item.Curve.a) > 0.5f || math.distance(curve.b, item.Curve.b) > 0.5f ||
                    math.distance(curve.c, item.Curve.c) > 0.5f || math.distance(curve.d, item.Curve.d) > 0.5f ||
                    (item.Grade.HasValue && !MatchesOsmGrade(entity, item.Grade.Value)))
                    throw new InvalidOperationException("The built network differs from the accepted preview.");
            }
            foreach (var obj in m_OsmEdit.Objects)
            {
                if (!IsAppliedOsmFeature(obj.Entity) || !MatchesOsmObject(obj.Placement, obj.Entity))
                    throw new InvalidOperationException("Placement could not be confirmed for OSM feature " + obj.Placement.WayId + ".");
            }
        }

        private bool IsAppliedOsmFeature(Entity entity) => EntityManager.Exists(entity) &&
            !EntityManager.HasComponent<Deleted>(entity) && !EntityManager.HasComponent<Temp>(entity) &&
            EntityManager.HasComponent<PrefabRef>(entity);

        private CompositionFlags RoadUpgrades(Entity entity) => EntityManager.HasComponent<Upgraded>(entity)
            ? EntityManager.GetComponentData<Upgraded>(entity).m_Flags : default;

        private IEnumerable<(OsmKind, long, Entity)> AppliedOsmEntities()
        {
            foreach (var junction in m_OsmEdit.Junctions) yield return (OsmKind.Node, junction.Value.Id, junction.Key);
            foreach (OsmRoadWitness road in m_OsmEdit.Edges) yield return (OsmKind.Way, road.WayId, road.Entity);
            foreach (var obj in m_OsmEdit.Objects)
                yield return (obj.Placement.AttachParent == Entity.Null ? OsmKind.Way : OsmKind.Node, obj.Placement.WayId, obj.Entity);
            foreach (var route in m_OsmEdit.Routes)
                if (!route.Remove) yield return (OsmKind.Relation, route.RelationId, route.Realized);
        }

        private bool CheckOsmNames(bool preview)
        {
            var names = new Dictionary<Entity, string>();
            var namedEdges = new Dictionary<Entity, string>();
            var streets = new HashSet<Entity>();
            foreach (var item in m_OsmEdit.Objects)
            {
                if (item.Placement.Name != null) names.Add(item.Entity, item.Placement.Name);
            }
            foreach (OsmRoadWitness item in m_OsmEdit.Edges)
            {
                if (item.Name == null) continue;
                Entity edge = preview ? item.Preview : item.Entity;
                namedEdges[edge] = item.Name;
                if (preview)
                {
                    Entity original = EntityManager.GetComponentData<Temp>(edge).m_Original;
                    if (original != Entity.Null) namedEdges[original] = item.Name;
                }
                Entity street = EntityManager.HasComponent<Aggregated>(edge)
                    ? EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate : Entity.Null;
                if (!EntityManager.HasBuffer<AggregateElement>(street))
                {
                    if (preview) continue;
                    return false;
                }
                if (names.TryGetValue(street, out string name) && name != item.Name)
                    throw new InvalidOperationException("Two names refer to the same street.");
                names[street] = item.Name;
                streets.Add(street);
            }
            if (names.Count == 0) return true;
            foreach (Entity street in streets)
            {
                DynamicBuffer<AggregateElement> members = EntityManager.GetBuffer<AggregateElement>(street, true);
                if (members.Length == 0 && !preview) return false;
                foreach (AggregateElement member in members)
                {
                    if (!namedEdges.TryGetValue(member.m_Edge, out string intended) || intended != names[street])
                        throw new InvalidOperationException("The street name would also change a segment outside the edit.");
                }
            }
            if (preview) return true;
            var service = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            if (!m_OsmEdit.NamesApplied)
            {
                foreach (var pair in names) service.SetCustomName(pair.Key, pair.Value);
                m_OsmEdit.NamesApplied = true;
            }
            foreach (var pair in names)
            {
                bool exists = service.TryGetCustomName(pair.Key, out string actual);
                if (pair.Value.Length == 0 ? exists : !exists || actual != pair.Value)
                    throw new InvalidOperationException("The city name change could not be confirmed.");
            }
            return true;
        }
    }
}
