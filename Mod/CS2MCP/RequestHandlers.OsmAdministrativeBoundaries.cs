using System;
using System.Collections.Generic;
using Colossal.Mathematics;
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
    public sealed partial class RequestHandlers
    {
        private IEnumerable<(Entity Area, List<float2> Ring)> OsmAdministrativeAreas(float4 bounds, IEnumerable<Entity> candidates)
        {
            foreach (Entity area in candidates)
            {
                if (!EntityManager.HasComponent<District>(area) || !EntityManager.HasComponent<Area>(area)
                    || !EntityManager.HasBuffer<AreaNode>(area) || !EntityManager.HasComponent<PrefabRef>(area)
                    || EntityManager.HasComponent<Temp>(area) || EntityManager.HasComponent<Deleted>(area)
                    || (EntityManager.GetComponentData<Area>(area).m_Flags & AreaFlags.Complete) == 0) continue;
                var ring = OsmAdministrativeBoundary.ReadRing(EntityManager, area);
                if (ring.Count >= 3 && CityOsmAreas.Intersects(ring, bounds)) yield return (area, ring);
            }
        }

        private void AddOsmAdministrativeBoundaries(OsmDocument document, float4 bounds, IEnumerable<Entity> candidates)
        {
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            foreach (var (area, ring) in OsmAdministrativeAreas(bounds, candidates))
            {
                long id = CityOsm.NativeId(area.Index);
                var outline = AddOsmRing(document, id, (1L << 56) + ((long)area.Index << 20), ring);
                outline.Tags.Remove("area");
                var relation = new OsmRelation(id);
                relation.Attributes["version"] = "1";
                relation.Tags["type"] = "boundary";
                relation.Tags["boundary"] = "administrative";
                relation.Tags["airimayor:entity"] = OsmEntityIdentity(area);
                string name = names.GetRenderedLabelName(area);
                if (!string.IsNullOrEmpty(name)) relation.Tags["name"] = name;
                relation.Members.Add(new OsmMember(OsmKind.Way, outline.Id, "outer"));
                document.Relations.Add(id, relation);
            }
        }

        private IEnumerable<(float Rank, int Id, object Item)> OsmAdministrativeRows(float4 bounds, string search)
        {
            var names = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            float2 center = (bounds.xy + bounds.zw) * 0.5f;
            foreach (var (area, ring) in OsmAdministrativeAreas(bounds, OsmAreaCandidates(bounds)))
            {
                string name = names.GetRenderedLabelName(area);
                if (!MatchesOsmName(search, name, null)) continue;
                float2 minimum = ring[0], maximum = ring[0];
                foreach (float2 point in ring)
                {
                    minimum = math.min(minimum, point);
                    maximum = math.max(maximum, point);
                }
                float distance = CityOsmAreas.DistanceToPolygon(center, ring);
                yield return (distance, area.Index, new
                {
                    relationId = CityOsm.NativeId(area.Index), name,
                    bounds = OsmQueryBounds(new Bounds2(minimum, maximum)), distanceM = Math.Round(distance, 1),
                });
            }
        }

        private OsmAdministrativeBoundary PlanOsmAdministrativeBoundary(CityOsmEditItem change, OsmDocument before, OsmDocument after, float4 bounds)
        {
            var original = change.Original as OsmRelation;
            var edited = change.Edited as OsmRelation;
            if (original != null) CityOsmAdministrativeBoundaries.Ring(before, original, bounds);
            Entity area = OsmOriginal(original);
            Entity prefab = area == Entity.Null ? OsmDistrictPrefab() : EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            if (edited == null)
                return new OsmAdministrativeBoundary { RelationId = original.Id, Original = area, Prefab = prefab, Remove = true };

            CityOsmEdit.CheckEditedTags(original, edited, "type", "boundary", "name");
            var ring = CityOsmAdministrativeBoundaries.Ring(after, edited, bounds);
            return new OsmAdministrativeBoundary
            {
                RelationId = edited.Id, Original = area, Prefab = prefab,
                Ring = ring, Name = OsmRequestedName(original, edited),
            };
        }

        private Entity OsmDistrictPrefab()
        {
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<DistrictData>(), ComponentType.ReadOnly<AreaGeometryData>(),
                ComponentType.ReadOnly<PrefabData>()))
            using (var items = query.ToEntityArray(Allocator.Temp))
            {
                Entity selected = Entity.Null;
                foreach (Entity candidate in items)
                {
                    if (IsLocked(candidate) || EntityManager.GetComponentData<AreaGeometryData>(candidate).m_Type != AreaType.District) continue;
                    if (selected == Entity.Null || candidate.Index < selected.Index) selected = candidate;
                }
                if (selected != Entity.Null) return selected;
                throw new FormatException("No unlocked district drawing tool is available in this city.");
            }
        }
    }
}
