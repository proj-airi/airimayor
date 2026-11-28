using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Net;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static class CityOsmBridgeMap
    {
        private const int OutlineOffset = 1 << 25;
        private const float ParameterTolerance = 0.00001f;

        internal static bool Intersects(Bounds2 bounds, EdgeGeometry geometry)
        {
            var left = Side(geometry.m_Start.m_Left, geometry.m_End.m_Left);
            var right = Side(geometry.m_Start.m_Right, geometry.m_End.m_Right);
            var ring = Ring(left, right, 0f, left.Count - 1, 0f, right.Count - 1);
            return ring.Count >= 3 && CityOsmAreas.Intersects(ring.Select(point => point.xz).ToArray(),
                new float4(bounds.min, bounds.max));
        }

        internal static void Apply(OsmDocument map, IReadOnlyList<CityOsmRoadSource> roads,
            IReadOnlyDictionary<int, EdgeGeometry> bridgeGeometry)
        {
            var decks = roads.Where(road => road.Grade != MapGrade.Ground).OrderBy(road => road.Index).ToArray();
            var localLayers = CityOsmLayers.LocalLayers(decks);
            for (int i = 0; i < decks.Length; i++)
            {
                var (cuts, layers) = localLayers[i];
                SetDeckWays(map, decks[i], cuts, layers);
                if (decks[i].Grade == MapGrade.Bridge && bridgeGeometry.TryGetValue(decks[i].Index, out EdgeGeometry geometry))
                    AddOutlines(map, decks[i], cuts, layers, geometry);
            }
        }

        private static void SetDeckWays(OsmDocument map, CityOsmRoadSource road, float[] cuts, string[] layers)
        {
            long id = CityOsm.NativeId(road.Index);
            OsmWay source = map.Ways[id];
            if (layers.Length == 1)
            {
                source.Tags["layer"] = layers[0];
                return;
            }

            long[] nodes = source.NodeIds.ToArray();
            int steps = nodes.Length - 1;
            long[] boundaries = new long[cuts.Length];
            boundaries[0] = nodes[0];
            boundaries[cuts.Length - 1] = nodes[steps];
            for (int i = 1; i < cuts.Length - 1; i++)
            {
                float sample = cuts[i] * steps;
                int rounded = (int)math.round(sample);
                if (math.abs(sample - rounded) < ParameterTolerance)
                {
                    boundaries[i] = nodes[rounded];
                    continue;
                }
                long nodeId = DerivedId(road.Index, i);
                map.Nodes.Add(nodeId, CityOsm.ToNode(nodeId, NetworkCourseMath.Point(road.Curve, cuts[i])));
                boundaries[i] = nodeId;
            }

            for (int i = 0; i < layers.Length; i++)
            {
                long pieceId = i == 0 ? id : DerivedId(road.Index, i);
                var way = new OsmWay(pieceId);
                foreach (var tag in source.Tags)
                {
                    if (!tag.Key.StartsWith("airimayor:", StringComparison.Ordinal) || tag.Key == "airimayor:entity")
                        way.Tags.Add(tag.Key, tag.Value);
                }
                foreach (var attribute in source.Attributes) way.Attributes.Add(attribute.Key, attribute.Value);
                way.Tags["layer"] = layers[i];
                way.NodeIds.Add(boundaries[i]);
                for (int step = (int)math.floor(cuts[i] * steps) + 1; step < (int)math.ceil(cuts[i + 1] * steps); step++)
                {
                    if (nodes[step] != boundaries[i] && nodes[step] != boundaries[i + 1]) way.NodeIds.Add(nodes[step]);
                }
                way.NodeIds.Add(boundaries[i + 1]);
                map.Ways[pieceId] = way;
            }
        }

        private static void AddOutlines(OsmDocument map, CityOsmRoadSource road, float[] cuts, string[] layers,
            EdgeGeometry geometry)
        {
            var left = Side(geometry.m_Start.m_Left, geometry.m_End.m_Left);
            var right = Side(geometry.m_Start.m_Right, geometry.m_End.m_Right);
            float[] leftCuts = SideCuts(left, road.Curve, cuts);
            float[] rightCuts = SideCuts(right, road.Curve, cuts);
            var vertices = new Dictionary<float3, long>();
            for (int i = 0; i < layers.Length; i++)
            {
                var ring = Ring(left, right, leftCuts[i], leftCuts[i + 1], rightCuts[i], rightCuts[i + 1]);
                if (ring.Count < 3) continue;
                var outline = new OsmWay(DerivedId(road.Index, OutlineOffset + i));
                outline.Attributes["version"] = "1";
                outline.Tags["man_made"] = "bridge";
                outline.Tags["layer"] = layers[i];
                foreach (float3 point in ring)
                {
                    if (!vertices.TryGetValue(point, out long id))
                    {
                        id = DerivedId(road.Index, OutlineOffset + vertices.Count);
                        vertices.Add(point, id);
                        map.Nodes.Add(id, CityOsm.ToNode(id, point));
                    }
                    outline.NodeIds.Add(id);
                }
                outline.NodeIds.Add(outline.NodeIds[0]);
                map.Ways.Add(outline.Id, outline);
            }
        }

        private static float[] SideCuts(IReadOnlyList<float3> side, RoadPath center, float[] parameters)
        {
            var cuts = new float[parameters.Length];
            cuts[cuts.Length - 1] = side.Count - 1;
            for (int i = 1; i < cuts.Length - 1; i++)
            {
                float2 point = NetworkCourseMath.Point(center, parameters[i]).xz;
                float nearest = float.PositiveInfinity;
                cuts[i] = cuts[i - 1];
                for (int j = (int)cuts[i - 1]; j < side.Count - 1; j++)
                {
                    float2 start = side[j].xz;
                    float2 span = side[j + 1].xz - start;
                    float lengthSquared = math.lengthsq(span);
                    if (lengthSquared == 0f) continue;
                    float fraction = math.clamp(math.dot(point - start, span) / lengthSquared,
                        math.max(0f, cuts[i - 1] - j), 1f);
                    float distance = math.distancesq(point, start + fraction * span);
                    if (distance >= nearest) continue;
                    nearest = distance;
                    cuts[i] = j + fraction;
                }
            }
            return cuts;
        }

        private static List<float3> Side(Bezier4x3 start, Bezier4x3 end)
        {
            var points = new List<float3>();
            Sample(new RoadPath(start.a, start.b, start.c, start.d));
            Sample(new RoadPath(end.a, end.b, end.c, end.d));
            return points;

            void Sample(RoadPath curve)
            {
                int count = CityOsm.SampleCount(curve);
                for (int i = 0; i <= count; i++)
                    AddPoint(points, NetworkCourseMath.Point(curve, i / (float)count));
            }
        }

        private static List<float3> Ring(IReadOnlyList<float3> left, IReadOnlyList<float3> right,
            float leftStart, float leftEnd, float rightStart, float rightEnd)
        {
            var points = new List<float3>();
            AddPoint(points, SidePoint(left, leftStart));
            for (int i = (int)math.floor(leftStart) + 1; i < leftEnd; i++) AddPoint(points, left[i]);
            AddPoint(points, SidePoint(left, leftEnd));
            AddPoint(points, SidePoint(right, rightEnd));
            for (int i = (int)math.ceil(rightEnd) - 1; i > rightStart; i--) AddPoint(points, right[i]);
            AddPoint(points, SidePoint(right, rightStart));
            if (points.Count > 1 && SamePosition(points[0], points[points.Count - 1])) points.RemoveAt(points.Count - 1);
            if (points.Count >= 3)
            {
                float twiceArea = 0f;
                float2 origin = points[0].xz;
                for (int i = 1; i < points.Count - 1; i++)
                {
                    float2 a = points[i].xz - origin;
                    float2 b = points[i + 1].xz - origin;
                    twiceArea += a.x * b.y - a.y * b.x;
                }
                if (math.abs(twiceArea) < 0.01f) points.Clear();
            }
            return points;
        }

        private static float3 SidePoint(IReadOnlyList<float3> side, float parameter)
        {
            int index = (int)parameter;
            return math.lerp(side[index], side[math.min(index + 1, side.Count - 1)], parameter - index);
        }

        private static void AddPoint(List<float3> points, float3 point)
        {
            if (points.Count == 0 || !SamePosition(points[points.Count - 1], point)) points.Add(point);
        }

        private static bool SamePosition(float3 a, float3 b) => math.distancesq(a.xz, b.xz) < 0.000001f;

        private static long DerivedId(int index, int offset) => CityOsmFile.FirstMapFeatureId + ((long)index << 26) + offset;
    }
}
