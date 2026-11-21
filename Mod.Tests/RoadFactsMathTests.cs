using Xunit;

namespace CS2MCP
{
    public sealed class RoadFactsMathTests
    {
        [Fact]
        public void Highway_group_maps_to_highway()
        {
            Assert.Equal(
                RoadFactsMath.RoadClassHighway,
                RoadFactsMath.ClassifyGroup("Highways"));
        }

        [Fact]
        public void Medium_group_maps_to_medium()
        {
            Assert.Equal(
                RoadFactsMath.RoadClassMedium,
                RoadFactsMath.ClassifyGroup("Medium Roads"));
        }

        [Fact]
        public void Small_group_maps_to_minor()
        {
            Assert.Equal(
                RoadFactsMath.RoadClassMinor,
                RoadFactsMath.ClassifyGroup("Small Roads"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("Miscellaneous")]
        public void Unclassified_group_stays_unknown(string group)
        {
            Assert.Equal(
                RoadFactsMath.RoadClassUnknown,
                RoadFactsMath.ClassifyGroup(group));
        }

        [Fact]
        public void Speed_converts_baked_limit_to_display_kmh()
        {
            Assert.Equal(100.0, RoadFactsMath.ToKmh(66.66667f));
            Assert.Equal(33.0, RoadFactsMath.ToKmh(22.22222f));
        }
    }
}
