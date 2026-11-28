using CS2MCP;
using Unity.Mathematics;
using Xunit;

namespace Airimayor.Tests
{
    public sealed class CityOsmRoadTagTests
    {
        [Theory]
        [InlineData(RoadFactsMath.RoadClassHighway, "93ae62d6ac4e85042b4352cac42a53df", "motorway", null, null)]
        [InlineData(RoadFactsMath.RoadClassMedium, null, "secondary", null, null)]
        [InlineData(RoadFactsMath.RoadClassMinor, "7107bc1fd2e38f148b0c01aadb044101", "service", "alley", null)]
        [InlineData(RoadFactsMath.RoadClassUnknown, "13a883c5b5a5f6b4480afb70c32d14fe", "service", null, null)]
        [InlineData(RoadFactsMath.RoadClassMinor, "48c9b68071119ca49ab92cd2a32cd808", "pedestrian", null, null)]
        [InlineData(RoadFactsMath.RoadClassMinor, "b6deb8db00c63464886f17115a1354b6", "residential", null, "gravel")]
        public void Known_road_assets_export_their_class_and_qualifiers(string roadClass, string prefabId,
            string highway, string service, string surface)
        {
            var tags = CityOsmRoadTags.Classify(roadClass, prefabId);
            var start = new float3(0, 0, 0);
            var end = new float3(10, 0, 0);
            int lanes = highway == "pedestrian" ? 0 : 2;
            var road = new CityOsmRoadSource
            {
                Index = 20,
                Entity = "20:1",
                Prefab = "Road asset",
                Start = new CityOsmJunction { Index = 21, Entity = "21:1", Position = start },
                End = new CityOsmJunction { Index = 22, Entity = "22:1", Position = end },
                Curve = new RoadPath(start, math.lerp(start, end, 1f / 3f), math.lerp(start, end, 2f / 3f), end),
                Highway = tags.Highway,
                Service = tags.Service,
                Surface = tags.Surface,
                Lanes = lanes,
            };

            OsmWay mapped = Assert.Single(CityOsm.Export(new[] { road }, System.Array.Empty<CityOsmBuildingSource>()).Ways.Values);

            Assert.Equal(highway, CityOsm.Tag(mapped, "highway"));
            Assert.Equal(service, CityOsm.Tag(mapped, "service"));
            Assert.Equal(surface, CityOsm.Tag(mapped, "surface"));
            Assert.Equal(lanes == 0 ? null : "2", CityOsm.Tag(mapped, "lanes"));
        }

        [Fact]
        public void Link_junction_uses_a_distinct_continuing_motorway()
        {
            Assert.True(CityOsmRoadTags.IsLinkJunction(1, 1, new[] { (2, 3, true, false), (2, 3, false, true) }));
            Assert.False(CityOsmRoadTags.IsLinkJunction(1, 1, new[] { (1, 3, true, false), (1, 3, false, true) }));
            Assert.False(CityOsmRoadTags.IsLinkJunction(1, 1, new[] { (2, 1, true, false), (2, 1, false, true) }));
            Assert.False(CityOsmRoadTags.IsLinkJunction(1, 1, new[] { (2, 3, true, false), (3, 3, false, true) }));
            Assert.False(CityOsmRoadTags.IsLinkJunction(1, 1, new[] { (2, 3, true, false), (2, 3, true, false) }));
        }
    }
}
