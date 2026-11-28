using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace CS2MCP
{
    internal readonly struct OsmZoneCell
    {
        internal OsmZoneCell(Entity block, int index, float2 center, ZoneType previous, ZoneType desired)
        {
            Block = block;
            Index = index;
            Center = center;
            Previous = previous;
            Desired = desired;
        }

        internal Entity Block { get; }
        internal int Index { get; }
        internal float2 Center { get; }
        internal ZoneType Previous { get; }
        internal ZoneType Desired { get; }
        internal static bool Eligible(Cell cell) => (cell.m_State & CellFlags.Visible) != 0
            && (cell.m_State & (CellFlags.Blocked | CellFlags.Overridden)) == 0;
    }

    internal struct OsmPerimeter
    {
        internal Entity Building, Area, Owner, Prefab;
        internal AreaNode[] Nodes;
    }

    internal sealed class OsmAreaPlan
    {
        internal readonly List<OsmZoneCell> Cells = new List<OsmZoneCell>();
        internal readonly List<OsmPerimeter> Perimeters = new List<OsmPerimeter>();
        internal readonly List<OsmAdministrativeBoundary> Boundaries = new List<OsmAdministrativeBoundary>();
        internal readonly Dictionary<long, List<(Entity Block, int Index)>> ZoneSources = new Dictionary<long, List<(Entity, int)>>();
        internal bool HasNativeEdits => Perimeters.Count != 0 || Boundaries.Count != 0;
        internal bool HasEdits => Cells.Count != 0 || HasNativeEdits;
    }

    public sealed partial class BridgeToolSystem
    {
        private void CheckOsmAreaPreview()
        {
            if (!ZoneCellsMatch(false)) throw new InvalidOperationException("The requested zone cells changed. Read the district again.");
            CheckOsmAdministrativePreview();
            if (m_OsmEdit.Areas.Perimeters.Count == 0) return;
            var requested = new Dictionary<Entity, OsmPerimeter>();
            foreach (var perimeter in m_OsmEdit.Areas.Perimeters) requested.Add(perimeter.Area, perimeter);
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<AreaNode>()))
            using (var preview = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity candidate in preview)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(candidate);
                    if (!requested.TryGetValue(temp.m_Original, out var perimeter)) continue;
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Replace)) != 0 || !AreaMatches(candidate, perimeter, true))
                        throw new InvalidOperationException("The city's preview differs from the requested operational perimeter.");
                    requested.Remove(temp.m_Original);
                }
            }
            if (requested.Count != 0) throw new InvalidOperationException("A requested operational perimeter is missing from the city preview.");
        }

        private bool AppliedOsmAreasMatch()
        {
            if (!ZoneCellsMatch(true) || !AppliedOsmAdministrativeBoundariesMatch()) return false;
            foreach (var perimeter in m_OsmEdit.Areas.Perimeters)
                if (!AreaMatches(perimeter.Area, perimeter, false)) return false;
            return true;
        }

        private void PaintOsmZoneCells()
        {
            if (m_OsmEdit.Areas.Cells.Count == 0) return;
            var updated = new HashSet<Entity>();
            var commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            foreach (var target in m_OsmEdit.Areas.Cells)
            {
                if (target.Previous.Equals(target.Desired)) continue;
                var cells = EntityManager.GetBuffer<Cell>(target.Block);
                Cell cell = cells[target.Index];
                cell.m_Zone = target.Desired;
                cells[target.Index] = cell;
                if (updated.Add(target.Block) && !EntityManager.HasComponent<Updated>(target.Block))
                    commandBuffer.AddComponent<Updated>(target.Block);
            }
        }

        private bool ZoneCellsMatch(bool applied)
        {
            foreach (var target in m_OsmEdit.Areas.Cells)
            {
                if (!EntityManager.HasBuffer<Cell>(target.Block) || !EntityManager.HasComponent<Block>(target.Block)
                    || EntityManager.HasComponent<Deleted>(target.Block)) return false;
                var cells = EntityManager.GetBuffer<Cell>(target.Block, true);
                if (target.Index >= cells.Length) return false;
                Block block = EntityManager.GetComponentData<Block>(target.Block);
                float2 center = ZoneUtils.GetCellPosition(block, new int2(target.Index % block.m_Size.x, target.Index / block.m_Size.x)).xz;
                if (math.distance(center, target.Center) > 0.05f) return false;
                Cell cell = cells[target.Index];
                if (!OsmZoneCell.Eligible(cell) || !cell.m_Zone.Equals(applied ? target.Desired : target.Previous)) return false;
            }
            return true;
        }

        private bool AreaMatches(Entity entity, OsmPerimeter perimeter, bool preview)
        {
            if (!EntityManager.HasBuffer<AreaNode>(entity) || !EntityManager.HasComponent<Owner>(entity)
                || !EntityManager.HasComponent<PrefabRef>(entity) || EntityManager.HasComponent<Deleted>(entity)
                || EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab != perimeter.Prefab) return false;
            Entity owner = EntityManager.GetComponentData<Owner>(entity).m_Owner;
            if (preview && EntityManager.HasComponent<Temp>(owner)) owner = EntityManager.GetComponentData<Temp>(owner).m_Original;
            if (owner != perimeter.Owner) return false;
            var nodes = EntityManager.GetBuffer<AreaNode>(entity, true);
            if (nodes.Length != perimeter.Nodes.Length) return false;
            for (int i = 0; i < nodes.Length; i++)
                if (math.distance(nodes[i].m_Position.xz, perimeter.Nodes[i].m_Position.xz) > 0.05f) return false;
            return true;
        }
    }
}
