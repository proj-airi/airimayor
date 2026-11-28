using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace CS2MCP
{
    internal readonly struct CityOsmEditItem
    {
        public CityOsmEditItem(OsmElement original, OsmElement edited)
        {
            Original = original;
            Edited = edited;
        }

        public OsmElement Original { get; }
        public OsmElement Edited { get; }
    }

    internal static class CityOsmEdit
    {
        public static List<CityOsmEditItem> Changes(CityOsmGeometry before, CityOsmGeometry after, float4 bounds)
        {
            OsmDocument source = before.Document;
            OsmDocument target = after.Document;
            var items = new List<CityOsmEditItem>();
            foreach (long id in source.Relations.Keys.Union(target.Relations.Keys))
            {
                source.Relations.TryGetValue(id, out OsmRelation original);
                target.Relations.TryGetValue(id, out OsmRelation edited);
                if (original == null || !original.SameContent(edited))
                {
                    if (CityOsm.Tag(original ?? edited, "type") != "route")
                        throw new FormatException($"Relation {id} has no city construction operation.");
                    items.Add(new CityOsmEditItem(original, edited));
                }
            }

            var shapeNodes = new HashSet<long>(source.Ways.Values.Concat(target.Ways.Values).SelectMany(w => w.NodeIds));
            var changedNodes = new HashSet<long>();
            foreach (long id in source.Nodes.Keys.Union(target.Nodes.Keys))
            {
                source.Nodes.TryGetValue(id, out OsmNode original);
                target.Nodes.TryGetValue(id, out OsmNode edited);
                if (edited == null && CityOsmTransit.RemovedWithRoute(original, source, target)) continue;
                if (original != null && edited != null && SameNodePosition(original, edited)
                    && original.Tags.Count == edited.Tags.Count
                    && original.Tags.All(tag => tag.Key == "ele" || CityOsm.Tag(edited, tag.Key) == tag.Value))
                {
                    continue;
                }
                if (original != null) RequireInside(original.Id, before.Positions[id], bounds);
                if (edited != null) RequireInside(edited.Id, after.Positions[id], bounds);
                changedNodes.Add(id);
                bool point = CityOsmStandalonePoints.IsPoint(original) || CityOsmStandalonePoints.IsPoint(edited);
                if (point && shapeNodes.Contains(id))
                {
                    throw new FormatException($"Use a separate node for point feature {id}.");
                }
                if (!shapeNodes.Contains(id) || point)
                {
                    items.Add(new CityOsmEditItem(original, edited));
                    continue;
                }
                if (edited != null)
                {
                    CheckEditedTags(original, edited, "ele");
                    if (edited.Tags.ContainsKey("ele") && !math.isfinite(after.Positions[id].y))
                    {
                        throw new FormatException($"Node {id} needs a numeric elevation in meters.");
                    }
                }
            }

            var oldJunctions = Junctions(before);
            var newJunctions = Junctions(after);
            newJunctions.SymmetricExceptWith(oldJunctions);
            var originalRoads = before.Roads.ToDictionary(way => way.Id);
            var roadNodes = new HashSet<long>(after.Roads.SelectMany(road => target.Ways[road.Id].NodeIds));
            var lotNodes = new HashSet<long>(after.Buildings.SelectMany(building => target.Ways[building.Id].NodeIds));
            foreach (long id in source.Ways.Keys.Union(target.Ways.Keys))
            {
                source.Ways.TryGetValue(id, out OsmWay original);
                target.Ways.TryGetValue(id, out OsmWay edited);
                if (original != null && original.SameContent(edited)
                    && !original.NodeIds.Any(changedNodes.Contains)
                    && !original.NodeIds.Skip(1).Take(original.NodeIds.Count - 2).Any(newJunctions.Contains))
                {
                    continue;
                }
                if (original != null)
                {
                    RequireInside(original, before, bounds);
                    if (originalRoads.TryGetValue(id, out CityOsmRoad road))
                    {
                        foreach (RoadPath curve in road.Sections.SelectMany(section => section.Curves))
                        {
                            RequireCourseInside(id, curve, bounds);
                        }
                    }
                }
                if (edited != null)
                {
                    RequireInside(edited, after, bounds);
                    foreach (long node in edited.NodeIds)
                    {
                        if ((roadNodes.Contains(node) && lotNodes.Contains(node)) || CityOsmStandalonePoints.IsPoint(target.Nodes[node]))
                        {
                            throw new FormatException($"Way {edited.Id} needs a separate point at node {node} for each lot, point object and road junction.");
                        }
                    }
                }
                items.Add(new CityOsmEditItem(original, edited));
            }
            return items;
        }

        internal static bool SameNodePosition(OsmNode original, OsmNode edited)
        {
            if (Math.Round(original.Latitude, 7) != Math.Round(edited.Latitude, 7)
                || Math.Round(original.Longitude, 7) != Math.Round(edited.Longitude, 7)) return false;
            string oldHeight = CityOsm.Tag(original, "ele");
            string newHeight = CityOsm.Tag(edited, "ele");
            if (oldHeight == newHeight) return true;
            float oldY = CityOsm.Position(original).y;
            return math.isfinite(oldY) && oldY == CityOsm.Position(edited).y;
        }

        private static HashSet<long> Junctions(CityOsmGeometry city)
        {
            var nodes = new HashSet<long>();
            foreach (CityOsmRoad road in city.Roads)
            {
                foreach (CityOsmSection section in road.Sections)
                {
                    nodes.Add(section.StartNode);
                    nodes.Add(section.EndNode);
                }
            }
            return nodes;
        }

        internal static void CheckEditedTags(OsmElement original, OsmElement edited, params string[] supported)
        {
            var keys = original == null ? edited.Tags.Keys.AsEnumerable() : original.Tags.Keys.Union(edited.Tags.Keys);
            foreach (string key in keys)
            {
                if (CityOsm.Tag(original, key) != CityOsm.Tag(edited, key) && !supported.Contains(key, StringComparer.Ordinal))
                {
                    throw new FormatException($"{edited.Kind} {edited.Id} cannot change '{key}' in the city.");
                }
            }
        }

        private static void RequireInside(OsmWay way, CityOsmGeometry city, float4 bounds)
        {
            foreach (long id in way.NodeIds)
            {
                RequireInside(id, city.Positions[id], bounds);
            }
        }

        internal static void RequireInside(long id, float3 position, float4 bounds)
        {
            if (math.any(position.xz < bounds.xy) || math.any(position.xz > bounds.zw))
            {
                throw new FormatException($"Node {id} lies outside the district. Read an extent covering the complete edited feature.");
            }
        }

        internal static void SupplyDeckHeights(OsmWay way, CityOsmGeometry city, Dictionary<long, float> elevations,
            int layer, float elevationLimit, float step, float2 placementRange, Func<float3, float> terrain)
        {
            float offset = step > 0f ? layer * step * math.max(1f, math.ceil((elevationLimit + 0.01f) / step)) : 0f;
            foreach (long id in way.NodeIds)
            {
                float3 point = city.Positions[id];
                if (math.isfinite(point.y)) continue;
                if (city.Document.Nodes[id].Tags.ContainsKey("ele") || offset == 0f || offset < placementRange.x || offset > placementRange.y)
                {
                    throw new FormatException($"Specify the deck elevation in meters at node {id} for way {way.Id}.");
                }
                float height = terrain(point) + offset;
                if (elevations.TryGetValue(id, out float other) && math.abs(height - other) > 0.01f)
                {
                    throw new FormatException($"Specify one shared junction elevation at node {id}.");
                }
                elevations[id] = height;
            }
        }

        internal static void RequireCourseInside(long id, RoadPath curve, float4 bounds)
        {
            Bounds2 extent = MathUtils.Bounds(new Bezier4x2(curve.A.xz, curve.B.xz, curve.C.xz, curve.D.xz));
            if (math.any(extent.min < bounds.xy) || math.any(extent.max > bounds.zw))
            {
                throw new FormatException($"Way {id} leaves the district. Read an extent covering the complete curve.");
            }
        }

        public static IEnumerable<(long Start, long End, RoadPath Curve)> Courses(List<long> nodes,
            int first, int last, CityOsmSection section)
        {
            if (section.Curves.Count == 1)
            {
                yield return (nodes[first], nodes[last], section.Curves[0]);
                yield break;
            }
            var points = new float3[section.Curves.Count + 1];
            points[0] = section.Curves[0].A;
            for (int i = 1; i < points.Length; i++) points[i] = section.Curves[i - 1].D;
            var ranges = new Stack<(int From, int To)>();
            ranges.Push((0, points.Length - 1));
            while (ranges.Count > 0)
            {
                var (from, to) = ranges.Pop();
                if (TryFit(points, from, to, out RoadPath curve, out int split))
                {
                    yield return (nodes[first + from], nodes[first + to], curve);
                }
                else
                {
                    ranges.Push((split, to));
                    ranges.Push((from, split));
                }
            }
        }

        private static bool TryFit(float3[] points, int first, int last, out RoadPath curve, out int split)
        {
            float3 a = points[first];
            float3 d = points[last];
            curve = RoadPath.Straight(a, d);
            split = first + (last - first) / 2;
            if (last - first == 1) return true;
            var distances = new float[last - first + 1];
            for (int i = 1; i < distances.Length; i++)
            {
                distances[i] = distances[i - 1] + math.distance(points[first + i], points[first + i - 1]);
            }
            float length = distances[distances.Length - 1];
            if (length < 0.01f || math.distancesq(a.xz, d.xz) < 0.01f) return false;
            float3 startDirection = math.normalizesafe(points[first + 1] - a);
            float3 endDirection = math.normalizesafe(points[last - 1] - d);
            float xx = 0f, xy = 0f, yy = 0f, rx = 0f, ry = 0f;
            for (int i = 1; i < distances.Length - 1; i++)
            {
                float t = distances[i] / length;
                float s = 1f - t;
                float3 x = 3f * s * s * t * startDirection;
                float3 y = 3f * s * t * t * endDirection;
                float3 residual = points[first + i] - (s * s * (1f + 2f * t) * a + t * t * (3f - 2f * t) * d);
                xx += math.dot(x, x);
                xy += math.dot(x, y);
                yy += math.dot(y, y);
                rx += math.dot(x, residual);
                ry += math.dot(y, residual);
            }
            float determinant = xx * yy - xy * xy;
            if (determinant > 0.000001f)
            {
                float startLength = math.clamp((rx * yy - ry * xy) / determinant, 0f, length);
                float endLength = math.clamp((ry * xx - rx * xy) / determinant, 0f, length);
                curve = new RoadPath(a, a + startDirection * startLength, d + endDirection * endLength, d);
            }
            float worst = 0f;
            for (int i = 1; i < distances.Length; i++)
            {
                float3 previous = points[first + i - 1];
                float3 next = points[first + i];
                for (int check = 1; check <= 4; check++)
                {
                    float part = check * 0.25f;
                    float t = math.lerp(distances[i - 1], distances[i], part) / length;
                    float error = math.distance(NetworkCourseMath.Point(curve, t), math.lerp(previous, next, part));
                    if (error > worst)
                    {
                        worst = error;
                        split = math.clamp(first + i, first + 1, last - 1);
                    }
                }
            }
            return worst <= 0.25f;
        }

        public static (float3 Position, quaternion Rotation, float2 Size) LotPose(CityOsmBuilding building)
        {
            float3[] corners = building.Footprint;
            if (corners.Length != 4)
            {
                throw new FormatException($"Building {building.Id} requires a rectangular asset lot.");
            }
            float3 center = (corners[0] + corners[1] + corners[2] + corners[3]) / 4f;
            float2 right = corners[1].xz - corners[0].xz;
            float2 forward = corners[3].xz - corners[0].xz;
            float2 size = new float2(math.length(right), math.length(forward));
            if (math.cmin(size) < 1f
                || math.abs(math.dot(right / size.x, forward)) > 0.1f
                || math.distance(corners[2].xz, corners[0].xz + right + forward) > 0.1f)
            {
                throw new FormatException($"Building {building.Id} requires four corners of a rectangular lot.");
            }
            return (center, quaternion.RotateY(-math.atan2(right.y, right.x)), size);
        }
    }
}
