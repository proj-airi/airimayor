using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace CS2MCP
{
    internal readonly struct OsmTerrainHeightfield
    {
        internal OsmTerrainHeightfield(float4 bounds, int columns, int rows, float[] heights)
        {
            XMin = bounds.x;
            ZMin = bounds.y;
            XMax = bounds.z;
            ZMax = bounds.w;
            Columns = columns;
            Rows = rows;
            Heights = heights;
        }

        public double XMin { get; }
        public double ZMin { get; }
        public double XMax { get; }
        public double ZMax { get; }
        public int Columns { get; }
        public int Rows { get; }
        public double XStep => (XMax - XMin) / (Columns - 1);
        public double ZStep => (ZMax - ZMin) / (Rows - 1);
        public string SampleOrder => "z-ascending-x-fastest";
        public float[] Heights { get; }
    }

    internal static class CityOsmSurface
    {
        private const int TerrainMaxIntervals = 192;
        private const int WaterMaxIntervals = 511;
        private const long FirstWaterId = 1L << 62;
        private const int MaxWayNodes = 2000;
        private const float ShoreDepthMeters = 0.05f;

        internal static OsmTerrainHeightfield SampleTerrain(float4 bounds, float nativeSpacing, Func<float2, float> sampleHeight)
        {
            int columns = TerrainIntervals(bounds.z - bounds.x, nativeSpacing) + 1;
            int rows = TerrainIntervals(bounds.w - bounds.y, nativeSpacing) + 1;
            var heights = new float[rows * columns];
            for (int row = 0; row < rows; row++)
            {
                float z = math.lerp(bounds.y, bounds.w, row / (float)(rows - 1));
                for (int column = 0; column < columns; column++)
                {
                    float x = math.lerp(bounds.x, bounds.z, column / (float)(columns - 1));
                    heights[row * columns + column] = sampleHeight(new float2(x, z));
                }
            }
            return new OsmTerrainHeightfield(bounds, columns, rows, heights);
        }

        private static int TerrainIntervals(double span, float spacing) =>
            (int)Math.Min(TerrainMaxIntervals, Math.Max(1, Math.Ceiling(span / spacing)));

        internal static void AddWater(OsmDocument document, float4 bounds, float nativeSpacing, Func<float2, float> sampleDepth)
        {
            double spacing = nativeSpacing;
            double span = Math.Max((double)bounds.z - bounds.x, (double)bounds.w - bounds.y);
            while (span > spacing * (WaterMaxIntervals - 1)) spacing *= 2;
            double[] columns = WaterAxis(bounds.x, bounds.z, spacing);
            double[] rows = WaterAxis(bounds.y, bounds.w, spacing);
            var depths = new float[columns.Length * rows.Length];
            for (int row = 0; row < rows.Length; row++)
                for (int column = 0; column < columns.Length; column++)
                    depths[row * columns.Length + column] =
                        sampleDepth(new float2((float)columns[column], (float)rows[row])) - ShoreDepthMeters;

            Dictionary<int, int> segments = ShoreSegments(depths, columns.Length, rows.Length);
            var rings = new List<ShoreRing>();
            foreach (int start in segments.Keys.OrderBy(key => key).ToArray())
            {
                if (!segments.ContainsKey(start)) continue;
                var coordinates = new List<double2>();
                int current = start;
                do
                {
                    coordinates.Add(ShorePosition(current, depths, columns, rows));
                    int next = segments[current];
                    segments.Remove(current);
                    current = next;
                } while (current != start);

                double2[] ring = RemoveStraightVertices(coordinates);
                if (ring.Length >= 3) rings.Add(new ShoreRing(ring));
            }
            WriteWater(document, rings);
        }

        private static double[] WaterAxis(double minimum, double maximum, double spacing)
        {
            var positions = new List<double> { minimum };
            for (double next = (Math.Floor(minimum / spacing) + 1) * spacing; next < maximum; next += spacing)
                positions.Add(next);
            positions.Add(maximum);
            return positions.ToArray();
        }

        private static Dictionary<int, int> ShoreSegments(float[] depths, int columns, int rows)
        {
            var segments = new Dictionary<int, int>();
            for (int row = 0; row < rows - 1; row++)
            {
                for (int column = 0; column < columns - 1; column++)
                {
                    int bottomLeft = row * columns + column;
                    int bottomRight = bottomLeft + 1;
                    int topLeft = bottomLeft + columns;
                    int topRight = topLeft + 1;
                    float a = depths[bottomLeft], b = depths[bottomRight];
                    float c = depths[topRight], d = depths[topLeft];
                    int bottom = bottomLeft * 3 + 1, right = bottomRight * 3 + 2;
                    int top = topLeft * 3 + 1, left = bottomLeft * 3 + 2;
                    int mask = (a > 0f ? 1 : 0) | (b > 0f ? 2 : 0) | (c > 0f ? 4 : 0) | (d > 0f ? 8 : 0);
                    switch (mask)
                    {
                        case 1: segments.Add(bottom, left); break;
                        case 2: segments.Add(right, bottom); break;
                        case 3: segments.Add(right, left); break;
                        case 4: segments.Add(top, right); break;
                        case 5:
                            if ((double)a * c > (double)b * d)
                            {
                                segments.Add(bottom, right);
                                segments.Add(top, left);
                            }
                            else
                            {
                                segments.Add(bottom, left);
                                segments.Add(top, right);
                            }
                            break;
                        case 6: segments.Add(top, bottom); break;
                        case 7: segments.Add(top, left); break;
                        case 8: segments.Add(left, top); break;
                        case 9: segments.Add(bottom, top); break;
                        case 10:
                            if ((double)b * d > (double)a * c)
                            {
                                segments.Add(right, top);
                                segments.Add(left, bottom);
                            }
                            else
                            {
                                segments.Add(right, bottom);
                                segments.Add(left, top);
                            }
                            break;
                        case 11: segments.Add(right, top); break;
                        case 12: segments.Add(left, right); break;
                        case 13: segments.Add(bottom, right); break;
                        case 14: segments.Add(left, bottom); break;
                    }

                    if (row == 0) CloseWetEdge(segments, bottomLeft, bottomRight, bottom, a, b);
                    if (column == columns - 2) CloseWetEdge(segments, bottomRight, topRight, right, b, c);
                    if (row == rows - 2) CloseWetEdge(segments, topRight, topLeft, top, c, d);
                    if (column == 0) CloseWetEdge(segments, topLeft, bottomLeft, left, d, a);
                }
            }
            return segments;
        }

        private static void CloseWetEdge(Dictionary<int, int> segments, int start, int end, int crossing, float first, float second)
        {
            if (first > 0f)
                segments.Add(start * 3, second > 0f ? end * 3 : crossing);
            else if (second > 0f)
                segments.Add(crossing, end * 3);
        }

        private static double2 ShorePosition(int vertex, float[] depths, double[] columns, double[] rows)
        {
            int point = vertex / 3, kind = vertex % 3;
            int column = point % columns.Length, row = point / columns.Length;
            double x = columns[column], z = rows[row];
            if (kind == 1) x += (columns[column + 1] - x) * CrossingFraction(depths[point], depths[point + 1]);
            if (kind == 2) z += (rows[row + 1] - z) * CrossingFraction(depths[point], depths[point + columns.Length]);
            return new double2(x, z);
        }

        private static double CrossingFraction(float start, float end) =>
            math.clamp(start / ((double)start - end), 0.00001d, 0.99999d);

        private static double2[] RemoveStraightVertices(List<double2> ring)
        {
            var result = new List<double2>();
            for (int i = 0; i < ring.Count; i++)
            {
                double2 incoming = ring[i] - ring[(i + ring.Count - 1) % ring.Count];
                double2 outgoing = ring[(i + 1) % ring.Count] - ring[i];
                if (Cross(incoming, outgoing) != 0d || math.dot(incoming, outgoing) <= 0d) result.Add(ring[i]);
            }
            return result.ToArray();
        }

        private readonly struct ShoreRing
        {
            internal ShoreRing(double2[] coordinates)
            {
                Coordinates = coordinates;
                double2 minimum = coordinates[0], maximum = minimum;
                double area = 0d;
                for (int i = 0; i < coordinates.Length; i++)
                {
                    double2 point = coordinates[i];
                    minimum = math.min(minimum, point);
                    maximum = math.max(maximum, point);
                    area += Cross(point - coordinates[0], coordinates[(i + 1) % coordinates.Length] - coordinates[0]);
                }
                Area = area * 0.5d;
                Bounds = new double4(minimum, maximum);
            }

            internal double2[] Coordinates { get; }
            internal double Area { get; }
            private double4 Bounds { get; }

            internal bool Contains(double2 point)
            {
                if (math.any(point < Bounds.xy) || math.any(point > Bounds.zw)) return false;
                bool inside = false;
                for (int i = 0, previous = Coordinates.Length - 1; i < Coordinates.Length; previous = i++)
                {
                    double2 a = Coordinates[previous], b = Coordinates[i];
                    if ((a.y > point.y) != (b.y > point.y) && point.x < a.x + (b.x - a.x) * (point.y - a.y) / (b.y - a.y))
                        inside = !inside;
                }
                return inside;
            }
        }

        private static double Cross(double2 a, double2 b) => a.x * b.y - a.y * b.x;

        private static void WriteWater(OsmDocument document, List<ShoreRing> rings)
        {
            ShoreRing[] outer = rings.Where(ring => ring.Area > 0d).OrderBy(ring => ring.Area).ToArray();
            var islands = new List<ShoreRing>[outer.Length];
            foreach (ShoreRing inner in rings.Where(ring => ring.Area < 0d))
            {
                int owner = Array.FindIndex(outer, candidate => candidate.Contains(inner.Coordinates[0]));
                if (owner < 0) throw new InvalidOperationException("The sampled shore has an island outside its water area.");
                if (islands[owner] == null) islands[owner] = new List<ShoreRing>();
                islands[owner].Add(inner);
            }
            long nextNode = FirstWaterId, nextWay = FirstWaterId, nextRelation = FirstWaterId;
            for (int i = 0; i < outer.Length; i++)
            {
                long[] outline = WriteRing(document, outer[i], ref nextNode, ref nextWay);
                if (outline.Length == 1 && islands[i] == null)
                {
                    document.Ways[outline[0]].Tags["natural"] = "water";
                    continue;
                }
                var water = new OsmRelation(nextRelation++);
                water.Attributes["version"] = "1";
                water.Tags["type"] = "multipolygon";
                water.Tags["natural"] = "water";
                foreach (long way in outline) water.Members.Add(new OsmMember(OsmKind.Way, way, "outer"));
                if (islands[i] != null)
                    foreach (ShoreRing inner in islands[i])
                        foreach (long way in WriteRing(document, inner, ref nextNode, ref nextWay))
                            water.Members.Add(new OsmMember(OsmKind.Way, way, "inner"));
                document.Relations.Add(water.Id, water);
            }
        }

        private static long[] WriteRing(OsmDocument document, ShoreRing ring, ref long nextNode, ref long nextWay)
        {
            int segments = ring.Coordinates.Length;
            var ids = new long[segments + 1];
            for (int i = 0; i < segments; i++)
            {
                double2 point = ring.Coordinates[i];
                var node = new OsmNode(nextNode++, point.y / CityOsm.LatitudeMeters, point.x / CityOsm.LongitudeMeters);
                node.Attributes["version"] = "1";
                document.Nodes.Add(node.Id, node);
                ids[i] = node.Id;
            }
            ids[segments] = ids[0];
            var ways = new List<long>();
            for (int start = 0; start < segments; start += MaxWayNodes - 1)
            {
                int count = Math.Min(MaxWayNodes, ids.Length - start);
                var way = new OsmWay(nextWay++, new ArraySegment<long>(ids, start, count));
                way.Attributes["version"] = "1";
                document.Ways.Add(way.Id, way);
                ways.Add(way.Id);
            }
            return ways.ToArray();
        }
    }
}
