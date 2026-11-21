using System;
using System.Collections.Generic;
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
        private const int TransitListDefaultLimit = 16;
        private const int TransitListHardMax = 64;

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

        private BridgeResponse ListTransitStops(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!TryReadOptionalTransportType(request, out TransportType? typeFilter, out BridgeResponse typeError))
            {
                return typeError;
            }

            int limit = request.TryGetInt("limit", out int rawLimit)
                ? math.clamp(rawLimit, 1, TransitListHardMax)
                : TransitListDefaultLimit;
            if (!TryGetOptionalCenter(request, out bool hasCenter, out float2 center, out BridgeResponse centerError))
            {
                return centerError;
            }
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 250f;

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var found = new List<(float distance, object item)>();
            int total = 0;
            using (NativeArray<Entity> entities = TransitStopQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
                    if (!EntityManager.HasComponent<TransportStopData>(prefabRef.m_Prefab))
                    {
                        continue;
                    }
                    TransportStopData stopData =
                        EntityManager.GetComponentData<TransportStopData>(prefabRef.m_Prefab);
                    if (typeFilter.HasValue && stopData.m_TransportType != typeFilter.Value)
                    {
                        continue;
                    }
                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    if (hasCenter && math.distance(transform.m_Position.xz, center) > radius)
                    {
                        continue;
                    }
                    total++;
                    float distance = hasCenter ? math.distance(transform.m_Position.xz, center) : 0f;
                    int connectedLines = 0;
                    if (EntityManager.HasBuffer<ConnectedRoute>(entity))
                    {
                        connectedLines = EntityManager.GetBuffer<ConnectedRoute>(entity, isReadOnly: true).Length;
                    }
                    string ownerPrefab = null;
                    if (EntityManager.HasComponent<Owner>(entity))
                    {
                        ownerPrefab = GetEntityPrefabName(
                            prefabSystem,
                            EntityManager.GetComponentData<Owner>(entity).m_Owner);
                    }
                    var item = new
                    {
                        entity = new { index = entity.Index, version = entity.Version },
                        prefab = GetEntityPrefabName(prefabSystem, entity),
                        type = FormatTransportType(stopData.m_TransportType),
                        passenger = stopData.m_PassengerTransport,
                        cargo = stopData.m_CargoTransport,
                        connectedLines,
                        ownerPrefab,
                        position = new
                        {
                            x = transform.m_Position.x,
                            y = transform.m_Position.y,
                            z = transform.m_Position.z,
                        },
                        distanceM = hasCenter ? (double?)Math.Round(distance, 1) : null,
                    };
                    AddBoundedItem(found, item, distance, limit, hasCenter);
                }
            }

            if (hasCenter)
            {
                found.Sort((a, b) => a.distance.CompareTo(b.distance));
            }
            var stops = new List<object>(found.Count);
            foreach ((float _, object item) in found)
            {
                stops.Add(item);
            }
            return BridgeResponse.Json(new
            {
                stopCount = stops.Count,
                total,
                truncated = total > stops.Count,
                stops,
                note = "Passenger lines can connect these existing stops.",
            });
        }

        private BridgeResponse ListTransitLines(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }

            if (!TryReadOptionalTransportType(request, out TransportType? typeFilter, out BridgeResponse typeError))
            {
                return typeError;
            }

            int limit = request.TryGetInt("limit", out int rawLimit)
                ? math.clamp(rawLimit, 1, TransitListHardMax)
                : TransitListDefaultLimit;
            if (!TryGetOptionalCenter(request, out bool hasCenter, out float2 center, out BridgeResponse centerError))
            {
                return centerError;
            }
            float radius = request.TryGetFloat("radius", out float rawRadius) ? math.max(rawRadius, 1f) : 250f;
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            NativeArray<UITransportLineData> sorted =
                TransportUIUtils.GetSortedLines(TransitLineQuery, EntityManager, prefabSystem);
            try
            {
                var lines = new List<object>();
                int total = 0;
                for (int i = 0; i < sorted.Length; i++)
                {
                    UITransportLineData line = sorted[i];
                    if (typeFilter.HasValue && line.type != typeFilter.Value)
                    {
                        continue;
                    }
                    if (hasCenter && !TransitLineIntersectsRadius(line.entity, center, radius))
                    {
                        continue;
                    }
                    total++;
                    if (lines.Count >= limit)
                    {
                        continue;
                    }
                    lines.Add(new
                    {
                        entity = new { index = line.entity.Index, version = line.entity.Version },
                        prefab = GetEntityPrefabName(prefabSystem, line.entity),
                        type = FormatTransportType(line.type),
                        active = line.active,
                        isCargo = line.isCargo,
                        lengthM = Math.Round(line.length, 1),
                        stops = line.stops,
                        vehicles = line.vehicles,
                        usage = Math.Round(line.usage, 3),
                    });
                }
                return BridgeResponse.Json(new
                {
                    lineCount = lines.Count,
                    total,
                    truncated = total > lines.Count,
                    lines,
                    note = "read-only snapshot; optional x/z/radius keeps lines with a stop inside the range; vehicles spawn from depots",
                });
            }
            finally
            {
                if (sorted.IsCreated)
                {
                    sorted.Dispose();
                }
            }
        }

        private bool TryReadOptionalTransportType(
            BridgeRequest request,
            out TransportType? typeFilter,
            out BridgeResponse error)
        {
            typeFilter = null;
            error = null;
            if (!request.Query.TryGetValue("type", out string typeRaw)
                || string.IsNullOrWhiteSpace(typeRaw))
            {
                return true;
            }
            if (!TryParsePassengerTransportType(typeRaw, out TransportType parsedType, out error))
            {
                return false;
            }
            typeFilter = parsedType;
            return true;
        }

        private bool TryParsePassengerTransportType(
            string raw,
            out TransportType transportType,
            out BridgeResponse error)
        {
            transportType = TransportType.None;
            error = null;
            if (!Enum.TryParse(raw.Trim(), true, out transportType)
                || transportType == TransportType.None
                || transportType == TransportType.Count)
            {
                error = BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    "type must be a passenger transport kind such as bus, tram, train, subway or ferry");
                return false;
            }
            if (transportType == TransportType.Taxi
                || transportType == TransportType.Work
                || transportType == TransportType.Post
                || transportType == TransportType.Bicycle
                || transportType == TransportType.Car)
            {
                error = BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    $"{FormatTransportType(transportType)} is not a simple passenger line; use bus (default) or another passenger type with matching stops");
                return false;
            }
            return true;
        }

        private static string FormatTransportType(TransportType type)
        {
            return Enum.GetName(typeof(TransportType), type)?.ToLowerInvariant() ?? type.ToString();
        }

        private bool TransitLineIntersectsRadius(Entity line, float2 center, float radius)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }
            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            float radiusSq = radius * radius;
            foreach (RouteWaypoint waypoint in waypoints)
            {
                if (!EntityManager.Exists(waypoint.m_Waypoint)
                    || !EntityManager.HasComponent<Position>(waypoint.m_Waypoint))
                {
                    continue;
                }
                float3 position = EntityManager.GetComponentData<Position>(waypoint.m_Waypoint).m_Position;
                if (math.distancesq(position.xz, center) <= radiusSq)
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddBoundedItem(
            List<(float distance, object item)> found,
            object item,
            float distance,
            int limit,
            bool hasCenter)
        {
            if (found.Count < limit)
            {
                found.Add((distance, item));
                return;
            }
            if (!hasCenter)
            {
                return;
            }
            int worst = 0;
            for (int j = 1; j < found.Count; j++)
            {
                if (found[j].distance > found[worst].distance)
                {
                    worst = j;
                }
            }
            if (distance < found[worst].distance)
            {
                found[worst] = (distance, item);
            }
        }
    }
}
