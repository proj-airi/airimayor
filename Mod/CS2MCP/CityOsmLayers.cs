using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static class CityOsmLayers
    {
        private const float CellMeters = 64f;
        private const float MinimumHeightDifference = 1f;

        private readonly struct Span
        {
            public readonly int Road;
            public readonly int Step;
            public readonly int Count;
            public readonly double2 Start;
            public readonly double2 End;
            public readonly int2 FirstCell;
            public readonly int2 LastCell;

            public Span(int road, int step, int count, float2 start, float2 end)
            {
                Road = road;
                Step = step;
                Count = count;
                Start = start;
                End = end;
                FirstCell = (int2)math.floor(math.min(start, end) / CellMeters);
                LastCell = (int2)math.floor(math.max(start, end) / CellMeters);
            }

            public float Parameter(double fraction) => (float)((Step + fraction) / Count);
        }

        internal static void Assign(OsmDocument document, IReadOnlyList<CityOsmRoadSource> decks)
        {
            var crossings = CrossingOrder(decks);
            var successors = new List<int>[decks.Count];
            var prerequisites = new int[decks.Count];
            foreach (var crossing in crossings)
            {
                if (successors[crossing.First] == null)
                {
                    successors[crossing.First] = new List<int>();
                }
                successors[crossing.First].Add(crossing.Next);
                prerequisites[crossing.Next]++;
            }
            var ready = new Queue<int>();
            var depth = new int[decks.Count];
            for (int i = 0; i < decks.Count; i++)
            {
                if (prerequisites[i] == 0) ready.Enqueue(i);
            }
            while (ready.Count > 0)
            {
                int current = ready.Dequeue();
                if (successors[current] == null) continue;
                foreach (int next in successors[current])
                {
                    depth[next] = math.max(depth[next], depth[current] + 1);
                    if (--prerequisites[next] == 0) ready.Enqueue(next);
                }
            }
            for (int i = 0; i < decks.Count; i++)
            {
                int sign = decks[i].Grade == MapGrade.Tunnel ? -1 : 1;
                int layer = prerequisites[i] == 0 ? sign * (1 + depth[i]) : sign;
                document.Ways[CityOsm.NativeId(decks[i].Index)].Tags["layer"] = layer.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static HashSet<(int First, int Next)> CrossingOrder(IReadOnlyList<CityOsmRoadSource> decks)
        {
            var cells = new Dictionary<int2, List<Span>>();
            var crossings = new HashSet<(int First, int Next)>();
            for (int i = 0; i < decks.Count; i++)
            {
                RoadPath curve = decks[i].Curve;
                float longestHandle = math.max(math.distance(curve.A.xz, curve.B.xz),
                    math.max(math.distance(curve.B.xz, curve.C.xz), math.distance(curve.C.xz, curve.D.xz)));
                int steps = math.max(CityOsm.SampleCount(curve), (int)math.ceil(3f * longestHandle / CellMeters));
                float2 start = curve.A.xz;
                for (int step = 0; step < steps; step++)
                {
                    float2 end = NetworkCourseMath.Point(curve, (step + 1f) / steps).xz;
                    var span = new Span(i, step, steps, start, end);
                    start = end;
                    for (int x = span.FirstCell.x; x <= span.LastCell.x; x++)
                    {
                        for (int z = span.FirstCell.y; z <= span.LastCell.y; z++)
                        {
                            var key = new int2(x, z);
                            if (!cells.TryGetValue(key, out List<Span> bucket))
                            {
                                bucket = new List<Span>();
                                cells.Add(key, bucket);
                            }
                            foreach (Span existing in bucket)
                            {
                                if (span.Road != existing.Road && decks[i].Grade == decks[existing.Road].Grade
                                    && math.all(key == math.max(span.FirstCell, existing.FirstCell)))
                                {
                                    CompareCrossing(span, existing, decks, crossings);
                                }
                            }
                            bucket.Add(span);
                        }
                    }
                }
            }
            return crossings;
        }

        private static void CompareCrossing(Span a, Span b, IReadOnlyList<CityOsmRoadSource> decks,
            HashSet<(int First, int Next)> crossings)
        {
            double2 u = a.End - a.Start;
            double2 v = b.End - b.Start;
            double2 delta = b.Start - a.Start;
            double determinant = Cross(u, v);
            if (math.abs(determinant) > 1e-8)
            {
                double ta = Cross(delta, v) / determinant;
                double tb = Cross(delta, u) / determinant;
                if (ta >= 0 && ta <= 1 && tb >= 0 && tb <= 1)
                {
                    CompareHeight(a, ta, b, tb, decks, crossings);
                }
                return;
            }
            double squaredU = math.lengthsq(u);
            double squaredV = math.lengthsq(v);
            if (squaredU == 0 || squaredV == 0 || math.abs(Cross(delta, u)) > 1e-8) return;
            double start = math.dot(delta, u) / squaredU;
            double end = math.dot(b.End - a.Start, u) / squaredU;
            double first = math.max(0, math.min(start, end));
            double last = math.min(1, math.max(start, end));
            if (first > last) return;
            CompareHeight(a, first, b, math.dot(a.Start + u * first - b.Start, v) / squaredV, decks, crossings);
            CompareHeight(a, last, b, math.dot(a.Start + u * last - b.Start, v) / squaredV, decks, crossings);
        }

        private static int? Endpoint(CityOsmRoadSource road, float t)
        {
            if (t <= 0.000001f) return road.Start.Index;
            if (t >= 0.999999f) return road.End.Index;
            return null;
        }

        private static double Cross(double2 a, double2 b) => a.x * b.y - a.y * b.x;

        private static void CompareHeight(Span a, double fractionA, Span b, double fractionB,
            IReadOnlyList<CityOsmRoadSource> decks, HashSet<(int First, int Next)> crossings)
        {
            CityOsmRoadSource roadA = decks[a.Road];
            CityOsmRoadSource roadB = decks[b.Road];
            float ta = a.Parameter(fractionA);
            float tb = b.Parameter(fractionB);
            int? junctionA = Endpoint(roadA, ta);
            if (junctionA.HasValue && junctionA == Endpoint(roadB, tb)) return;
            float heightA = NetworkCourseMath.Point(roadA.Curve, ta).y;
            float heightB = NetworkCourseMath.Point(roadB.Curve, tb).y;
            if (math.abs(heightA - heightB) < MinimumHeightDifference) return;
            bool aFirst = roadA.Grade == MapGrade.Tunnel ? heightA > heightB : heightA < heightB;
            crossings.Add(aFirst ? (a.Road, b.Road) : (b.Road, a.Road));
        }
    }
}
