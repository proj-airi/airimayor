using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    internal sealed class OsmTransitEdit
    {
        public long RelationId;
        public Entity Original, Prefab, Realized;
        public Entity[] Stops = Array.Empty<Entity>();
        public float4 Bounds;
        public string Name;
        public bool Remove, NameApplied;
    }

    internal static class OsmTransitPath
    {
        internal static IEnumerable<Bezier4x3> Curves(EntityManager manager, Entity line)
        {
            if (!manager.HasBuffer<RouteSegment>(line)) yield break;
            foreach (RouteSegment entry in manager.GetBuffer<RouteSegment>(line, true))
            {
                Entity segment = entry.m_Segment;
                if (manager.HasBuffer<CurveElement>(segment))
                {
                    var curves = manager.GetBuffer<CurveElement>(segment, true);
                    if (curves.Length > 0)
                    {
                        foreach (CurveElement curve in curves) yield return curve.m_Curve;
                        continue;
                    }
                }
                if (!manager.HasBuffer<PathElement>(segment)) continue;
                foreach (PathElement step in manager.GetBuffer<PathElement>(segment, true))
                    if (manager.HasComponent<Game.Net.Curve>(step.m_Target))
                        yield return MathUtils.Cut(manager.GetComponentData<Game.Net.Curve>(step.m_Target).m_Bezier, step.m_TargetDelta);
            }
        }
    }

    public sealed partial class BridgeToolSystem
    {
        private void CreateOsmTransitDefinitions(IReadOnlyList<OsmTransitEdit> routes)
        {
            foreach (OsmTransitEdit item in routes)
            {
                EntityCommandBuffer buffer = m_ToolOutputBarrier.CreateCommandBuffer();
                Entity definition = buffer.CreateEntity();
                buffer.AddComponent(definition, new CreationDefinition
                {
                    m_Prefab = item.Prefab, m_Original = item.Original, m_Flags = item.Remove ? CreationFlags.Delete : 0
                });
                buffer.AddComponent(definition, default(Updated));
                var waypoints = buffer.AddBuffer<WaypointDefinition>(definition);
                if (item.Remove) continue;
                var originals = new Dictionary<Entity, Entity>();
                if (item.Original != Entity.Null)
                {
                    foreach (RouteWaypoint entry in EntityManager.GetBuffer<RouteWaypoint>(item.Original, true))
                    {
                        Entity old = entry.m_Waypoint;
                        if (EntityManager.HasComponent<Connected>(old)) originals[EntityManager.GetComponentData<Connected>(old).m_Connected] = old;
                    }
                }
                foreach (Entity stop in item.Stops)
                {
                    originals.TryGetValue(stop, out Entity old);
                    waypoints.Add(new WaypointDefinition(EntityManager.GetComponentData<Transform>(stop).m_Position)
                    {
                        m_Connection = stop, m_Original = old
                    });
                }
                if (item.Original == Entity.Null) waypoints.Add(waypoints[0]);
            }
        }

        private bool OsmTransitPreviewReady(IReadOnlyList<OsmTransitEdit> routes)
        {
            if (routes.Count == 0) return true;
            applyMode = ApplyMode.None;
            OsmTransitEdit item = routes[0];
            using (var previews = m_TempRouteQuery.ToEntityArray(Allocator.Temp))
            {
                if (previews.Length == 0) return false;
                if (previews.Length != 1)
                    throw new InvalidOperationException("Another passenger service is being previewed.");
                Entity line = previews[0];
                Temp temp = EntityManager.GetComponentData<Temp>(line);
                if (temp.m_Original != item.Original || ((temp.m_Flags & TempFlags.Delete) != 0) != item.Remove)
                    throw new InvalidOperationException($"Passenger route {item.RelationId} is no longer the service being edited.");
                if (!item.Remove && !OsmTransitPathReady(item, line)) return false;
                item.Realized = item.Original == Entity.Null ? line : item.Original;
                return true;
            }
        }

        private bool OsmTransitPathReady(OsmTransitEdit item, Entity line)
        {
            if (EntityManager.GetComponentData<PrefabRef>(line).m_Prefab != item.Prefab ||
                (EntityManager.GetComponentData<Route>(line).m_Flags & RouteFlags.Complete) == 0)
                throw new InvalidOperationException($"The city did not form a complete passenger loop for route {item.RelationId}.");
            var waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            var segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (waypoints.Length != item.Stops.Length || segments.Length != waypoints.Length)
                throw new InvalidOperationException($"Passenger route {item.RelationId} changed its boarding points.");
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                Entity next = waypoints[(i + 1) % waypoints.Length].m_Waypoint;
                Entity segment = segments[i].m_Segment;
                if (!IsAppliedOsmFeature(item.Stops[i]) || !EntityManager.HasComponent<Connected>(waypoint) ||
                    EntityManager.GetComponentData<Connected>(waypoint).m_Connected != item.Stops[i])
                    throw new InvalidOperationException($"Boarding point {i + 1} differs on route {item.RelationId}.");
                if (!EntityManager.HasComponent<PathInformation>(segment) || !EntityManager.HasComponent<PathTargets>(segment))
                    return false;
                var info = EntityManager.GetComponentData<PathInformation>(segment);
                if ((info.m_State & (PathFlags.Pending | PathFlags.Scheduled | PathFlags.Obsolete)) != 0) return false;
                var targets = EntityManager.GetComponentData<PathTargets>(segment);
                if (math.distancesq(targets.m_ReadyStartPosition, EntityManager.GetComponentData<Position>(waypoint).m_Position) >= 1f ||
                    math.distancesq(targets.m_ReadyEndPosition, EntityManager.GetComponentData<Position>(next).m_Position) >= 1f)
                    return false;
                if (info.m_Distance < 0f || (info.m_State & (PathFlags.Failed | PathFlags.Stuck)) != 0)
                    throw new InvalidOperationException($"Passenger route {item.RelationId} cannot travel from boarding point {i + 1} to {(i + 1) % waypoints.Length + 1}.");
                if (!EntityManager.HasBuffer<PathElement>(segment) || EntityManager.GetBuffer<PathElement>(segment, true).Length == 0)
                    return false;
            }
            var extent = new Bounds2(float.PositiveInfinity, float.NegativeInfinity);
            foreach (Bezier4x3 curve in OsmTransitPath.Curves(EntityManager, line)) extent |= MathUtils.Bounds(curve.xz);
            if (math.any(extent.min < item.Bounds.xy) || math.any(extent.max > item.Bounds.zw))
                throw new InvalidOperationException($"Route {item.RelationId} travels beyond the district. Read bounds {CityOsmTransit.Extent(new float4(math.min(extent.min, item.Bounds.xy), math.max(extent.max, item.Bounds.zw)))}.");
            return true;
        }

        private bool VerifyOsmTransitResult(IReadOnlyList<OsmTransitEdit> routes)
        {
            foreach (OsmTransitEdit item in routes)
            {
                if (item.Remove)
                {
                    if (EntityManager.Exists(item.Original) && !EntityManager.HasComponent<Deleted>(item.Original)) return false;
                    continue;
                }
                if (!IsAppliedOsmFeature(item.Realized) || !OsmTransitPathReady(item, item.Realized)) return false;
                if (item.Name == null) continue;
                var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
                if (!item.NameApplied)
                {
                    names.SetCustomName(item.Realized, item.Name);
                    item.NameApplied = true;
                }
                bool named = names.TryGetCustomName(item.Realized, out string actual);
                if (item.Name.Length == 0 ? named : !named || actual != item.Name)
                    throw new InvalidOperationException($"Passenger route {item.RelationId} did not retain its name.");
            }
            return true;
        }
    }
}
