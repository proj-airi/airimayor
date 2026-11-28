using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;
using TransportStop = Game.Routes.TransportStop;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_TransitStopQuery;
        private bool m_TransitStopQueryCreated;
        private EntityQuery m_TransitLineQuery;
        private bool m_TransitLineQueryCreated;
        private EntityQuery m_TransportLinePrefabQuery;
        private bool m_TransportLinePrefabQueryCreated;

        private EntityQuery TransitStopQuery
        {
            get
            {
                if (!m_TransitStopQueryCreated)
                {
                    m_TransitStopQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<TransportStop>(),
                            ComponentType.ReadOnly<Transform>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Temp>(),
                            ComponentType.ReadOnly<Deleted>(),
                        },
                    });
                    m_TransitStopQueryCreated = true;
                }
                return m_TransitStopQuery;
            }
        }

        private EntityQuery TransitLineQuery
        {
            get
            {
                if (!m_TransitLineQueryCreated)
                {
                    m_TransitLineQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Route>(),
                            ComponentType.ReadOnly<TransportLine>(),
                            ComponentType.ReadOnly<RouteWaypoint>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Temp>(),
                            ComponentType.ReadOnly<Deleted>(),
                        },
                    });
                    m_TransitLineQueryCreated = true;
                }
                return m_TransitLineQuery;
            }
        }

        private EntityQuery TransportLinePrefabQuery
        {
            get
            {
                if (!m_TransportLinePrefabQueryCreated)
                {
                    m_TransportLinePrefabQuery = EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<TransportLineData>());
                    m_TransportLinePrefabQueryCreated = true;
                }
                return m_TransportLinePrefabQuery;
            }
        }

        private IEnumerable<(float Rank, int Id, object Item)> OsmStopRows(
            Bounds2 district, string search, TransportType? transportType)
        {
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            float2 center = (district.min + district.max) * 0.5f;
            using (var stops = TransitStopQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity stop in stops)
                {
                    float3 position = EntityManager.GetComponentData<Transform>(stop).m_Position;
                    if (!MathUtils.Intersect(district, position.xz)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
                    if (!EntityManager.HasComponent<TransportStopData>(prefab)) continue;
                    var data = EntityManager.GetComponentData<TransportStopData>(prefab);
                    if (transportType.HasValue && data.m_TransportType != transportType.Value) continue;
                    string asset = PrefabDisplayName(prefabs, prefab);
                    string name = names.GetRenderedLabelName(stop);
                    if (!MatchesOsmName(search, name, asset)) continue;

                    Entity station = StationOwningPassengerStop(stop, data);
                    var extent = new Bounds2(position.xz, position.xz);
                    if (EntityManager.HasComponent<Transform>(station))
                        extent |= EntityManager.GetComponentData<Transform>(station).m_Position.xz;
                    long? nodeId = station != Entity.Null || OsmCurbStopMode(prefab) != null
                        ? CityOsm.NativeId(stop.Index) : (long?)null;
                    Entity owner = EntityManager.HasComponent<Owner>(stop)
                        ? EntityManager.GetComponentData<Owner>(stop).m_Owner : Entity.Null;
                    float distance = math.distance(center, position.xz);
                    yield return (distance, stop.Index, new
                    {
                        nodeId,
                        entity = new { index = stop.Index, version = stop.Version },
                        prefab = asset,
                        name,
                        type = FormatTransportType(data.m_TransportType),
                        passenger = data.m_PassengerTransport,
                        cargo = data.m_CargoTransport,
                        connectedLines = EntityManager.HasBuffer<ConnectedRoute>(stop)
                            ? EntityManager.GetBuffer<ConnectedRoute>(stop, isReadOnly: true).Length : 0,
                        ownerPrefab = owner == Entity.Null ? null : GetEntityPrefabName(prefabs, owner),
                        position = new { x = position.x, y = position.y, z = position.z },
                        bounds = OsmTransitQueryBounds(extent),
                        distanceM = Math.Round(distance, 1),
                    });
                }
            }
        }

        private IEnumerable<(float Rank, int Id, object Item)> OsmRouteRows(
            Bounds2 district, string search, TransportType? transportType)
        {
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            foreach (Entity entity in OsmRouteCandidates(district))
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (!EntityManager.HasComponent<TransportLineData>(prefab)) continue;
                var data = EntityManager.GetComponentData<TransportLineData>(prefab);
                if (transportType.HasValue && data.m_TransportType != transportType.Value) continue;
                string asset = PrefabDisplayName(prefabs, prefab);
                string name = names.GetRenderedLabelName(entity);
                if (!MatchesOsmName(search, name, asset)) continue;
                if (!TryMeasureOsmRoute(entity, district, out Bounds2 extent, out float distance)) continue;

                UITransportLineData line = TransportUIUtils.BuildTransportLine(entity, EntityManager, prefabs);
                yield return (distance, entity.Index, new
                {
                    relationId = IsMappedPassengerRoute(data) ? CityOsm.NativeId(entity.Index) : (long?)null,
                    entity = new { index = entity.Index, version = entity.Version },
                    prefab = asset,
                    name,
                    type = FormatTransportType(line.type),
                    active = line.active,
                    isCargo = line.isCargo,
                    lengthM = Math.Round(line.length, 1),
                    stops = line.stops,
                    vehicles = line.vehicles,
                    usage = Math.Round(line.usage, 3),
                    bounds = OsmTransitQueryBounds(extent),
                    distanceM = Math.Round(distance, 1),
                });
            }
        }

        private bool TryMeasureOsmRoute(Entity line, Bounds2 district, out Bounds2 extent, out float distance)
        {
            extent = new Bounds2(float.PositiveInfinity, float.NegativeInfinity);
            distance = float.PositiveInfinity;
            float2 center = (district.min + district.max) * 0.5f;
            bool intersects = false;
            foreach (RouteWaypoint entry in EntityManager.GetBuffer<RouteWaypoint>(line, true))
            {
                if (!TryReadOsmRoutePoint(entry.m_Waypoint, out _, out float3 position)) continue;
                extent |= position.xz;
                intersects |= MathUtils.Intersect(district, position.xz);
                distance = math.min(distance, math.distance(center, position.xz));
            }
            foreach (Bezier4x3 curve in OsmTransitPath.Curves(EntityManager, line))
            {
                extent |= MathUtils.Bounds(curve.xz);
                intersects = intersects || CityOsmSpatial.Intersects(district, curve.xz);
                distance = math.min(distance, MathUtils.Distance(curve.xz, center, out _));
            }
            return intersects;
        }

        private static object OsmTransitQueryBounds(Bounds2 extent)
        {
            float4 bounds = CityOsmTransit.ReadBounds(new float4(extent.min, extent.max));
            return OsmQueryBounds(new Bounds2(bounds.xy, bounds.zw));
        }

        private static string FormatTransportType(TransportType type)
        {
            return Enum.GetName(typeof(TransportType), type)?.ToLowerInvariant() ?? type.ToString();
        }
    }
}
