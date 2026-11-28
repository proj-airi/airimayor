using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private string OsmRoadPrefabId(Entity prefab, PrefabSystem prefabs)
        {
            return prefabs.TryGetPrefab<RoadPrefab>(prefab, out var asset)
                && AssetDatabase.global.resources.prefabsMap.TryGetGuid(asset, out string id) ? id : null;
        }

        private bool IsOsmMotorwayLink(Entity entity, Edge edge, (int? Count, string Direction) traffic,
            Dictionary<Entity, bool> classified)
        {
            if (!traffic.Count.HasValue || !OsmOneWay(traffic.Direction)) return false;
            if (classified.TryGetValue(entity, out bool link)) return link;
            Entity aggregate = OsmRoadAggregate(entity);
            if (aggregate == Entity.Null) return false;
            var arm = new HashSet<Entity> { entity };
            var pending = new Queue<(Entity Road, Entity Node, bool Enters)>();
            pending.Enqueue((entity, edge.m_Start, traffic.Direction == "-1"));
            pending.Enqueue((entity, edge.m_End, traffic.Direction == "yes"));
            while (pending.Count != 0)
            {
                var current = pending.Dequeue();
                if (!EntityManager.HasBuffer<ConnectedEdge>(current.Node) || EntityManager.HasComponent<Roundabout>(current.Node)) continue;
                var mainline = new List<(int Aggregate, int Lanes, bool Enters, bool Leaves)>();
                var continuation = Entity.Null;
                var continuationEnd = Entity.Null;
                int adjacentRoads = 0;
                foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(current.Node, true))
                {
                    Entity road = connection.m_Edge;
                    if (road == current.Road || !EntityManager.HasComponent<Road>(road)
                        || EntityManager.HasComponent<Deleted>(road) || EntityManager.HasComponent<Temp>(road)) continue;
                    adjacentRoads++;
                    if (!EntityManager.HasComponent<PrefabRef>(road) || !EntityManager.HasComponent<Composition>(road)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
                    if (!EntityManager.HasComponent<RoadData>(prefab)
                        || (EntityManager.GetComponentData<RoadData>(prefab).m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) == 0) continue;
                    Entity neighborAggregate = OsmRoadAggregate(road);
                    if (neighborAggregate == Entity.Null) continue;
                    Entity composition = EntityManager.GetComponentData<Composition>(road).m_Edge;
                    var lanes = OsmCarLanes(composition);
                    if (!lanes.Count.HasValue || lanes.Direction == null) continue;
                    Edge neighbor = EntityManager.GetComponentData<Edge>(road);
                    bool startsHere = neighbor.m_Start == current.Node;
                    bool enters = lanes.Direction == "no" || (startsHere ? lanes.Direction == "-1" : lanes.Direction == "yes");
                    bool leaves = lanes.Direction == "no" || !enters;
                    if (neighborAggregate != aggregate)
                    {
                        mainline.Add((neighborAggregate.Index, lanes.Count.Value, enters, leaves));
                    }
                    else if (lanes.Count == traffic.Count && OsmOneWay(lanes.Direction) && enters != current.Enters)
                    {
                        continuation = road;
                        continuationEnd = startsHere ? neighbor.m_End : neighbor.m_Start;
                    }
                }
                link |= CityOsmRoadTags.IsLinkJunction(aggregate.Index, traffic.Count.Value, mainline);
                if (adjacentRoads == 1 && continuation != Entity.Null && arm.Add(continuation))
                    pending.Enqueue((continuation, continuationEnd, current.Enters));
            }

            foreach (Entity road in arm) classified[road] = link;
            return link;
        }

        private Entity OsmRoadAggregate(Entity road)
        {
            return EntityManager.HasComponent<Aggregated>(road)
                ? EntityManager.GetComponentData<Aggregated>(road).m_Aggregate : Entity.Null;
        }

        private static bool OsmOneWay(string direction) => direction == "yes" || direction == "-1";
    }
}
