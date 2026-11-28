using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Colossal.Mathematics;
using Game.Net;
using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmBridgeMapTests
    {
        [Fact]
        public void Bridge_outline_follows_both_native_edges_and_matches_the_way_layer()
        {
            var bridge = Road(1, MapGrade.Bridge, RoadPath.Straight(new float3(0, 8, 0), new float3(40, 12, 0)));
            var upper = Road(2, MapGrade.Bridge, RoadPath.Straight(new float3(20, 20, -20), new float3(20, 20, 20)));
            var geometry = Deck(new float3(0, 8, 8), new float3(40, 12, 8), new float3(0, 8, -2), new float3(40, 12, -2));
            var roads = new[] { bridge, upper };
            OsmDocument input = CityOsm.Export(roads, Array.Empty<CityOsmBuildingSource>());
            var before = input.Write();
            OsmDocument map = input.Copy();
            CityOsmBridgeMap.Apply(map, roads, new Dictionary<int, EdgeGeometry> { [bridge.Index] = geometry });

            Assert.Equal(before, input.Write());
            OsmWay outline = Assert.Single(map.Ways.Values, way => CityOsm.Tag(way, "man_made") == "bridge");
            Assert.Equal("1", outline.Tags["layer"]);
            Assert.Equal(outline.Tags["layer"], map.Ways[CityOsm.NativeId(bridge.Index)].Tags["layer"]);
            Assert.Equal("2", map.Ways[CityOsm.NativeId(upper.Index)].Tags["layer"]);
            Assert.Equal(outline.NodeIds[0], outline.NodeIds.Last());
            Assert.Contains(outline.NodeIds.Select(id => CityOsm.Position(map.Nodes[id])), point => math.abs(point.z - 8f) < 0.01f);
            Assert.Contains(outline.NodeIds.Select(id => CityOsm.Position(map.Nodes[id])), point => math.abs(point.z + 2f) < 0.01f);
            Assert.True(CityOsmBridgeMap.Intersects(new Bounds2(new float2(18, 5), new float2(22, 6)), geometry));
            Assert.False(CityOsmBridgeMap.Intersects(new Bounds2(new float2(18, 9), new float2(22, 10)), geometry));
            Assert.Equal(map.Write(), OsmDocument.Read(map.Write()).Write());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Ways_with_reversed_crossing_order_are_split_only_in_the_read_only_map(bool bridge)
        {
            MapGrade grade = bridge ? MapGrade.Bridge : MapGrade.Tunnel;
            var level = Road(1, grade, RoadPath.Straight(new float3(-70, 10, 0), new float3(70, 10, 0)));
            var ascending = Road(2, grade, new RoadPath(new float3(-64, 2, 10), new float3(-40, 5, -30),
                new float3(40, 17, -30), new float3(64, 20, 10)));
            var roads = new[] { level, ascending };
            var outlines = new Dictionary<int, EdgeGeometry>();
            if (grade == MapGrade.Bridge)
            {
                outlines.Add(level.Index, Deck(new float3(-70, 10, 3), new float3(70, 10, 3),
                    new float3(-70, 10, -3), new float3(70, 10, -3)));
            }
            OsmDocument input = CityOsm.Export(roads, Array.Empty<CityOsmBuildingSource>());
            OsmDocument map = input.Copy();
            OsmDocument reversed = input.Copy();
            CityOsmBridgeMap.Apply(map, roads, outlines);
            CityOsmBridgeMap.Apply(reversed, roads.Reverse().ToArray(), outlines);

            OsmWay[] levelPieces = Pieces(map, level);
            OsmWay[] ascendingPieces = Pieces(map, ascending);
            Assert.Equal(2, input.Ways.Count);
            Assert.Equal(2, levelPieces.Length);
            Assert.Equal(2, ascendingPieces.Length);
            Assert.Equal(CityOsm.NativeId(level.Index), levelPieces[0].Id);
            Assert.Equal(levelPieces[0].NodeIds.Last(), levelPieces[1].NodeIds[0]);
            Assert.Equal(ascendingPieces[0].NodeIds.Last(), ascendingPieces[1].NodeIds[0]);
            Assert.True(Layer(levelPieces[0]) > Layer(ascendingPieces[0]));
            Assert.True(Layer(levelPieces[1]) < Layer(ascendingPieces[1]));
            Assert.All(levelPieces, way => Assert.Equal("primary", way.Tags["highway"]));
            Assert.Equal(map.Write(), reversed.Write());
            if (grade == MapGrade.Bridge)
            {
                var outlinesByLayer = map.Ways.Values.Where(way => CityOsm.Tag(way, "man_made") == "bridge").ToArray();
                Assert.Equal(levelPieces.Select(Layer).ToArray(), outlinesByLayer.Select(Layer).ToArray());
                Assert.Equal(2, outlinesByLayer[0].NodeIds.Intersect(outlinesByLayer[1].NodeIds).Count());
            }
        }

        [Fact]
        public void A_ramp_can_move_between_layers_when_crossing_different_bridges()
        {
            var ramp = Road(1, MapGrade.Bridge, RoadPath.Straight(new float3(-100, 2, 0), new float3(100, 32, 0)));
            var west = Road(2, MapGrade.Bridge, RoadPath.Straight(new float3(-80, 12, -50), new float3(-80, 12, 50)));
            var east = Road(3, MapGrade.Bridge, RoadPath.Straight(new float3(80, 22, -50), new float3(80, 22, 50)));
            var exit = Road(4, MapGrade.Ground, RoadPath.Straight(ramp.Curve.D, new float3(130, 31, 0)));
            exit.Start = ramp.End;
            var roads = new[] { ramp, west, east, exit };
            OsmDocument map = CityOsm.Export(roads, Array.Empty<CityOsmBuildingSource>());
            CityOsmBridgeMap.Apply(map, roads, new Dictionary<int, EdgeGeometry>());

            OsmWay[] rampPieces = Pieces(map, ramp);
            Assert.Equal(new[] { 1, 2 }, rampPieces.Select(Layer).ToArray());
            Assert.Equal(2, Layer(Assert.Single(Pieces(map, west))));
            Assert.Equal(1, Layer(Assert.Single(Pieces(map, east))));
            Assert.Equal(rampPieces[0].NodeIds.Last(), rampPieces[1].NodeIds[0]);
            Assert.Equal(rampPieces[1].NodeIds.Last(), Assert.Single(Pieces(map, exit)).NodeIds[0]);
        }

        private static OsmWay[] Pieces(OsmDocument map, CityOsmRoadSource road) =>
            map.Ways.Values.Where(way => CityOsm.Tag(way, "airimayor:entity") == road.Entity).ToArray();

        private static int Layer(OsmWay way) => int.Parse(way.Tags["layer"], CultureInfo.InvariantCulture);

        private static CityOsmRoadSource Road(int index, MapGrade grade, RoadPath curve) => new CityOsmRoadSource
        {
            Index = index,
            Entity = $"{index}:1",
            Prefab = "Medium Road",
            Highway = "primary",
            Grade = grade,
            Curve = curve,
            Start = new CityOsmJunction { Index = index * 2, Entity = $"{index * 2}:1", Position = curve.A },
            End = new CityOsmJunction { Index = index * 2 + 1, Entity = $"{index * 2 + 1}:1", Position = curve.D }
        };

        private static EdgeGeometry Deck(float3 leftStart, float3 leftEnd, float3 rightStart, float3 rightEnd)
        {
            float3 leftMiddle = math.lerp(leftStart, leftEnd, 0.5f);
            float3 rightMiddle = math.lerp(rightStart, rightEnd, 0.5f);
            return new EdgeGeometry
            {
                m_Start = new Segment { m_Left = Straight(leftStart, leftMiddle), m_Right = Straight(rightStart, rightMiddle) },
                m_End = new Segment { m_Left = Straight(leftMiddle, leftEnd), m_Right = Straight(rightMiddle, rightEnd) }
            };
        }

        private static Bezier4x3 Straight(float3 start, float3 end)
        {
            var path = RoadPath.Straight(start, end);
            return new Bezier4x3(path.A, path.B, path.C, path.D);
        }
    }
}
