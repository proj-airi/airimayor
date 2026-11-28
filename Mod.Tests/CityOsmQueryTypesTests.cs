using System;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmQueryTypesTests
    {
        [Fact]
        public void Road_classes_include_each_requested_class()
        {
            var classes = CityOsmQueryTypes.RoadClasses("[\"highway\",\"large\"]");

            Assert.True(classes.Includes(RoadFactsMath.RoadClassHighway));
            Assert.True(classes.Includes(RoadFactsMath.RoadClassLarge));
            Assert.False(classes.Includes(RoadFactsMath.RoadClassMinor));
        }

        [Fact]
        public void Building_types_match_mapped_uses()
        {
            var types = CityOsmQueryTypes.BuildingTypes("[\"office\",\"other\"]");

            Assert.True(types.Includes(CityOsmQueryTypes.BuildingType("office")));
            Assert.True(types.Includes(CityOsmQueryTypes.BuildingType("yes")));
            Assert.False(types.Includes(CityOsmQueryTypes.BuildingType("residential")));
        }

        [Fact]
        public void An_omitted_filter_includes_every_type()
        {
            Assert.True(CityOsmQueryTypes.RoadClasses(null).Includes(RoadFactsMath.RoadClassMinor));
            Assert.True(CityOsmQueryTypes.BuildingTypes(null).Includes("school"));
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("\"highway\"")]
        [InlineData("[\"highway\",2]")]
        [InlineData("[\"highway\",null]")]
        [InlineData("[\"highway\",\"rail\"]")]
        public void Invalid_road_class_lists_report_the_supported_choices(string json)
        {
            var error = Assert.Throws<FormatException>(() => CityOsmQueryTypes.RoadClasses(json));

            Assert.Contains("road_classes", error.Message);
            Assert.Contains("highway", error.Message);
        }
    }
}
