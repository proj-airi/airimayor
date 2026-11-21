using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private static bool TryGetOptionalCenter(
            BridgeRequest request,
            out bool hasCenter,
            out float2 center,
            out BridgeResponse error)
        {
            hasCenter = false;
            center = default;
            error = null;
            bool hasX = request.TryGetFloat("x", out float x);
            bool hasZ = request.TryGetFloat("z", out float z);
            if (hasX != hasZ)
            {
                error = BridgeResponse.Error(
                    BridgeErrorKind.InvalidArguments,
                    "x and z must both be provided");
                return false;
            }
            if (!hasX)
            {
                return true;
            }
            hasCenter = true;
            center = new float2(x, z);
            return true;
        }

        private object ReadLowVoltageElectricity(Entity entity, out float loadRatio)
        {
            loadRatio = 0f;
            if (!EntityManager.HasComponent<ElectricityNodeConnection>(entity))
            {
                return null;
            }
            Entity middle = EntityManager.GetComponentData<ElectricityNodeConnection>(entity)
                .m_ElectricityNode;
            if (middle == Entity.Null
                || !EntityManager.HasBuffer<ConnectedFlowEdge>(middle))
            {
                return null;
            }
            DynamicBuffer<ConnectedFlowEdge> connected =
                EntityManager.GetBuffer<ConnectedFlowEdge>(middle, isReadOnly: true);
            int chosenAbsFlow = 0;
            int chosenCapacity = 0;
            bool bottleneck = false;
            float chosenLoad = -1f;
            foreach (ConnectedFlowEdge item in connected)
            {
                if (!EntityManager.HasComponent<ElectricityFlowEdge>(item.m_Edge))
                {
                    continue;
                }
                ElectricityFlowEdge flowEdge =
                    EntityManager.GetComponentData<ElectricityFlowEdge>(item.m_Edge);
                TypedNetworkMath.ConsiderElectricityEdge(
                    flowEdge.m_Flow,
                    flowEdge.m_Capacity,
                    flowEdge.isBottleneck,
                    ref chosenAbsFlow,
                    ref chosenCapacity,
                    ref bottleneck,
                    ref chosenLoad);
            }
            if (chosenLoad < 0f)
            {
                return null;
            }
            loadRatio = chosenLoad;
            return new
            {
                flow = chosenAbsFlow,
                capacity = chosenCapacity,
                bottleneck,
            };
        }

        private object ReadRoadTraffic(Entity entity, out float volumeIndex, out float congestionIndex)
        {
            volumeIndex = 0f;
            congestionIndex = 0f;
            if (!EntityManager.HasComponent<Game.Net.Road>(entity))
            {
                return null;
            }
            Game.Net.Road road = EntityManager.GetComponentData<Game.Net.Road>(entity);
            float flowPercent = math.csum(Game.Net.NetUtils.GetTrafficFlowSpeed(road)) * 25f;
            volumeIndex = math.csum(
                (road.m_TrafficFlowDistance0 + road.m_TrafficFlowDistance1)
                * 2.6666667f) * 0.25f;
            congestionIndex = volumeIndex * math.saturate(1f - flowPercent * 0.01f);
            int activeBottlenecks = 0;
            if (EntityManager.HasBuffer<Game.Net.SubLane>(entity))
            {
                DynamicBuffer<Game.Net.SubLane> lanes =
                    EntityManager.GetBuffer<Game.Net.SubLane>(entity, isReadOnly: true);
                foreach (Game.Net.SubLane lane in lanes)
                {
                    if (EntityManager.HasComponent<Game.Net.Bottleneck>(lane.m_SubLane)
                        && EntityManager.GetComponentData<Game.Net.Bottleneck>(lane.m_SubLane).m_Timer >= 20)
                    {
                        activeBottlenecks++;
                    }
                }
            }
            return new
            {
                volumeIndex = (float)Math.Round(volumeIndex, 1),
                congestionIndex = (float)Math.Round(congestionIndex, 1),
                activeBottlenecks,
                flowPercent = (float)Math.Round(flowPercent, 1),
            };
        }

        private List<TypedNetworkEdge> SnapshotTypedNetworks()
        {
            var snapshot = new List<TypedNetworkEdge>();
            HashSet<Entity> outside = OutsideNetworkNodes();
            using (NativeArray<Entity> entities = PlacedRoadQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
                    TypedNetworkKinds kinds = ClassifyNetPrefab(prefabRef.m_Prefab);
                    if ((kinds & TypedNetworkMath.ProductKinds) == TypedNetworkKinds.None)
                    {
                        continue;
                    }
                    Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(entity);
                    Game.Net.Edge edge = EntityManager.GetComponentData<Game.Net.Edge>(entity);
                    snapshot.Add(new TypedNetworkEdge(
                        entity.Index,
                        edge.m_Start.Index,
                        edge.m_End.Index,
                        SampleNetworkCurve(curve),
                        kinds,
                        outside.Contains(edge.m_Start),
                        outside.Contains(edge.m_End)));
                }
            }
            return snapshot;
        }

        private TypedNetworkKinds ClassifyNetPrefab(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null || !EntityManager.Exists(prefabEntity))
            {
                return TypedNetworkKinds.None;
            }
            bool isRoad = EntityManager.HasComponent<RoadData>(prefabEntity);
            bool water = false;
            bool sewage = false;
            bool lowVoltage = false;
            if (EntityManager.HasComponent<NetData>(prefabEntity))
            {
                Game.Net.Layer layers =
                    EntityManager.GetComponentData<NetData>(prefabEntity).m_RequiredLayers;
                water = (layers & Game.Net.Layer.WaterPipe) != 0;
                sewage = (layers & Game.Net.Layer.SewagePipe) != 0;
                lowVoltage = (layers & Game.Net.Layer.PowerlineLow) != 0;
            }
            if (EntityManager.HasComponent<WaterPipeConnectionData>(prefabEntity))
            {
                WaterPipeConnectionData pipe =
                    EntityManager.GetComponentData<WaterPipeConnectionData>(prefabEntity);
                water |= pipe.m_FreshCapacity > 0;
                sewage |= pipe.m_SewageCapacity > 0;
            }
            if (EntityManager.HasComponent<ElectricityConnectionData>(prefabEntity))
            {
                lowVoltage |= EntityManager.GetComponentData<ElectricityConnectionData>(prefabEntity)
                    .m_Voltage == ElectricityConnection.Voltage.Low;
            }
            return TypedNetworkMath.Classify(isRoad, water, sewage, lowVoltage);
        }

        private HashSet<Entity> OutsideNetworkNodes()
        {
            var nodes = new HashSet<Entity>();
            using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.OutsideConnection>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Objects.ElectricityOutsideConnection>(), ComponentType.ReadOnly<Game.Objects.WaterPipeOutsideConnection>() },
            }))
            using (var connections = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity connection in connections)
                {
                    Entity node = EntityManager.HasComponent<Game.Common.Owner>(connection)
                        ? EntityManager.GetComponentData<Game.Common.Owner>(connection).m_Owner : connection;
                    if (EntityManager.HasBuffer<Game.Net.ConnectedEdge>(node)) nodes.Add(node);
                }
            }
            return nodes;
        }
    }
}
