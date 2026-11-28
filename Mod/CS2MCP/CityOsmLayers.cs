using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static class CityOsmLayers
    {
        private const float CellMeters = 64f;
        private const float MinimumHeightDifference = 1f;
        private const float ParameterTolerance = 0.00001f;
        private const int Before = 1;
        private const int After = 2;

        private readonly struct Crossing : IEquatable<Crossing>
        {
            public Crossing(int first, float firstParameter, int next, float nextParameter)
            {
                First = first;
                FirstParameter = firstParameter;
                Next = next;
                NextParameter = nextParameter;
            }

            public int First { get; }
            public float FirstParameter { get; }
            public int Next { get; }
            public float NextParameter { get; }

            public bool Equals(Crossing other) => First == other.First && FirstParameter == other.FirstParameter
                && Next == other.Next && NextParameter == other.NextParameter;

            public override bool Equals(object other) => other is Crossing crossing && Equals(crossing);
            public override int GetHashCode() => (First, FirstParameter, Next, NextParameter).GetHashCode();
        }

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
            var order = new HashSet<(int First, int Next)>();
            VisitCrossings(decks, crossing => order.Add((crossing.First, crossing.Next)));
            int?[] depths = Rank(decks.Count, order);
            for (int i = 0; i < decks.Count; i++)
            {
                document.Ways[CityOsm.NativeId(decks[i].Index)].Tags["layer"] = Layer(decks[i].Grade, depths[i]);
            }
        }

        internal static (float[] Cuts, string[] Layers)[] LocalLayers(IReadOnlyList<CityOsmRoadSource> decks)
        {
            var crossings = new HashSet<Crossing>();
            VisitCrossings(decks, crossing => crossings.Add(crossing));
            int?[] depths = Rank(decks.Count, crossings.Select(crossing => (crossing.First, crossing.Next)).Distinct());
            float[][] cuts = CrossingCuts(depths, crossings);
            var offsets = new int[decks.Count];
            int count = 0;
            for (int i = 0; i < decks.Count; i++)
            {
                offsets[i] = count;
                count += cuts[i].Length - 1;
            }
            if (count != decks.Count)
            {
                depths = Rank(count, crossings.Select(crossing =>
                    (offsets[crossing.First] + PieceAt(cuts[crossing.First], crossing.FirstParameter),
                    offsets[crossing.Next] + PieceAt(cuts[crossing.Next], crossing.NextParameter))).Distinct());
            }
            var result = new (float[] Cuts, string[] Layers)[decks.Count];
            for (int i = 0; i < decks.Count; i++)
            {
                var layers = new string[cuts[i].Length - 1];
                for (int piece = 0; piece < layers.Length; piece++)
                    layers[piece] = Layer(decks[i].Grade, depths[offsets[i] + piece]);
                result[i] = (cuts[i], layers);
            }
            return result;
        }

        private static float[][] CrossingCuts(int?[] depths, IEnumerable<Crossing> crossings)
        {
            var positions = new List<(float Parameter, int Order)>[depths.Length];
            foreach (Crossing crossing in crossings)
            {
                AddCrossingPosition(crossing.First, crossing.FirstParameter, Before);
                AddCrossingPosition(crossing.Next, crossing.NextParameter, After);
            }

            var cuts = new float[depths.Length][];
            for (int road = 0; road < depths.Length; road++)
            {
                var parameters = positions[road];
                if (parameters == null)
                {
                    cuts[road] = new[] { 0f, 1f };
                    continue;
                }
                parameters.Sort((a, b) => a.Parameter.CompareTo(b.Parameter));
                var boundaries = new List<float> { 0f };
                int previousOrder = 0;
                float previousEnd = 0f;
                for (int i = 0; i < parameters.Count;)
                {
                    var (start, order) = parameters[i++];
                    float end = start;
                    while (i < parameters.Count && parameters[i].Parameter - start <= ParameterTolerance)
                    {
                        end = parameters[i].Parameter;
                        order |= parameters[i++].Order;
                    }
                    if (previousOrder != 0 && (!depths[road].HasValue || previousOrder != order))
                        boundaries.Add((previousEnd + start) * 0.5f);
                    previousOrder = order;
                    previousEnd = end;
                }
                boundaries.Add(1f);
                cuts[road] = boundaries.ToArray();
            }
            return cuts;

            void AddCrossingPosition(int index, float parameter, int order)
            {
                if (positions[index] == null) positions[index] = new List<(float, int)>();
                positions[index].Add((parameter, order));
            }
        }

        private static int PieceAt(float[] cuts, float parameter)
        {
            int index = Array.BinarySearch(cuts, parameter);
            return math.clamp(index < 0 ? ~index - 1 : index, 0, cuts.Length - 2);
        }

        private static string Layer(MapGrade grade, int? depth) =>
            ((grade == MapGrade.Tunnel ? -1 : 1) * (1 + (depth ?? 0))).ToString(CultureInfo.InvariantCulture);

        private static int?[] Rank(int count, IEnumerable<(int First, int Next)> order)
        {
            var successors = new List<int>[count];
            var prerequisites = new int[count];
            foreach (var crossing in order)
            {
                if (successors[crossing.First] == null)
                {
                    successors[crossing.First] = new List<int>();
                }
                successors[crossing.First].Add(crossing.Next);
                prerequisites[crossing.Next]++;
            }
            var ready = new Queue<int>();
            var depth = new int[count];
            for (int i = 0; i < count; i++)
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
            return depth.Select((value, index) => prerequisites[index] == 0 ? (int?)value : null).ToArray();
        }

        private static void VisitCrossings(IReadOnlyList<CityOsmRoadSource> decks, Action<Crossing> visit)
        {
            var cells = new Dictionary<int2, List<Span>>();
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
                                    CompareCrossing(span, existing, decks, visit);
                                }
                            }
                            bucket.Add(span);
                        }
                    }
                }
            }
        }

        private static void CompareCrossing(Span a, Span b, IReadOnlyList<CityOsmRoadSource> decks,
            Action<Crossing> visit)
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
                    CompareHeight(a, ta, b, tb, decks, visit);
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
            CompareHeight(a, first, b, math.dot(a.Start + u * first - b.Start, v) / squaredV, decks, visit);
            CompareHeight(a, last, b, math.dot(a.Start + u * last - b.Start, v) / squaredV, decks, visit);
        }

        private static int? Endpoint(CityOsmRoadSource road, float t)
        {
            if (t <= 0.000001f) return road.Start.Index;
            if (t >= 0.999999f) return road.End.Index;
            return null;
        }

        private static double Cross(double2 a, double2 b) => a.x * b.y - a.y * b.x;

        private static void CompareHeight(Span a, double fractionA, Span b, double fractionB,
            IReadOnlyList<CityOsmRoadSource> decks, Action<Crossing> visit)
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
            visit(aFirst ? new Crossing(a.Road, ta, b.Road, tb) : new Crossing(b.Road, tb, a.Road, ta));
        }
    }
}
