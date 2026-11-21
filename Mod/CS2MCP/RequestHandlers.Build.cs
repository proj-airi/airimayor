using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private const float kMapHalfSize = 7168f;
        private const float kMapTileSize = 623.304347826f;

        private EntityQuery m_PlacedBuildingQuery;
        private bool m_PlacedBuildingQueryCreated;
        private EntityQuery m_PlacedRoadQuery;
        private bool m_PlacedRoadQueryCreated;
        private EntityQuery PlacedBuildingQuery
        {
            get
            {
                if (!m_PlacedBuildingQueryCreated)
                {
                    m_PlacedBuildingQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Buildings.Building>(),
                            ComponentType.ReadOnly<Transform>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                        },
                    });
                    m_PlacedBuildingQueryCreated = true;
                }
                return m_PlacedBuildingQuery;
            }
        }

        private EntityQuery PlacedRoadQuery
        {
            get
            {
                if (!m_PlacedRoadQueryCreated)
                {
                    m_PlacedRoadQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[]
                        {
                            ComponentType.ReadOnly<Game.Net.Edge>(),
                            ComponentType.ReadOnly<Game.Net.Curve>(),
                            ComponentType.ReadOnly<PrefabRef>(),
                        },
                        None = new[]
                        {
                            ComponentType.ReadOnly<Game.Tools.Temp>(),
                            ComponentType.ReadOnly<Game.Common.Deleted>(),
                            ComponentType.ReadOnly<Game.Common.Owner>(),
                        },
                    });
                    m_PlacedRoadQueryCreated = true;
                }
                return m_PlacedRoadQuery;
            }
        }

        private static float3[] SampleNetworkCurve(Game.Net.Curve curve)
        {
            int segments = math.clamp((int)math.ceil(curve.m_Length / 8f), 1, 192);
            var points = new float3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                points[i] = BezierPoint(curve.m_Bezier, i / (float)segments);
            }
            return points;
        }

        private void GetBuildingOperationalCapabilities(
            Entity building,
            out bool hasStorageArea,
            out bool hasExtractorArea,
            out bool expandableStorageArea,
            out bool expandableExtractorArea)
        {
            hasStorageArea = false;
            hasExtractorArea = false;
            expandableStorageArea = false;
            expandableExtractorArea = false;
            if (!EntityManager.HasBuffer<Game.Areas.SubArea>(building))
            {
                return;
            }
            DynamicBuffer<Game.Areas.SubArea> subAreas =
                EntityManager.GetBuffer<Game.Areas.SubArea>(building, isReadOnly: true);
            foreach (Game.Areas.SubArea subArea in subAreas)
            {
                Entity area = subArea.m_Area;
                if (area == Entity.Null
                    || !EntityManager.Exists(area)
                    || !IsAreaOwnedBy(area, building))
                {
                    continue;
                }
                bool storage = EntityManager.HasComponent<Game.Areas.Storage>(area);
                bool extractor = EntityManager.HasComponent<Game.Areas.Extractor>(area);
                hasStorageArea |= storage;
                hasExtractorArea |= extractor;
                if (storage
                    && EntityManager.HasBuffer<Game.Areas.Node>(area)
                    && EntityManager.HasComponent<PrefabRef>(area))
                {
                    Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    int nodeCount = EntityManager.GetBuffer<Game.Areas.Node>(area, isReadOnly: true).Length;
                    expandableStorageArea |= nodeCount >= 4
                        && nodeCount <= 16
                        && EntityManager.HasComponent<StorageAreaData>(areaPrefab)
                        && (EntityManager.GetComponentData<StorageAreaData>(areaPrefab).m_Resources
                            & Game.Economy.Resource.Garbage) != 0;
                }
                if (extractor
                    && EntityManager.HasBuffer<Game.Areas.Node>(area)
                    && EntityManager.HasComponent<PrefabRef>(area))
                {
                    Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    int nodeCount = EntityManager.GetBuffer<Game.Areas.Node>(area, isReadOnly: true).Length;
                    expandableExtractorArea |= nodeCount >= 4
                        && nodeCount <= 16
                        && EntityManager.HasComponent<ExtractorAreaData>(areaPrefab);
                }
            }
        }

        private static float? NetworkWidthM(EntityManager entityManager, Entity prefabEntity)
        {
            if (!entityManager.HasComponent<NetGeometryData>(prefabEntity))
            {
                return null;
            }
            return (float?)Math.Round(
                entityManager.GetComponentData<NetGeometryData>(prefabEntity).m_DefaultWidth,
                1);
        }

        private static float3 BezierPoint(Bezier4x3 bezier, float t)
        {
            float3 ab = math.lerp(bezier.a, bezier.b, t);
            float3 bc = math.lerp(bezier.b, bezier.c, t);
            float3 cd = math.lerp(bezier.c, bezier.d, t);
            float3 abc = math.lerp(ab, bc, t);
            float3 bcd = math.lerp(bc, cd, t);
            return math.lerp(abc, bcd, t);
        }

        private static long MapTileKey(float2 position)
        {
            int x = (int)Math.Floor((position.x + kMapHalfSize) / kMapTileSize);
            int z = (int)Math.Floor((position.y + kMapHalfSize) / kMapTileSize);
            return ((long)x << 32) | (uint)z;
        }

        private static float BuildingRadius(BuildingData building)
        {
            return math.length(new float2(building.m_LotSize)) * 4f;
        }

        private float BuildingRadius(Entity prefabEntity)
        {
            return EntityManager.HasComponent<BuildingData>(prefabEntity)
                ? BuildingRadius(EntityManager.GetComponentData<BuildingData>(prefabEntity))
                : 0f;
        }

        private bool IsOnOwnedTile(float3 position)
        {
            long key = MapTileKey(position.xz);
            using (NativeArray<Entity> entities = MapTileQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (MapTileKey(EntityManager.GetComponentData<Game.Areas.Geometry>(entity)
                            .m_CenterPosition.xz) == key)
                    {
                        return !EntityManager.HasComponent<Game.Common.Native>(entity);
                    }
                }
            }
            return false;
        }

    }
}
