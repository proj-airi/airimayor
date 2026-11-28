using CS2MCP;
using System;
using Xunit;

namespace airimayor.Tests
{
    public sealed class CityOsmMapFileTests
    {
        [Fact]
        public void Marked_map_remains_read_only_after_round_trip()
        {
            var map = new OsmDocument();
            CityOsmFile.MarkMap(map);

            var reopened = OsmDocument.Read(map.Write());

            Assert.Throws<FormatException>(() => CityOsmFile.RequireEditable(reopened));
        }

        [Fact]
        public void Generated_map_features_remain_read_only_when_copied_to_a_district()
        {
            var district = new OsmDocument();
            district.Nodes.Add(-1, new OsmNode(-1, 0, 0));
            CityOsmFile.RequireEditable(district);

            long mapId = CityOsmFile.FirstMapFeatureId;
            district.Nodes.Add(mapId, new OsmNode(mapId, 0, 0));

            Assert.Throws<FormatException>(() => CityOsmFile.RequireEditable(district));
        }
    }
}
