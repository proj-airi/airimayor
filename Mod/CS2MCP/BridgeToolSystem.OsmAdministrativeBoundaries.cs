using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace CS2MCP
{
    internal struct OsmAdministrativeBoundary
    {
        internal long RelationId;
        internal Entity Original, Prefab, Realized;
        internal List<float2> Ring;
        internal string Name;
        internal bool Remove;

        internal static List<float2> ReadRing(EntityManager manager, Entity entity)
        {
            var nodes = manager.GetBuffer<AreaNode>(entity, true);
            var ring = new List<float2>(nodes.Length);
            foreach (AreaNode node in nodes) ring.Add(node.m_Position.xz);
            return ring;
        }
    }

    public sealed partial class BridgeToolSystem
    {
        private void CreateOsmAdministrativeBoundary(OsmAdministrativeBoundary item)
        {
            var buffer = m_ToolOutputBarrier.CreateCommandBuffer();
            Entity definition = buffer.CreateEntity();
            buffer.AddComponent(definition, new CreationDefinition
            {
                m_Original = item.Original,
                m_Prefab = item.Prefab,
                m_Flags = item.Remove ? CreationFlags.Delete : item.Original == Entity.Null ? default : CreationFlags.Relocate,
            });
            buffer.AddComponent(definition, default(Updated));
            var nodes = buffer.AddBuffer<AreaNode>(definition);
            if (item.Remove)
            {
                foreach (AreaNode node in EntityManager.GetBuffer<AreaNode>(item.Original, true)) nodes.Add(node);
                return;
            }
            foreach (float2 point in item.Ring) nodes.Add(new AreaNode(new float3(point.x, 0f, point.y), float.MinValue));
            if (item.Original == Entity.Null) nodes.Add(nodes[0]);
        }

        private void CheckOsmAdministrativePreview()
        {
            var edits = m_OsmEdit.Areas.Boundaries;
            if (edits.Count == 0) return;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<District>(), ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<AreaNode>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>()))
            using (var previews = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity preview in previews)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(preview);
                    bool removed = (temp.m_Flags & TempFlags.Delete) != 0;
                    bool cancelled = (temp.m_Flags & (TempFlags.Cancel | TempFlags.Replace)) != 0;
                    int index = edits.FindIndex(edit => edit.Realized == Entity.Null && edit.Original == temp.m_Original
                        && !cancelled && (edit.Remove ? removed : !removed && MatchesOsmAdministrativeBoundary(preview, edit)));
                    if (index < 0)
                    {
                        if (temp.m_Original != Entity.Null && !removed && !cancelled
                            && CityOsmAdministrativeBoundaries.SameRing(OsmAdministrativeBoundary.ReadRing(EntityManager, preview),
                                OsmAdministrativeBoundary.ReadRing(EntityManager, temp.m_Original))) continue;
                        throw new InvalidOperationException("The district preview includes a boundary change outside the requested edit.");
                    }
                    var item = edits[index];
                    item.Realized = item.Original == Entity.Null ? preview : item.Original;
                    edits[index] = item;
                }
                if (edits.Exists(edit => edit.Realized == Entity.Null)) throw new InvalidOperationException("A requested administrative boundary is missing from the city preview.");
            }
        }

        private bool AppliedOsmAdministrativeBoundariesMatch()
        {
            foreach (var item in m_OsmEdit.Areas.Boundaries)
            {
                if (item.Remove)
                {
                    if (EntityManager.Exists(item.Original) && !EntityManager.HasComponent<Deleted>(item.Original)) return false;
                    continue;
                }
                if (!IsAppliedOsmFeature(item.Realized)) return false;
                if (!MatchesOsmAdministrativeBoundary(item.Realized, item))
                    throw new InvalidOperationException($"Administrative boundary {item.RelationId} differs from the accepted preview.");
            }
            return true;
        }

        private bool MatchesOsmAdministrativeBoundary(Entity entity, OsmAdministrativeBoundary item) =>
            EntityManager.HasComponent<District>(entity) && EntityManager.HasBuffer<AreaNode>(entity)
            && (EntityManager.GetComponentData<Area>(entity).m_Flags & AreaFlags.Complete) != 0
            && EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab == item.Prefab
            && CityOsmAdministrativeBoundaries.SameRing(OsmAdministrativeBoundary.ReadRing(EntityManager, entity), item.Ring);
    }
}
