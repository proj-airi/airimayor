using Colossal.Mathematics;
using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmFileTests
    {
        [Fact]
        public void Empty_land_has_a_geographic_extent_and_a_revision_specific_to_this_city_and_rectangle()
        {
            var document = new OsmDocument();
            var bounds = new float4(-120, -40, 80, 160);
            CityOsmFile.AddBounds(document, bounds);
            string content = document.Write();
            var header = Assert.Single(OsmDocument.Read(content).Headers);
            Assert.Equal(-40d, (double)header.Attribute("minlat") * CityOsm.LatitudeMeters, 6);
            Assert.Equal(80d, (double)header.Attribute("maxlon") * CityOsm.LongitudeMeters, 6);
            var city = Guid.NewGuid();
            string revision = CityOsmFile.Revision(city, bounds, content);
            Assert.Equal(revision, CityOsmFile.Revision(city, bounds, document.Edit(content).Write()));
            Assert.NotEqual(revision, CityOsmFile.Revision(Guid.NewGuid(), bounds, content));
            Assert.NotEqual(revision, CityOsmFile.Revision(city, bounds + 1, content));
            var withStop = document.Edit("<osmChange version='0.6'><create><node id='-1' lat='0' lon='0'><tag k='highway' v='bus_stop'/></node></create></osmChange>");
            Assert.Equal("bus", CityOsmInfrastructure.PlatformMode(withStop.Nodes[-1]));
            Assert.NotEqual(revision, CityOsmFile.Revision(city, bounds, withStop.Write()));
            Assert.Empty(document.Nodes);
        }

        [Theory]
        [InlineData(0, 0, 1, 1, true)]
        [InlineData(0, 0, 0, 1, false)]
        [InlineData(0, 0, -1, 1, false)]
        [InlineData(0, 0, float.PositiveInfinity, 1, false)]
        public void Coordinate_extents_require_two_ordered_finite_corners(float west, float south, float east, float north, bool valid)
        {
            Assert.Equal(valid, CityOsmFile.ValidBounds(new float4(west, south, east, north)));
        }

        [Fact]
        public void Spatial_read_includes_crossing_curves_and_excludes_empty_corners_of_their_bounding_box()
        {
            var crossing = new Bezier4x2(new float2(-30, 0), new float2(-10, 0), new float2(10, 0), new float2(30, 0));
            var arch = new Bezier4x2(new float2(-30, 0), new float2(-30, 40), new float2(30, 40), new float2(30, 0));
            var atOrigin = new Bounds2(new float2(-1), new float2(1));
            var endJunction = new float2(35, 0);
            Bounds2 readBounds = CityOsmSpatial.NetworkReadBounds(crossing, crossing.a, endJunction);
            Assert.True(CityOsmFile.ValidBounds(new float4(readBounds.min, readBounds.max)));
            Assert.True(MathUtils.Intersect(readBounds, crossing.a));
            Assert.True(MathUtils.Intersect(readBounds, endJunction));
            Assert.True(CityOsmSpatial.Intersects(atOrigin, crossing));
            Assert.False(CityOsmSpatial.Intersects(atOrigin, arch));
            Assert.True(CityOsmSpatial.Intersects(new Bounds2(new float2(-1, 29), new float2(1, 31)), arch));
        }

        [Fact]
        public void Spatial_lookup_reaches_lot_corners_outside_the_indexed_building_geometry()
        {
            var geometry = new Bounds3(new float3(-7.8f, 0f, -3.8f), new float3(7.8f, 5f, 3.8f));
            float extra = CityOsmSpatial.LotOverhang(new float2(8f, 4f), geometry);
            Assert.InRange(extra, 0.2828f, 0.2829f);
            Assert.Equal(0f, CityOsmSpatial.LotOverhang(new float2(3), geometry));
        }

        [Theory]
        [InlineData((int)TypedNetworkKinds.Water)]
        [InlineData((int)TypedNetworkKinds.Sewage)]
        [InlineData((int)(TypedNetworkKinds.Water | TypedNetworkKinds.Sewage))]
        [InlineData((int)TypedNetworkKinds.LowVoltage)]
        public void Underground_networks_keep_their_contents_connections_and_depth(int kinds)
        {
            var utility = (TypedNetworkKinds)kinds;
            var start = new CityOsmJunction { Index = 1, Entity = "1:3", Position = new float3(-20, -10, 0) };
            var end = new CityOsmJunction { Index = 2, Entity = "2:3", Position = new float3(20, -10, 0) };
            var pipe = new CityOsmRoadSource { Index = 9, Entity = "9:3", Prefab = "Buried", Utility = utility,
                Start = start, End = end, Curve = RoadPath.Straight(start.Position, end.Position) };
            OsmDocument file = CityOsm.Export(new[] { pipe }, Array.Empty<CityOsmBuildingSource>());
            var way = Assert.Single(file.Ways.Values);
            Assert.Equal(utility, CityOsmInfrastructure.UtilityKinds(way));
            var course = Assert.Single(Assert.Single(Assert.Single(CityOsm.Import(file).Roads).Sections).Curves);
            Assert.Equal(start.Position, course.A);
            Assert.Equal(end.Position, course.D);
        }
    }
}
