using CS2MCP;
using SkiaSharp;
using Unity.Mathematics;
using Xunit;

namespace Airimayor.Tests
{
    public sealed class CityOsmRasterTests
    {
        [Fact]
        public void A_bridge_covers_a_wider_ground_road_and_keeps_its_standard_casing()
        {
            OsmDocument map = OsmDocument.Read(
                "<osm version='0.6'><node id='1' lon='-0.0015' lat='0'/><node id='2' lon='0.0015' lat='0'/>" +
                "<node id='3' lon='0' lat='-0.0015'/><node id='4' lon='0' lat='0.0015'/>" +
                "<way id='1'><nd ref='1'/><nd ref='2'/><tag k='highway' v='motorway'/></way>" +
                "<way id='2'><nd ref='3'/><nd ref='4'/><tag k='highway' v='primary'/><tag k='layer' v='0'/><tag k='bridge' v='yes'/></way></osm>");
            using var image = SKBitmap.Decode(CityOsmRaster.Render(map, new float4(-200f, -200f, 200f, 200f), null));
            int x = image.Width / 2, y = image.Height / 2;

            Assert.Equal(SKColor.Parse("#fcd6a4"), image.GetPixel(x, y));
            Assert.Equal(SKColor.Parse("#e892a2"), image.GetPixel(x + 30, y));
            Assert.Contains(Enumerable.Range(x + 8, 7), column => image.GetPixel(column, y).Red < 50);
        }

        [Fact]
        public void A_water_multipolygon_preserves_its_land_hole_when_member_ways_need_stitching()
        {
            var map = new OsmDocument();
            AddNode(map, 1, -150, -150);
            AddNode(map, 2, 150, -150);
            AddNode(map, 3, 150, 150);
            AddNode(map, 4, -150, 150);
            AddNode(map, 5, -45, -45);
            AddNode(map, 6, 45, -45);
            AddNode(map, 7, 45, 45);
            AddNode(map, 8, -45, 45);
            map.Ways.Add(1, new OsmWay(1, new long[] { 1, 2, 3 }));
            map.Ways.Add(2, new OsmWay(2, new long[] { 1, 4, 3 }));
            map.Ways.Add(3, new OsmWay(3, new long[] { 5, 6, 7, 8, 5 }));
            var lake = new OsmRelation(1);
            lake.Tags["type"] = "multipolygon";
            lake.Tags["natural"] = "water";
            lake.Members.Add(new OsmMember(OsmKind.Way, 1, "outer"));
            lake.Members.Add(new OsmMember(OsmKind.Way, 2, "outer"));
            lake.Members.Add(new OsmMember(OsmKind.Way, 3, "inner"));
            map.Relations.Add(lake.Id, lake);

            using var image = SKBitmap.Decode(CityOsmRaster.Render(map, new float4(-200f, -200f, 200f, 200f), null));

            Assert.Equal(SKColor.Parse("#f2efe9"), image.GetPixel(image.Width / 2, image.Height / 2));
            Assert.Equal(SKColor.Parse("#aad3df"), image.GetPixel(image.Width * 3 / 4, image.Height / 2));
        }

        [Fact]
        public void Close_maps_draw_buildings_and_city_overviews_use_the_same_map_without_buildings()
        {
            var map = new OsmDocument();
            AddNode(map, 1, -100, -100);
            AddNode(map, 2, 100, -100);
            AddNode(map, 3, 100, 100);
            AddNode(map, 4, -100, 100);
            var building = new OsmWay(1, new long[] { 1, 2, 3, 4, 1 });
            building.Tags["building"] = "yes";
            map.Ways.Add(building.Id, building);

            using var close = SKBitmap.Decode(CityOsmRaster.Render(map, new float4(-200f, -200f, 200f, 200f), null));
            using var overview = SKBitmap.Decode(CityOsmRaster.Render(map, new float4(-8000f, -8000f, 8000f, 8000f), null));

            Assert.Equal(SKColor.Parse("#d9d0c9"), close.GetPixel(close.Width / 2, close.Height / 2));
            Assert.Equal(SKColor.Parse("#f2efe9"), overview.GetPixel(overview.Width / 2, overview.Height / 2));
        }

        [Fact]
        public void A_narrow_read_never_draws_a_road_beyond_the_requested_edges()
        {
            var map = new OsmDocument();
            AddNode(map, 1, 0, -80);
            AddNode(map, 2, 0, 80);
            var road = new OsmWay(1, new long[] { 1, 2 });
            road.Tags["highway"] = "motorway";
            map.Ways.Add(road.Id, road);

            using var image = SKBitmap.Decode(CityOsmRaster.Render(map, new float4(-400f, -5f, 400f, 5f), null));
            int x = image.Width / 2, y = image.Height / 2;

            Assert.Equal(SKColor.Parse("#e892a2"), image.GetPixel(x, y));
            Assert.Equal(SKColor.Parse("#f2efe9"), image.GetPixel(x, y - 20));
            Assert.Equal(SKColor.Parse("#f2efe9"), image.GetPixel(x, y + 20));
        }

        private static void AddNode(OsmDocument map, long id, double x, double z) =>
            map.Nodes.Add(id, new OsmNode(id, z / CityOsm.LatitudeMeters, x / CityOsm.LongitudeMeters));
    }
}
