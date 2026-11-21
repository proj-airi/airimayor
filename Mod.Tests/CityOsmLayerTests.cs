using System;
using System.Linq;
using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmLayerTests
    {
        [Theory]
        [InlineData(true, "1", "2", "3")]
        [InlineData(false, "-3", "-2", "-1")]
        public void Crossing_decks_follow_their_local_heights(bool bridge, string low, string middle, string high)
        {
            MapGrade grade = bridge ? MapGrade.Bridge : MapGrade.Tunnel;
            float shift = bridge ? 0f : -30f;
            var lower = Straight(1, grade, new float3(-80, 4 + shift, 0), new float3(80, 4 + shift, 0));
            var center = Straight(2, grade, new float3(0, 12 + shift, -80), new float3(0, 12 + shift, 80));
            var upper = Straight(3, grade, new float3(-80, 20 + shift, -80), new float3(80, 20 + shift, 80));
            var separate = Straight(4, grade, new float3(-20, 40 + shift, 97), new float3(20, 40 + shift, 97));
            var ground = Straight(5, MapGrade.Ground, new float3(-90, 0, -30), new float3(90, 0, -30));
            OsmDocument map = CityOsm.Export(new[] { upper, separate, lower, ground, center }, Array.Empty<CityOsmBuildingSource>());

            Assert.Equal(low, Layer(map, lower));
            Assert.Equal(middle, Layer(map, center));
            Assert.Equal(high, Layer(map, upper));
            Assert.Equal(bridge ? "1" : "-1", Layer(map, separate));
            Assert.False(map.Ways[CityOsm.NativeId(ground.Index)].Tags.ContainsKey("layer"));
        }

        [Fact]
        public void A_sloping_road_uses_its_height_at_each_crossing()
        {
            var ramp = Straight(1, MapGrade.Bridge, new float3(-100, 2, 0), new float3(100, 32, 0));
            var west = Straight(2, MapGrade.Bridge, new float3(-80, 12, -50), new float3(-80, 12, 50));
            var east = Straight(3, MapGrade.Bridge, new float3(80, 22, -50), new float3(80, 22, 50));
            OsmDocument map = CityOsm.Export(new[] { ramp, west, east }, Array.Empty<CityOsmBuildingSource>());

            Assert.Equal("3", Layer(map, west));
            Assert.Equal("2", Layer(map, ramp));
            Assert.Equal("1", Layer(map, east));
        }

        [Fact]
        public void Decks_that_swap_order_fall_back_consistently_across_input_order()
        {
            var level = Straight(1, MapGrade.Bridge, new float3(-70, 10, 0), new float3(70, 10, 0));
            var ascending = Straight(2, MapGrade.Bridge, new float3(-64, 2, 10), new float3(64, 20, 10));
            ascending.Curve = new RoadPath(ascending.Curve.A, new float3(-40, 5, -30),
                new float3(40, 17, -30), ascending.Curve.D);
            var pair = new[] { level, ascending };
            OsmDocument map = CityOsm.Export(pair, Array.Empty<CityOsmBuildingSource>());
            OsmDocument reversed = CityOsm.Export(pair.Reverse(), Array.Empty<CityOsmBuildingSource>());

            Assert.Equal("1", Layer(map, level));
            Assert.Equal("1", Layer(map, ascending));
            Assert.Equal(map.Write(), reversed.Write());
        }

        [Fact]
        public void Stacked_parallel_decks_and_joined_bridge_ends_keep_different_meanings()
        {
            var lower = Straight(1, MapGrade.Bridge, new float3(-64, 5, 0), new float3(64, 5, 0));
            var upper = Straight(2, MapGrade.Bridge, new float3(-32, 16, 0), new float3(32, 16, 0));
            var extension = Straight(3, MapGrade.Bridge, new float3(64, 7, 0), new float3(96, 9, 20));
            extension.Start = lower.End;
            OsmDocument map = CityOsm.Export(new[] { upper, lower, extension }, Array.Empty<CityOsmBuildingSource>());

            Assert.Equal("2", Layer(map, upper));
            Assert.Equal("1", Layer(map, lower));
            Assert.Equal("1", Layer(map, extension));
        }

        private static string Layer(OsmDocument map, CityOsmRoadSource road) => map.Ways[CityOsm.NativeId(road.Index)].Tags["layer"];

        private static CityOsmRoadSource Straight(int id, MapGrade grade, float3 from, float3 to)
        {
            return new CityOsmRoadSource
            {
                Index = id,
                Entity = $"{id}:1",
                Grade = grade,
                Highway = "primary",
                Prefab = "Medium Road",
                Start = new CityOsmJunction { Index = id * 2, Entity = $"{id * 2}:1", Position = from },
                End = new CityOsmJunction { Index = id * 2 + 1, Entity = $"{id * 2 + 1}:1", Position = to },
                Curve = RoadPath.Straight(from, to)
            };
        }
    }
}
