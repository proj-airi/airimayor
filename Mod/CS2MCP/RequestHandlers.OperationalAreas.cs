using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;
using SubArea = Game.Areas.SubArea;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse InspectOperationalArea(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetInt("index", out int index)
                || !request.TryGetInt("version", out int version))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    "Choose a building from query_osm and supply its index and version.");
            }

            if (!TryResolveExistingEntity(index, version, out Entity building)
                || !EntityManager.HasComponent<Game.Buildings.Building>(building))
            {
                return BridgeResponse.Error(BridgeErrorKind.NotFound,
                    $"entity {index}:{version} is not an existing building");
            }

            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            string buildingName = GetEntityPrefabName(prefabSystem, building);
            var areas = new List<object>();
            int editableAreaCount = 0;
            if (EntityManager.HasBuffer<Game.Areas.SubArea>(building))
            {
                DynamicBuffer<Game.Areas.SubArea> subAreas =
                    EntityManager.GetBuffer<Game.Areas.SubArea>(building, isReadOnly: true);
                foreach (Game.Areas.SubArea subArea in subAreas)
                {
                    if (subArea.m_Area == Entity.Null || !EntityManager.Exists(subArea.m_Area))
                    {
                        continue;
                    }
                    object areaView = BuildOperationalAreaView(
                        prefabSystem,
                        building,
                        subArea.m_Area,
                        out bool editable);
                    areas.Add(areaView);
                    if (editable)
                    {
                        editableAreaCount++;
                    }
                }
            }

            return BridgeResponse.Json(new
            {
                building = buildingName,
                entity = new { index, version },
                areaCount = areas.Count,
                editableAreaCount,
                areas,
                note = areas.Count == 0
                    ? "this building has no owned operational area"
                    : "read-only snapshot; storage capacity is calculated with the game's AreaUtils and extractor fields are current simulation state",
            });
        }

        private OsmPerimeter PlanOsmOperationalArea(OsmDocument before, OsmDocument after, OsmWay old, OsmWay next, float4 bounds)
        {
            CityOsmEdit.CheckEditedTags(old, next);
            var current = CityOsmAreas.Ring(before, old, bounds);
            var desired = CityOsmAreas.Ring(after, next, bounds);
            for (int i = 0; i < desired.Count; i++)
                if (before.Nodes.TryGetValue(next.NodeIds[i], out OsmNode previousNode)
                    && CityOsmEdit.SameNodePosition(previousNode, after.Nodes[next.NodeIds[i]]))
                    desired[i] = CityOsm.Position(previousNode).xz;
            var area = OsmOriginal(old);
            long ownerId = before.Relations[old.Id].Members.Single(m => m.Role == "owner").Id;
            string[] identity = CityOsm.Tag(before.Nodes[ownerId], "airimayor:facility").Split(':');
            if (!TryResolveExistingEntity(int.Parse(identity[0]), int.Parse(identity[1]), out Entity building))
                throw new FormatException("The facility that owns this area changed.");
            if (!TryResolveExpandableOperationalArea(building, area, out Entity owner, out Entity prefabEntity,
                out DynamicBuffer<Node> currentNodes, out ExtractorAreaData? extractorData, out string error)) throw new FormatException(error);
            CityOsmAreas.CheckExpansion(current, desired);
            TerrainHeightData heights = World.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData();
            var nodes = new Node[desired.Count];
            nodes[0] = currentNodes[0];
            nodes[1] = currentNodes[1];
            for (int i = 2; i < desired.Count; i++)
                nodes[i] = CreateOperationalAreaNode(FindNearestOperationalAreaNode(currentNodes, desired[i]), desired[i], ref heights);
            if (!IsOperationalAreaCandidateClear(building, nodes, out string obstacle))
                throw new FormatException(obstacle);
            if (extractorData.HasValue && !TryValidateOperationalAreaResource(nodes, extractorData.Value, out string resource))
                throw new FormatException(resource);
            return new OsmPerimeter { Building = building, Area = area, Owner = owner, Prefab = prefabEntity, Nodes = nodes };
        }

        private bool TryResolveExpandableOperationalArea(
            Entity building, Entity area, out Entity owner, out Entity prefabEntity,
            out DynamicBuffer<Node> nodes, out ExtractorAreaData? extractorData, out string error)
        {
            owner = Entity.Null;
            prefabEntity = Entity.Null;
            nodes = default;
            extractorData = null;
            error = null;
            if (!EntityManager.HasBuffer<SubArea>(building) || !IsAreaOwnedBy(area, building)
                || !EntityManager.HasComponent<Lot>(area) || !EntityManager.HasComponent<Geometry>(area)
                || !EntityManager.HasBuffer<Node>(area) || !EntityManager.HasComponent<PrefabRef>(area))
            {
                error = "The operational area is no longer owned by that facility.";
                return false;
            }
            bool attached = false;
            foreach (SubArea part in EntityManager.GetBuffer<SubArea>(building, true))
                if (part.m_Area == area) attached = true;
            owner = EntityManager.GetComponentData<Owner>(area).m_Owner;
            prefabEntity = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            nodes = EntityManager.GetBuffer<Node>(area, true);
            bool isStorage = EntityManager.HasComponent<Storage>(area) && EntityManager.HasComponent<StorageAreaData>(prefabEntity)
                && (EntityManager.GetComponentData<StorageAreaData>(prefabEntity).m_Resources & Game.Economy.Resource.Garbage) != 0;
            if (EntityManager.HasComponent<Extractor>(area) && EntityManager.HasComponent<ExtractorAreaData>(prefabEntity))
                extractorData = EntityManager.GetComponentData<ExtractorAreaData>(prefabEntity);
            if (!attached || (!isStorage && !extractorData.HasValue) || nodes.Length < CityOsmAreas.MinimumOperationalCorners
                || nodes.Length > CityOsmAreas.MaximumOperationalCorners)
            {
                error = "This perimeter is not an expandable landfill storage or extractor area.";
                return false;
            }
            return true;
        }

        private struct ForestResourceIterator
            : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds2 Bounds;
            public NativeArray<float2> Polygon;
            public ComponentLookup<Tree> Trees;
            public ComponentLookup<Plant> Plants;
            public ComponentLookup<Transform> Transforms;
            public ComponentLookup<Damaged> Damaged;
            public ComponentLookup<PrefabRef> PrefabRefs;
            public ComponentLookup<TreeData> TreePrefabs;
            public ComponentLookup<Decoration> Decorations;
            public float RemainingAmount;

            public bool Intersect(QuadTreeBoundsXZ bounds)
            {
                const BoundsMask required = BoundsMask.IsTree | BoundsMask.NotOverridden;
                return (bounds.m_Mask & required) == required
                    && MathUtils.Intersect(bounds.m_Bounds.xz, Bounds);
            }

            public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
            {
                const BoundsMask required = BoundsMask.IsTree | BoundsMask.NotOverridden;
                if ((bounds.m_Mask & required) != required
                    || !MathUtils.Intersect(bounds.m_Bounds.xz, Bounds)
                    || (Decorations.HasComponent(entity) && Decorations.IsComponentEnabled(entity))
                    || !Trees.HasComponent(entity)
                    || !Plants.HasComponent(entity)
                    || !Transforms.HasComponent(entity)
                    || !PrefabRefs.HasComponent(entity))
                {
                    return;
                }

                float2 position = Transforms[entity].m_Position.xz;
                if (!ContainsPoint(position))
                {
                    return;
                }

                Entity prefab = PrefabRefs[entity].m_Prefab;
                if (!TreePrefabs.TryGetComponent(prefab, out TreeData treeData)
                    || treeData.m_WoodAmount < Game.Objects.ObjectUtils.MIN_TREE_WOOD_RESOURCE)
                {
                    return;
                }

                Damaged.TryGetComponent(entity, out Damaged damaged);
                float amount = Game.Objects.ObjectUtils.CalculateWoodAmount(
                    Trees[entity],
                    Plants[entity],
                    damaged,
                    treeData);
                if (amount <= 0f)
                {
                    return;
                }

                RemainingAmount += amount;
            }

            private bool ContainsPoint(float2 point)
            {
                bool inside = false;
                int previous = Polygon.Length - 1;
                for (int current = 0; current < Polygon.Length; current++)
                {
                    float2 a = Polygon[current];
                    float2 b = Polygon[previous];
                    bool crosses = (a.y > point.y) != (b.y > point.y)
                        && point.x < (b.x - a.x) * (point.y - a.y)
                            / (b.y - a.y) + a.x;
                    if (crosses)
                    {
                        inside = !inside;
                    }
                    previous = current;
                }
                return inside;
            }
        }

        private bool TryValidateOperationalAreaResource(Node[] nodes, ExtractorAreaData extractorData, out string error)
        {
            error = null;
            MapFeature feature = extractorData.m_MapFeature;
            if (feature == MapFeature.Forest)
            {
                return TryValidateForestOperationalArea(nodes, extractorData.m_RequireNaturalResource, out error);
            }
            if (feature != MapFeature.FertileLand
                && feature != MapFeature.Ore
                && feature != MapFeature.Oil
                && feature != MapFeature.Fish)
            {
                if (!extractorData.m_RequireNaturalResource) return true;
                error = $"The city cannot check {feature} resources for this operational area.";
                return false;
            }

            NaturalResourceSystem resources =
                World.GetOrCreateSystemManaged<NaturalResourceSystem>();
            NativeArray<NaturalResourceCell> map = resources.GetMap(
                readOnly: true,
                out Unity.Jobs.JobHandle dependencies);
            dependencies.Complete();
            int textureSize = (int)math.round(math.sqrt(map.Length));
            if (textureSize <= 0 || textureSize * textureSize != map.Length)
            {
                error = "natural-resource map has an unexpected size";
                return false;
            }

            float cellSize = (float)CellMapSystem<NaturalResourceCell>.kMapSize / textureSize;
            float halfTexture = textureSize * 0.5f;
            float cellArea = cellSize * cellSize;
            float remainingAmount = 0f;
            float2 origin = nodes[0].m_Position.xz;
            for (int triangleIndex = 1; triangleIndex < nodes.Length - 1; triangleIndex++)
            {
                var triangle = new Triangle2(
                    origin,
                    nodes[triangleIndex].m_Position.xz,
                    nodes[triangleIndex + 1].m_Position.xz);
                float2 minimum = math.min(triangle.a, math.min(triangle.b, triangle.c));
                float2 maximum = math.max(triangle.a, math.max(triangle.b, triangle.c));
                int minX = math.clamp((int)math.floor(minimum.x / cellSize + halfTexture), 0, textureSize - 1);
                int maxX = math.clamp((int)math.floor(maximum.x / cellSize + halfTexture), 0, textureSize - 1);
                int minY = math.clamp((int)math.floor(minimum.y / cellSize + halfTexture), 0, textureSize - 1);
                int maxY = math.clamp((int)math.floor(maximum.y / cellSize + halfTexture), 0, textureSize - 1);
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        NaturalResourceAmount amount = GetOperationalResourceAmount(
                            map[x + y * textureSize],
                            feature);
                        float remaining = math.max(0f, amount.m_Base - amount.m_Used);
                        if (remaining <= 0f)
                        {
                            continue;
                        }
                        var cellBounds = new Bounds2
                        {
                            min = new float2((x - halfTexture) * cellSize, (y - halfTexture) * cellSize),
                            max = new float2((x + 1f - halfTexture) * cellSize, (y + 1f - halfTexture) * cellSize),
                        };
                        if (MathUtils.Intersect(cellBounds, triangle, out float intersectionArea))
                        {
                            remainingAmount += remaining * intersectionArea / cellArea;
                        }
                    }
                }
            }
            if (extractorData.m_RequireNaturalResource && remainingAmount <= 0.01f)
            {
                error = $"planned {feature} extractor expansion has no remaining resource coverage";
                return false;
            }
            return true;
        }

        private bool TryValidateForestOperationalArea(Node[] nodes, bool requireNaturalResource, out string error)
        {
            error = null;
            float remainingAmount;
            var polygon = new NativeArray<float2>(nodes.Length, Allocator.Temp);
            try
            {
                float2 minimum = new float2(float.MaxValue);
                float2 maximum = new float2(float.MinValue);
                for (int i = 0; i < nodes.Length; i++)
                {
                    float2 position = nodes[i].m_Position.xz;
                    polygon[i] = position;
                    minimum = math.min(minimum, position);
                    maximum = math.max(maximum, position);
                }

                Game.Objects.SearchSystem search =
                    World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
                NativeQuadTree<Entity, QuadTreeBoundsXZ> searchTree =
                    search.GetStaticSearchTree(readOnly: true, out JobHandle dependencies);
                dependencies.Complete();
                var iterator = new ForestResourceIterator
                {
                    Bounds = new Bounds2 { min = minimum, max = maximum },
                    Polygon = polygon,
                    Trees = m_System.GetComponentLookup<Tree>(true),
                    Plants = m_System.GetComponentLookup<Plant>(true),
                    Transforms = m_System.GetComponentLookup<Transform>(true),
                    Damaged = m_System.GetComponentLookup<Damaged>(true),
                    PrefabRefs = m_System.GetComponentLookup<PrefabRef>(true),
                    TreePrefabs = m_System.GetComponentLookup<TreeData>(true),
                    Decorations = m_System.GetComponentLookup<Decoration>(true),
                };
                searchTree.Iterate(ref iterator);
                remainingAmount = iterator.RemainingAmount;
            }
            finally
            {
                polygon.Dispose();
            }

            if (requireNaturalResource && remainingAmount <= 0.01f)
            {
                error = "planned Forest extractor expansion contains no productive tree resources";
                return false;
            }
            return true;
        }

        private static NaturalResourceAmount GetOperationalResourceAmount(
            NaturalResourceCell cell,
            MapFeature feature)
        {
            switch (feature)
            {
                case MapFeature.FertileLand:
                    return cell.m_Fertility;
                case MapFeature.Ore:
                    return cell.m_Ore;
                case MapFeature.Oil:
                    return cell.m_Oil;
                case MapFeature.Fish:
                    return cell.m_Fish;
                default:
                    return default;
            }
        }

        private bool IsOperationalAreaCandidateClear(
            Entity building,
            Node[] nodes,
            out string reason)
        {
            var polygon = new List<float2>(nodes.Length);
            foreach (Node node in nodes)
            {
                polygon.Add(node.m_Position.xz);
                if (!IsOnOwnedTile(node.m_Position))
                {
                    reason = "a planned expansion node is outside owned map tiles";
                    return false;
                }
            }
            using (NativeArray<Entity> buildings = PlacedBuildingQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity other in buildings)
                {
                    if (other == building || IsAreaOwnedBy(other, building))
                    {
                        continue;
                    }
                    Transform transform = EntityManager.GetComponentData<Transform>(other);
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(other);
                    float clearance = BuildingRadius(prefabRef.m_Prefab) + 2f;
                    if (clearance > 2f
                        && CityOsmAreas.DistanceToPolygon(transform.m_Position.xz, polygon) < clearance)
                    {
                        reason = "planned expansion intersects an existing building";
                        return false;
                    }
                }
            }
            using (NativeArray<Entity> roads = PlacedRoadQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity road in roads)
                {
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(road);
                    float clearance = EntityManager.HasComponent<NetGeometryData>(prefabRef.m_Prefab)
                        ? EntityManager.GetComponentData<NetGeometryData>(prefabRef.m_Prefab).m_DefaultWidth * 0.5f + 2f
                        : 2f;
                    Game.Net.Curve curve = EntityManager.GetComponentData<Game.Net.Curve>(road);
                    for (int i = 0; i <= 24; i++)
                    {
                        float2 point = BezierPoint(curve.m_Bezier, i / 24f).xz;
                        if (CityOsmAreas.DistanceToPolygon(point, polygon) < clearance)
                        {
                            reason = "planned expansion intersects an existing road";
                            return false;
                        }
                    }
                }
            }
            reason = null;
            return true;
        }

        private static Node FindNearestOperationalAreaNode(DynamicBuffer<Node> nodes, float2 point)
        {
            int selected = 0;
            float best = float.MaxValue;
            for (int i = 0; i < nodes.Length; i++)
            {
                float distance = math.distancesq(nodes[i].m_Position.xz, point);
                if (distance < best)
                {
                    best = distance;
                    selected = i;
                }
            }
            return nodes[selected];
        }

        private static Node CreateOperationalAreaNode(
            Node template,
            float2 xz,
            ref TerrainHeightData heightData)
        {
            float3 position = new float3(xz.x, template.m_Position.y, xz.y);
            position.y = TerrainUtils.SampleHeight(ref heightData, position);
            template.m_Position = position;
            return template;
        }

        private object BuildOperationalAreaView(
            PrefabSystem prefabSystem,
            Entity building,
            Entity area,
            out bool editable)
        {
            Entity prefabEntity = EntityManager.HasComponent<PrefabRef>(area)
                ? EntityManager.GetComponentData<PrefabRef>(area).m_Prefab
                : Entity.Null;
            string prefabName = prefabEntity != Entity.Null
                ? GetEntityPrefabName(prefabSystem, area)
                : null;
            bool isStorage = EntityManager.HasComponent<Storage>(area);
            bool isExtractor = EntityManager.HasComponent<Extractor>(area);
            string kind = isStorage ? "storage" : isExtractor ? "extractor" : "other";

            Geometry? geometry = EntityManager.HasComponent<Geometry>(area)
                ? EntityManager.GetComponentData<Geometry>(area)
                : (Geometry?)null;
            var nodes = new List<object>();
            if (EntityManager.HasBuffer<Node>(area))
            {
                DynamicBuffer<Node> areaNodes = EntityManager.GetBuffer<Node>(area, isReadOnly: true);
                foreach (Node node in areaNodes)
                {
                    nodes.Add(new
                    {
                        x = node.m_Position.x,
                        y = node.m_Position.y,
                        z = node.m_Position.z,
                    });
                }
            }

            object storage = null;
            if (isStorage)
            {
                Storage value = EntityManager.GetComponentData<Storage>(area);
                int? capacity = null;
                if (geometry.HasValue
                    && prefabEntity != Entity.Null
                    && EntityManager.HasComponent<StorageAreaData>(prefabEntity))
                {
                    capacity = AreaUtils.CalculateStorageCapacity(
                        geometry.Value,
                        EntityManager.GetComponentData<StorageAreaData>(prefabEntity));
                }
                storage = new
                {
                    amount = value.m_Amount,
                    workAmount = value.m_WorkAmount,
                    capacity,
                };
            }

            object extraction = null;
            if (isExtractor)
            {
                Extractor value = EntityManager.GetComponentData<Extractor>(area);
                ExtractorAreaData? data = prefabEntity != Entity.Null
                    && EntityManager.HasComponent<ExtractorAreaData>(prefabEntity)
                    ? EntityManager.GetComponentData<ExtractorAreaData>(prefabEntity)
                    : (ExtractorAreaData?)null;
                extraction = new
                {
                    resource = data.HasValue ? data.Value.m_MapFeature.ToString() : null,
                    requiresNaturalResource = data.HasValue
                        ? (bool?)data.Value.m_RequireNaturalResource
                        : null,
                    resourceAmount = value.m_ResourceAmount,
                    maxConcentration = value.m_MaxConcentration,
                    extractedAmount = value.m_ExtractedAmount,
                    totalExtracted = value.m_TotalExtracted,
                    workAmount = value.m_WorkAmount,
                };
            }

            editable = IsAreaOwnedBy(area, building)
                && (isStorage || isExtractor)
                && nodes.Count >= 3;
            return new
            {
                kind,
                prefab = prefabName,
                editable,
                lockedBuildingEdge = EntityManager.HasComponent<Game.Areas.Lot>(area),
                surfaceArea = geometry.HasValue ? (float?)Math.Round(geometry.Value.m_SurfaceArea, 1) : null,
                nodes,
                storage,
                extraction,
            };
        }

        private bool IsAreaOwnedBy(Entity area, Entity expectedOwner)
        {
            Entity current = area;
            for (int depth = 0; depth < 8 && EntityManager.HasComponent<Owner>(current); depth++)
            {
                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (current == expectedOwner)
                {
                    return true;
                }
                if (current == Entity.Null || !EntityManager.Exists(current))
                {
                    break;
                }
            }
            return false;
        }

        private string GetEntityPrefabName(PrefabSystem prefabSystem, Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity))
            {
                return null;
            }
            Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            PrefabBase prefab = prefabSystem.GetPrefab<PrefabBase>(prefabEntity);
            return prefab != null ? prefab.name : null;
        }
    }
}
