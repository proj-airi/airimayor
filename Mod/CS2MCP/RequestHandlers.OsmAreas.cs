using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private struct OsmZoneBlocks : INativeQuadTreeIterator<Entity, Bounds2>
        {
            internal Bounds2 Bounds;
            internal List<Entity> Blocks;
            public bool Intersect(Bounds2 bounds) => MathUtils.Intersect(Bounds, bounds);
            public void Iterate(Bounds2 bounds, Entity block) { if (Intersect(bounds)) Blocks.Add(block); }
        }

        private struct OsmAreaSearch : INativeQuadTreeIterator<AreaSearchItem, QuadTreeBoundsXZ>
        {
            internal Bounds2 Bounds;
            internal HashSet<Entity> Areas;
            public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(Bounds, bounds.m_Bounds.xz);
            public void Iterate(QuadTreeBoundsXZ bounds, AreaSearchItem item) { if (Intersect(bounds)) Areas.Add(item.m_Area); }
        }

        private List<Entity> OsmZoneBlocksIn(float4 bounds)
        {
            var iterator = new OsmZoneBlocks { Bounds = new Bounds2(bounds.xy, bounds.zw), Blocks = new List<Entity>() };
            var tree = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>().GetSearchTree(true, out var dependencies);
            dependencies.Complete();
            tree.Iterate(ref iterator);
            return iterator.Blocks;
        }

        private void AddOsmAreas(OsmDocument document, float4 bounds)
        {
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var zones = new Dictionary<ZoneType, string>();
            foreach (Entity entity in OsmZoneBlocksIn(bounds))
            {
                if (!ZoneBlockQuery.MatchesIgnoreFilter(entity)) continue;
                Block block = EntityManager.GetComponentData<Block>(entity);
                var cells = EntityManager.GetBuffer<Cell>(entity, true);
                for (int index = 0; index < cells.Length;)
                {
                    int start = index++;
                    Cell first = cells[start];
                    if (!OsmZoneCell.Eligible(first) || first.m_Zone.Equals(ZoneType.None)) continue;
                    while (index < cells.Length && index % block.m_Size.x != 0 && OsmZoneCell.Eligible(cells[index])
                        && first.m_Zone.Equals(cells[index].m_Zone)) index++;
                    int2 coordinate = new int2(start % block.m_Size.x, start / block.m_Size.x);
                    float2 a = ZoneUtils.GetCellPosition(block, coordinate).xz;
                    float2 column = ZoneUtils.GetCellPosition(block, coordinate + new int2(1, 0)).xz - a;
                    float2 row = ZoneUtils.GetCellPosition(block, coordinate + new int2(0, 1)).xz - a;
                    float2 corner = a - (column + row) * 0.5f;
                    float2 far = column * (index - start);
                    float2[] ring = { corner, corner + far, corner + far + row, corner + row };
                    if (!CityOsmAreas.Intersects(ring, bounds)) continue;
                    if (!zones.TryGetValue(first.m_Zone, out string name))
                    {
                        using (var assets = ZonePrefabQuery.ToEntityArray(Allocator.Temp))
                        {
                            foreach (Entity asset in assets)
                                zones[EntityManager.GetComponentData<ZoneData>(asset).m_ZoneType] = PrefabDisplayName(prefabs, asset);
                        }
                        name = zones[first.m_Zone];
                    }
                    long key = ((long)entity.Index << 16) + checked((ushort)start);
                    var way = AddOsmRing(document, (1L << 50) + key, (1L << 54) + key * 4, ring);
                    way.Tags[CityOsmAreas.Zone] = name;
                    way.Tags["airimayor:block"] = OsmEntityIdentity(entity);
                    way.Tags["airimayor:cells"] = (index - start).ToString(CultureInfo.InvariantCulture);
                }
            }

            var areas = new OsmAreaSearch { Bounds = new Bounds2(bounds.xy, bounds.zw), Areas = new HashSet<Entity>() };
            var areaTree = World.GetOrCreateSystemManaged<Game.Areas.SearchSystem>().GetSearchTree(true, out var areaDependencies);
            areaDependencies.Complete();
            areaTree.Iterate(ref areas);
            foreach (Entity area in areas.Areas)
            {
                if (!EntityManager.Exists(area) || EntityManager.HasComponent<Game.Tools.Temp>(area)
                    || EntityManager.HasComponent<Deleted>(area) || !EntityManager.HasComponent<Lot>(area)
                    || !EntityManager.HasBuffer<AreaNode>(area) || !EntityManager.HasComponent<Owner>(area)
                    || !EntityManager.HasComponent<PrefabRef>(area)) continue;
                Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                string kind = EntityManager.HasComponent<Extractor>(area) && EntityManager.HasComponent<ExtractorAreaData>(areaPrefab) ? "extractor"
                    : EntityManager.HasComponent<Storage>(area) && EntityManager.HasComponent<StorageAreaData>(areaPrefab) ? "storage" : null;
                if (kind == null) continue;
                var nodes = EntityManager.GetBuffer<AreaNode>(area, true);
                var ring = new List<float2>(nodes.Length);
                foreach (var node in nodes) ring.Add(node.m_Position.xz);
                if (ring.Count < 3 || !CityOsmAreas.Intersects(ring, bounds)) continue;
                Entity building = EntityManager.GetComponentData<Owner>(area).m_Owner;
                var owners = new HashSet<Entity>();
                while (building != Entity.Null && !EntityManager.HasComponent<Game.Buildings.Building>(building)
                    && owners.Add(building) && EntityManager.HasComponent<Owner>(building))
                    building = EntityManager.GetComponentData<Owner>(building).m_Owner;
                if (!EntityManager.HasComponent<Game.Buildings.Building>(building) || !EntityManager.HasComponent<Transform>(building)) continue;
                long id = CityOsm.NativeId(area.Index);
                var perimeter = AddOsmRing(document, id, (1L << 57) + ((long)area.Index << 20), ring);
                perimeter.Tags[CityOsmAreas.Perimeter] = kind;
                perimeter.Tags["airimayor:entity"] = OsmEntityIdentity(area);
                long ownerId = (1L << 60) + building.Index;
                if (!document.Nodes.ContainsKey(ownerId))
                {
                    var owner = CityOsm.ToNode(ownerId, EntityManager.GetComponentData<Transform>(building).m_Position);
                    owner.Tags["airimayor:facility"] = OsmEntityIdentity(building);
                    document.Nodes.Add(ownerId, owner);
                }
                var relation = new OsmRelation(id);
                relation.Members.Add(new OsmMember(OsmKind.Node, ownerId, "owner"));
                relation.Members.Add(new OsmMember(OsmKind.Way, id, "perimeter"));
                relation.Tags["type"] = "site";
                relation.Tags["site"] = "operational_area";
                relation.Attributes["version"] = "1";
                document.Relations.Add(id, relation);
            }
        }

        private static OsmWay AddOsmRing(OsmDocument document, long id, long firstNode, IReadOnlyList<float2> ring)
        {
            var way = new OsmWay(id);
            way.Attributes["version"] = "1";
            way.Tags["area"] = "yes";
            for (int i = 0; i < ring.Count; i++)
            {
                long nodeId = firstNode + i;
                var node = CityOsm.ToNode(nodeId, new float3(ring[i].x, 0f, ring[i].y));
                node.Tags.Remove("ele");
                document.Nodes.Add(nodeId, node);
                way.NodeIds.Add(nodeId);
            }
            way.NodeIds.Add(firstNode);
            document.Ways.Add(id, way);
            return way;
        }

        private OsmAreaPlan PlanOsmAreas(float4 bounds, OsmDocument before, OsmDocument after, List<CityOsmEditItem> changes)
        {
            var plan = new OsmAreaPlan();
            var brushes = new List<(long Id, List<float2> Ring, ZoneType Zone, string Block, bool ClearOld)>();
            foreach (var change in CityOsmAreas.TakeChanges(changes))
            {
                var old = (OsmWay)change.Original;
                var next = (OsmWay)change.Edited;
                if (next != null)
                {
                    foreach (long id in next.NodeIds)
                    {
                        before.Nodes.TryGetValue(id, out OsmNode oldNode);
                        if (CityOsm.Tag(oldNode, "ele") != CityOsm.Tag(after.Nodes[id], "ele"))
                            throw new FormatException($"Area corner {id} follows the ground. Edit its horizontal position.");
                    }
                }
                if (CityOsm.Tag(old, CityOsmAreas.Perimeter) == null && CityOsm.Tag(next, CityOsmAreas.Perimeter) == null)
                {
                    plan.ZoneSources.Add((old ?? next).Id, new List<(Entity, int)>());
                    if (old != null)
                        brushes.Add((old.Id, CityOsmAreas.Ring(before, old, bounds), ZoneType.None, CityOsm.Tag(old, "airimayor:block"), true));
                    if (next != null)
                    {
                        CityOsmEdit.CheckEditedTags(old, next, CityOsmAreas.Zone, "area");
                        if ((CityOsm.Tag(next, "area") ?? "yes") != "yes") throw new FormatException($"Zoning way {next.Id} must be an area.");
                        if (!TryResolveZone(CityOsm.Tag(next, CityOsmAreas.Zone), out ZoneType zone, out string error)) throw new FormatException(error);
                        brushes.Add((next.Id, CityOsmAreas.Ring(after, next, bounds), zone, null, false));
                    }
                    continue;
                }
                if (old == null || next == null) throw new FormatException("Expand the existing facility perimeter. Its ownership and identity must remain.");
                plan.Perimeters.Add(PlanOsmOperationalArea(before, after, old, next, bounds));
            }
            if (brushes.Count == 0) return plan;
            var covered = new bool[brushes.Count];
            var ownedTiles = new HashSet<long>();
            foreach (Entity blockEntity in OsmZoneBlocksIn(bounds))
            {
                if (!ZoneBlockQuery.MatchesIgnoreFilter(blockEntity)) continue;
                Block block = EntityManager.GetComponentData<Block>(blockEntity);
                var cells = EntityManager.GetBuffer<Cell>(blockEntity, true);
                string identity = OsmEntityIdentity(blockEntity);
                for (int index = 0; index < cells.Length; index++)
                {
                    Cell cell = cells[index];
                    if (!OsmZoneCell.Eligible(cell)) continue;
                    float3 center = ZoneUtils.GetCellPosition(block, new int2(index % block.m_Size.x, index / block.m_Size.x));
                    ZoneType? painted = null;
                    bool cleared = false;
                    for (int i = 0; i < brushes.Count; i++)
                    {
                        var brush = brushes[i];
                        if ((brush.Block != null && brush.Block != identity) || CityOsmAreas.DistanceToPolygon(center.xz, brush.Ring) > 0.001f) continue;
                        covered[i] = true;
                        if (brush.ClearOld) cleared = true;
                        else if (painted.HasValue && !painted.Value.Equals(brush.Zone)) throw new FormatException("Overlapping zoning polygons give one cell different zones.");
                        else
                        {
                            painted = brush.Zone;
                            if (!brush.Zone.Equals(ZoneType.None)) plan.ZoneSources[brush.Id].Add((blockEntity, index));
                        }
                    }
                    if (!cleared && !painted.HasValue) continue;
                    if (ownedTiles.Add(MapTileKey(center.xz)) && !IsOnOwnedTile(center))
                        throw new FormatException("A zoning polygon reaches eligible cells on land the city does not own.");
                    plan.Cells.Add(new OsmZoneCell(blockEntity, index, center.xz, cell.m_Zone, painted ?? ZoneType.None));
                }
            }
            if (covered.Any(value => !value))
                throw new FormatException("A zoning polygon covers no existing eligible frontage. Build its street first, then read the district before zoning.");
            return plan;
        }

        private static bool MapOsmAreaResults(OsmAreaPlan plan, OsmDocument district, Dictionary<string, List<string>> matches)
        {
            if (!plan.HasEdits) return true;
            foreach (var perimeter in plan.Perimeters)
            {
                long id = CityOsm.NativeId(perimeter.Area.Index);
                if (!district.Ways.TryGetValue(id, out OsmWay way) || CityOsm.Tag(way, "airimayor:entity") != OsmEntityIdentity(perimeter.Area)) return false;
                matches["way/" + id] = new List<string> { "way/" + id };
            }
            if (plan.ZoneSources.Count == 0) return true;
            var rows = new Dictionary<(string Block, int Index), long>();
            foreach (OsmWay way in district.Ways.Values)
            {
                string block = CityOsm.Tag(way, "airimayor:block");
                if (block == null) continue;
                int start = (int)(way.Id & ushort.MaxValue);
                int count = int.Parse(way.Tags["airimayor:cells"], CultureInfo.InvariantCulture);
                for (int i = 0; i < count; i++) rows.Add((block, start + i), way.Id);
            }
            foreach (var source in plan.ZoneSources)
            {
                var selected = new SortedSet<long>();
                foreach (var cell in source.Value)
                {
                    if (!rows.TryGetValue((OsmEntityIdentity(cell.Block), cell.Index), out long row)) return false;
                    selected.Add(row);
                }
                matches["way/" + source.Key] = selected.Select(id => "way/" + id).ToList();
            }
            return true;
        }

        private void CheckOsmAreaDependencies(OsmAreaPlan plan, IReadOnlyList<OsmRoadPlacement> roads,
            IReadOnlyList<OsmBuildingPlacement> buildings, IReadOnlyList<Entity> deletions)
        {
            if (!plan.HasEdits) return;
            foreach (var perimeter in plan.Perimeters)
                if (deletions.Contains(perimeter.Building) || buildings.Any(item => item.Original == perimeter.Building))
                    throw new FormatException("Complete the edit to the facility before expanding its owned perimeter.");
            if (plan.Cells.Count == 0) return;
            var zoneFrontage = new HashSet<Entity>();
            foreach (var cell in plan.Cells)
            {
                if (EntityManager.HasComponent<Owner>(cell.Block))
                    zoneFrontage.Add(EntityManager.GetComponentData<Owner>(cell.Block).m_Owner);
            }
            if (deletions.Any(zoneFrontage.Contains))
                throw new FormatException("A zoning polygon uses frontage on a street being removed. Read the surviving frontage after demolition.");
            foreach (var road in roads)
            {
                if (!road.Grade.HasValue) continue;
                if (road.Original != Entity.Null && EntityManager.HasComponent<Game.Net.Curve>(road.Original)
                    && EntityManager.GetComponentData<PrefabRef>(road.Original).m_Prefab == road.Prefab)
                {
                    var original = EntityManager.GetComponentData<Game.Net.Curve>(road.Original).m_Bezier;
                    if (math.distance(original.a, road.Curve.A) < 0.05f && math.distance(original.b, road.Curve.B) < 0.05f
                        && math.distance(original.c, road.Curve.C) < 0.05f && math.distance(original.d, road.Curve.D) < 0.05f) continue;
                }
                float radius = ZoneUtils.MAX_ZONE_DEPTH * ZoneUtils.CELL_SIZE + NativeRoadWidthM(road.Prefab) * 0.5f;
                if (zoneFrontage.Contains(road.Original) || plan.Cells.Any(cell => MathUtils.Distance(road.Bezier.xz, cell.Center, out _) <= radius))
                    throw new FormatException("A zoning polygon depends on a street whose frontage is being rebuilt. Apply the street edit and read its new cells before zoning.");
            }
        }
    }
}
