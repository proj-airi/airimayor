using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmStandalonePointTests
    {
        [Theory]
        [InlineData("natural", "tree")]
        [InlineData("highway", "street_lamp")]
        public void Standalone_points_survive_xml_and_the_district_diff(string tag, string value)
        {
            var before = new OsmDocument();
            var proposed = before.Copy();
            var point = CityOsm.ToNode(-1, new float3(10, 2, 14));
            point.Tags[tag] = value;
            proposed.Nodes.Add(point.Id, point);
            var after = before.Edit(OsmChange.Between(before, proposed).Write());
            var bounds = new float4(0, 0, 30, 30);
            var creation = Assert.Single(CityOsmEdit.Changes(CityOsm.Import(before), CityOsm.Import(after), bounds));
            var mapped = Assert.IsType<OsmNode>(creation.Edited);
            Assert.True(CityOsmStandalonePoints.IsPoint(mapped));
            var readback = CityOsm.ToNode(mapped.Id, CityOsm.Position(mapped));
            CityOsmStandalonePoints.WriteTags(readback, CityOsmStandalonePoints.Kind(mapped));
            Assert.Equal(value, readback.Tags[tag]);
            Assert.Equal(new float3(10, 2, 14), CityOsm.Position(readback));
        }

        [Fact]
        public void Point_objects_have_their_own_nodes()
        {
            var before = new OsmDocument();
            var proposed = before.Copy();
            var tree = CityOsm.ToNode(-1, new float3(10, 0, 10));
            tree.Tags["natural"] = "tree";
            proposed.Nodes.Add(tree.Id, tree);
            proposed.Nodes.Add(-2, CityOsm.ToNode(-2, new float3(20, 0, 10)));
            var way = new OsmWay(-1, new[] { -1L, -2L });
            way.Tags["highway"] = "footway";
            proposed.Ways.Add(way.Id, way);
            Assert.Throws<FormatException>(() => CityOsmEdit.Changes(CityOsm.Import(before), CityOsm.Import(proposed), new float4(0, 0, 30, 30)));
        }
    }
}
