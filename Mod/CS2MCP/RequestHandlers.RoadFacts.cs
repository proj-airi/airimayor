using Game.Prefabs;
using Unity.Entities;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private float NativeRoadWidthM(Entity prefab)
        {
            return EntityManager.HasComponent<NetGeometryData>(prefab)
                ? EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth
                : 0f;
        }

        private struct RoadPrefabFacts
        {
            public string RoadClass;
            public double? SpeedKmh;
            public int? CarLanes;
            public bool HighwayRules;
            public bool Zonable;
        }

        /// <summary>
        /// Reads UI-group category, speed limit, car-lane count and flags
        /// for one road prefab entity. Never throws: missing components
        /// yield unknown or null facts instead of an error.
        /// </summary>
        private RoadPrefabFacts ReadRoadPrefabFacts(
            Entity prefabEntity,
            PrefabSystem prefabSystem,
            string prefabName)
        {
            var facts = new RoadPrefabFacts
            {
                RoadClass = RoadFactsMath.RoadClassUnknown,
            };
            if (prefabEntity == Entity.Null || !EntityManager.Exists(prefabEntity))
            {
                return facts;
            }
            if (EntityManager.HasComponent<RoadData>(prefabEntity))
            {
                RoadData road = EntityManager.GetComponentData<RoadData>(prefabEntity);
                facts.SpeedKmh = RoadFactsMath.ToKmh(road.m_SpeedLimit);
                facts.HighwayRules = (road.m_Flags & RoadFlags.UseHighwayRules) != 0;
                facts.Zonable = (road.m_Flags & RoadFlags.EnableZoning) != 0;
            }
            facts.CarLanes = CountRoadCarLanes(prefabEntity, prefabSystem);
            facts.RoadClass = RoadFactsMath.Classify(
                ReadUiGroupName(prefabEntity, prefabSystem),
                prefabName);
            return facts;
        }

        private int? CountRoadCarLanes(Entity prefabEntity, PrefabSystem prefabSystem)
        {
            // Lanes live in the authoring sections, not in SubNet buffers.
            // Conditional pieces (elevated/tunnel variants of the same lanes)
            // are skipped so each lane counts once.
            NetGeometryPrefab geometry = prefabSystem.GetPrefab<NetGeometryPrefab>(prefabEntity);
            if (geometry == null || geometry.m_Sections == null)
            {
                return null;
            }
            int lanes = 0;
            bool any = false;
            foreach (NetSectionInfo section in geometry.m_Sections)
            {
                if (section.m_Section == null || section.m_Section.m_Pieces == null)
                {
                    continue;
                }
                any = true;
                foreach (NetPieceInfo piece in section.m_Section.m_Pieces)
                {
                    if (piece.m_Piece == null
                        || piece.m_RequireAll.Length > 0
                        || piece.m_RequireAny.Length > 0
                        || !piece.m_Piece.TryGet<NetPieceLanes>(out NetPieceLanes pieceLanes)
                        || pieceLanes.m_Lanes == null)
                    {
                        continue;
                    }
                    foreach (NetLaneInfo lane in pieceLanes.m_Lanes)
                    {
                        if (lane.m_Lane != null && lane.m_Lane.TryGet<CarLane>(out _))
                        {
                            lanes++;
                        }
                    }
                }
            }
            return any ? lanes : (int?)null;
        }

        private string ReadUiGroupName(Entity prefabEntity, PrefabSystem prefabSystem)
        {
            if (!EntityManager.HasComponent<UIObjectData>(prefabEntity))
            {
                return null;
            }
            Entity group = EntityManager.GetComponentData<UIObjectData>(prefabEntity).m_Group;
            if (group == Entity.Null || !EntityManager.Exists(group))
            {
                return null;
            }
            // The group slot holds a group prefab entity, not an instance:
            // resolve it through PrefabSystem like RoadBuilder does for
            // UIGroupPrefabs (RoadsHighways, RoadsSmallRoads, ...).
            UIGroupPrefab groupPrefab = prefabSystem.GetPrefab<UIGroupPrefab>(group);
            return groupPrefab != null ? groupPrefab.name : null;
        }
    }
}
