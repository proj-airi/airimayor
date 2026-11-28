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
        internal const int MinimumOperationalCorners = 4;
        internal const int MaximumOperationalCorners = 16;
        private const float MinimumOperationalCornerDistance = 4f;

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
            if (PolygonArea(ring) < 0.001f) return false;
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
            if (DistanceToPolygon(bounds.xy, ring) == 0f) return true;
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
            const string cornerLimits = "An operational perimeter needs 4–16 corners at least 4 meters apart.";
            if (next.Count < MinimumOperationalCorners || next.Count > MaximumOperationalCorners)
                throw new FormatException(cornerLimits);
            for (int i = 0; i < next.Count; i++)
            {
                if (math.distancesq(next[i], next[(i + 1) % next.Count]) < MinimumOperationalCornerDistance * MinimumOperationalCornerDistance)
                    throw new FormatException(cornerLimits);
            }
            if (!math.all(old[0] == next[0]) || !math.all(old[1] == next[1]))
                throw new FormatException("Keep the first two corners of the perimeter at the facility's locked edge.");
            if (PolygonArea(next) <= PolygonArea(old) + 1f)
                throw new FormatException("An operational area can only expand.");
            if (!ContainsPolygon(next, old))
                throw new FormatException("The expanded perimeter must contain the entire existing operational area.");
        }

        private static float PolygonArea(IReadOnlyList<float2> ring)
        {
            float twiceArea = 0f;
            for (int i = 0; i < ring.Count; i++)
                twiceArea += Cross(ring[i], ring[(i + 1) % ring.Count]);
            return math.abs(twiceArea) * 0.5f;
        }

        internal static float DistanceToPolygon(float2 point, IReadOnlyList<float2> polygon)
        {
            bool inside = false;
            float distance = float.MaxValue;
            for (int i = 0, previous = polygon.Count - 1; i < polygon.Count; previous = i++)
            {
                float2 a = polygon[previous], b = polygon[i];
                float2 edge = b - a;
                float t = math.saturate(math.dot(point - a, edge) / math.max(0.001f, math.lengthsq(edge)));
                distance = math.min(distance, math.distance(point, a + edge * t));
                if ((a.y > point.y) != (b.y > point.y) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside ? 0f : distance;
        }

        private static bool ContainsPolygon(IReadOnlyList<float2> outer, IReadOnlyList<float2> inner)
        {
            for (int i = 0; i < inner.Count; i++)
            {
                float2 a = inner[i], b = inner[(i + 1) % inner.Count];
                var cuts = new List<float> { 0f, 1f };
                for (int j = 0; j < outer.Count; j++)
                {
                    float2 c = outer[j], d = outer[(j + 1) % outer.Count];
                    float det = Cross(b - a, d - c);
                    if (math.abs(det) <= 0.000001f) continue;
                    float u = Cross(c - a, b - a) / det;
                    if (u >= 0f && u <= 1f) cuts.Add(math.saturate(Cross(c - a, d - c) / det));
                }
                cuts.Sort();
                for (int k = 1; k < cuts.Count; k++)
                    if (DistanceToPolygon(math.lerp(a, b, (cuts[k - 1] + cuts[k]) * 0.5f), outer) > 0.001f)
                        return false;
            }
            return true;
        }
    }
}
