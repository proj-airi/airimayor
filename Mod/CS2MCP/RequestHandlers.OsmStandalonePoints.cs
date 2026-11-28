using System;
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private EntityQuery? m_FurniturePrefabQuery;

        private EntityQuery FurniturePrefabQuery => m_FurniturePrefabQuery ??= EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<StaticObjectData>(), ComponentType.ReadOnly<UIObjectData>());

        private OsmStandalonePoint OsmStandalonePointPrefab(Entity prefab)
        {
            if (EntityManager.HasComponent<PlaceableObjectData>(prefab))
            {
                if (EntityManager.HasComponent<TreeData>(prefab)) return OsmStandalonePoint.Tree;
                if (EntityManager.HasComponent<StreetLightData>(prefab)) return OsmStandalonePoint.StreetLamp;
            }
            if (!EntityManager.HasComponent<StaticObjectData>(prefab) || !EntityManager.HasComponent<UIObjectData>(prefab))
                return OsmStandalonePoint.None;
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            return prefabs.TryGetPrefab<StaticObjectPrefab>(prefab, out var asset)
                && AssetDatabase.global.resources.prefabsMap.TryGetGuid(asset, out string prefabId)
                ? CityOsmStandalonePoints.NativeFurniture(prefabId)
                : OsmStandalonePoint.None;
        }

        private OsmNode ReadOsmStandalonePoint(Entity entity, Bounds2 district, PrefabSystem prefabs)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity) || !EntityManager.HasComponent<Transform>(entity)
                || EntityManager.HasComponent<Owner>(entity) || EntityManager.HasComponent<Game.Objects.Attached>(entity)
                || EntityManager.HasComponent<Temp>(entity) || EntityManager.HasComponent<Deleted>(entity)
                || EntityManager.HasComponent<Overridden>(entity)) return null;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            OsmStandalonePoint kind = OsmStandalonePointPrefab(prefab);
            if (kind == OsmStandalonePoint.None) return null;
            float3 position = EntityManager.GetComponentData<Transform>(entity).m_Position;
            if (math.any(position.xz < district.min) || math.any(position.xz > district.max)) return null;
            OsmNode node = CityOsm.ToNode(CityOsm.NativeId(entity.Index), position);
            node.Tags["airimayor:entity"] = OsmEntityIdentity(entity);
            node.Tags["airimayor:prefab"] = PrefabDisplayName(prefabs, prefab);
            CityOsmStandalonePoints.WriteTags(node, kind);
            return node;
        }

        private bool TryPlanOsmStandalonePoint(OsmNode original, OsmNode edited, ref TerrainHeightData terrain,
            List<OsmBuildingPlacement> placements, List<Entity> deletions)
        {
            OsmStandalonePoint previousKind = CityOsmStandalonePoints.Kind(original);
            OsmStandalonePoint nextKind = CityOsmStandalonePoints.Kind(edited);
            if (previousKind == OsmStandalonePoint.None && nextKind == OsmStandalonePoint.None) return false;
            Entity existing = OsmOriginal(original);
            if (edited == null)
            {
                deletions.Add(existing);
                return true;
            }
            if (original != null && previousKind != nextKind)
                throw new FormatException($"Point {edited.Id} must retain its city object type.");
            CityOsmEdit.CheckEditedTags(original, edited, CityOsmStandalonePoints.Tag(nextKind).Key, "ele", "airimayor:prefab");
            EntityQuery assets = nextKind is OsmStandalonePoint.Bench or OsmStandalonePoint.WasteBasket
                ? FurniturePrefabQuery
                : CurbObjectPrefabQuery;
            Entity prefab = FindOsmInfrastructureAsset(assets, CityOsm.Tag(edited, "airimayor:prefab"), existing,
                asset => OsmStandalonePointPrefab(asset) == nextKind);
            quaternion rotation = quaternion.identity;
            if (existing != Entity.Null)
            {
                if (EntityManager.GetComponentData<PrefabRef>(existing).m_Prefab != prefab)
                    throw new FormatException($"Remove point {edited.Id} and add a new point to use a different asset.");
                rotation = EntityManager.GetComponentData<Transform>(existing).m_Rotation;
            }

            float3 position = CityOsm.Position(edited);
            if (edited.Tags.ContainsKey("ele") && !math.isfinite(position.y))
                throw new FormatException($"Point {edited.Id} needs a numeric elevation in meters.");
            if (!math.isfinite(position.y) || (original != null && !CityOsmEdit.SameNodePosition(original, edited)
                && CityOsm.Tag(original, "ele") == CityOsm.Tag(edited, "ele")))
                position.y = TerrainUtils.SampleHeight(ref terrain, position);
            placements.Add(new OsmBuildingPlacement
            {
                Kind = OsmKind.Node, WayId = edited.Id, Original = existing, Prefab = prefab,
                Position = position, Rotation = rotation,
            });
            return true;
        }
    }
}
