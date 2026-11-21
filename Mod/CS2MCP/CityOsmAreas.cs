using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static class CityOsmAreas
    {
        internal const string Zone = "airimayor:zone";
        internal const string Perimeter = "airimayor:operational_area";
        internal const string TargetArea = "airimayor:target_area_m2";

        internal static bool IsArea(OsmWay way) => CityOsm.Tag(way, Zone) != null || CityOsm.Tag(way, Perimeter) != null;

        internal static List<CityOsmEditItem> TakeChanges(List<CityOsmEditItem> changes)
        {
            var areas = changes.Where(item => IsArea(item.Original as OsmWay) || IsArea(item.Edited as OsmWay)).ToList();
            changes.RemoveAll(item => IsArea(item.Original as OsmWay) || IsArea(item.Edited as OsmWay));
            foreach (var item in areas)
                if ((item.Original != null && !IsArea(item.Original as OsmWay)) || (item.Edited != null && !IsArea(item.Edited as OsmWay)))
                    throw new FormatException("A zoning or operational perimeter cannot change into a different city feature.");
            return areas;
        }

        internal static List<float2> Ring(OsmDocument document, OsmWay way, float4 bounds)
        {
            if (way.NodeIds.Count < 4 || way.NodeIds[0] != way.NodeIds[way.NodeIds.Count - 1])
                throw new FormatException($"Area way {way.Id} needs a closed perimeter.");
            var vertices = way.NodeIds.Take(way.NodeIds.Count - 1).Select(id => CityOsm.Position(document.Nodes[id]).xz).ToList();
            if (vertices.Any(p => math.any(p < bounds.xy) || math.any(p > bounds.zw)))
                throw new FormatException($"Read a rectangle covering the whole perimeter of way {way.Id}.");
            if (!Simple(vertices)) throw new FormatException($"Area way {way.Id} needs a simple polygon with distinct corners.");
            return vertices;
        }

        private static bool Simple(IReadOnlyList<float2> ring)
        {
            if (OperationalAreaPlanningMath.PolygonArea(ring) < 0.001f) return false;
            for (int i = 0; i < ring.Count; i++)
            {
                float2 a = ring[i], b = ring[(i + 1) % ring.Count], previous = ring[(i + ring.Count - 1) % ring.Count];
                if (math.distancesq(a, b) < 0.000001f || (math.abs(Cross(b - a, previous - a)) < 0.00001f && math.dot(b - a, previous - a) > 0f)) return false;
                for (int j = i + 2; j < ring.Count; j++)
                {
                    if (i == 0 && j == ring.Count - 1) continue;
                    if (Intersect(a, b, ring[j], ring[(j + 1) % ring.Count])) return false;
                }
            }
            return true;
        }

        internal static bool Intersects(IReadOnlyList<float2> ring, float4 bounds)
        {
            if (ring.Any(p => math.all(p >= bounds.xy) && math.all(p <= bounds.zw))) return true;
            float2[] rectangle = { bounds.xy, new float2(bounds.z, bounds.y), bounds.zw, new float2(bounds.x, bounds.w) };
            if (OperationalAreaPlanningMath.DistanceToPolygon(bounds.xy, ring) == 0f) return true;
            for (int i = 0; i < ring.Count; i++)
                for (int j = 0; j < 4; j++)
                    if (Intersect(ring[i], ring[(i + 1) % ring.Count], rectangle[j], rectangle[(j + 1) % 4])) return true;
            return false;
        }

        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        private static bool Intersect(float2 a, float2 b, float2 c, float2 d)
        {
            if (math.any(math.max(a, b) < math.min(c, d)) || math.any(math.max(c, d) < math.min(a, b))) return false;
            return Cross(b - a, c - a) * Cross(b - a, d - a) <= 0 && Cross(d - c, a - c) * Cross(d - c, b - c) <= 0;
        }

        internal static void CheckExpansion(IReadOnlyList<float2> old, IReadOnlyList<float2> next)
        {
            if (next.Count < OperationalAreaPlanningMath.MinNodeCount || next.Count > OperationalAreaPlanningMath.MaxNodeCount
                || !OperationalAreaPlanningMath.HasMinimumSpacing(next, OperationalAreaPlanningMath.MinNodeDistance))
                throw new FormatException("An operational perimeter needs 4–16 corners at least 4 meters apart.");
            if (!math.all(old[0] == next[0]) || !math.all(old[1] == next[1]))
                throw new FormatException("Keep the first two corners of the perimeter at the facility's locked edge.");
            if (OperationalAreaPlanningMath.PolygonArea(next) <= OperationalAreaPlanningMath.PolygonArea(old) + 1f)
                throw new FormatException("An operational area can only expand.");
            if (!OperationalAreaPlanningMath.ContainsPolygon(next, old))
                throw new FormatException("The expanded perimeter must contain the entire existing operational area.");
        }
    }
}
