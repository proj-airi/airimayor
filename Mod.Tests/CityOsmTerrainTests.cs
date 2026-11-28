using System;
using System.Linq;
using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmTerrainTests
    {
        private static readonly float4 District = new float4(-100, -100, 100, 100);

        [Fact]
        public void A_closed_concave_way_carries_the_terrain_level_and_bounds()
        {
            var before = new OsmDocument();
            float2[] expected =
            {
                new float2(-5, -7), new float2(8, -7), new float2(8, 3),
                new float2(2, 1), new float2(-5, 3),
            };
            OsmDocument submitted = TerrainEdit(before, expected, "-1.25");
            OsmDocument patched = before.Edit(OsmChange.Between(before, submitted).Write());

            var level = CityOsmTerrain.Read(before, patched, District);

            Assert.True(level.HasValue);
            Assert.Equal(-10, level.Value.WayId);
            Assert.Equal(expected, level.Value.Ring);
            Assert.Equal(new float4(-5, -7, 8, 3), level.Value.Bounds);
            Assert.Equal(-1.25f, level.Value.TargetHeight);
        }

        [Fact]
        public void An_ordinary_osm_edit_has_no_terrain_intent()
        {
            var before = new OsmDocument();
            OsmDocument after = before.Edit("<osmChange version=\"0.6\"><create><node id=\"-1\" lat=\"0\" lon=\"0\">"
                + "<tag k=\"natural\" v=\"tree\"/></node></create></osmChange>");

            Assert.Null(CityOsmTerrain.Read(before, after, District));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("5,5")]
        public void Terrain_height_must_be_a_finite_number_in_city_meters(string height)
        {
            var before = new OsmDocument();
            OsmDocument after = TerrainEdit(before, Rectangle(), height);

            Assert.Throws<FormatException>(() => CityOsmTerrain.Read(before, after, District));
        }

        [Fact]
        public void Terrain_cannot_move_existing_objects_or_install_other_features_in_the_same_edit()
        {
            var before = new OsmDocument();
            before.Put(CityOsm.ToNode(2, new float3(10, 0, 20)));
            OsmDocument moved = TerrainEdit(before, Rectangle());
            OsmDocument planted = moved.Copy();
            moved.Nodes[2].Longitude += 0.001d;
            var tree = new OsmNode(-20, 0d, 0d);
            tree.Tags["natural"] = "tree";
            planted.Put(tree);

            Assert.Throws<FormatException>(() => CityOsmTerrain.Read(before, moved, District));
            Assert.Throws<FormatException>(() => CityOsmTerrain.Read(before, planted, District));
        }

        [Fact]
        public void Terrain_requires_a_new_closed_simple_polygon_inside_the_read_district()
        {
            var before = new OsmDocument();
            OsmDocument outside = TerrainEdit(before, Rectangle().Select(vertex => vertex + 200f).ToArray());
            float2[] bowTie = Rectangle();
            (bowTie[2], bowTie[3]) = (bowTie[3], bowTie[2]);
            OsmDocument crossed = TerrainEdit(before, bowTie);
            OsmDocument unclosed = TerrainEdit(before, Rectangle());
            unclosed.Ways[-10].NodeIds.RemoveAt(4);
            OsmDocument existing = TerrainEdit(before, Rectangle());
            OsmDocument levelAgain = existing.Copy();
            levelAgain.Ways[-10].Tags["airimayor:target_height"] = "18";

            Assert.Throws<FormatException>(() => CityOsmTerrain.Read(before, outside, District));
            Assert.Throws<FormatException>(() => CityOsmTerrain.Read(before, crossed, District));
            Assert.Throws<FormatException>(() => CityOsmTerrain.Read(before, unclosed, District));
            Assert.Throws<FormatException>(() => CityOsmTerrain.Read(existing, levelAgain, District));
        }

        private static float2[] Rectangle() => new[]
        {
            new float2(0, 0), new float2(10, 0), new float2(10, 8), new float2(0, 8),
        };

        private static OsmDocument TerrainEdit(OsmDocument before, float2[] ring, string height = "5.5")
        {
            OsmDocument after = before.Copy();
            long[] corners = Enumerable.Range(1, ring.Length).Select(i => -(long)i).ToArray();
            for (int i = 0; i < ring.Length; i++)
            {
                var node = new OsmNode(corners[i], ring[i].y / CityOsm.LatitudeMeters, ring[i].x / CityOsm.LongitudeMeters);
                after.Put(node);
            }
            var way = new OsmWay(-10, corners.Append(corners[0]));
            way.Tags["area"] = "yes";
            way.Tags["airimayor:terrain"] = "level";
            if (height != null) way.Tags["airimayor:target_height"] = height;
            after.Put(way);
            return after;
        }
    }
}
