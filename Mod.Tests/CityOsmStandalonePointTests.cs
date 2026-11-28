using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmStandalonePointTests
    {
        [Theory]
        [InlineData("natural", "tree")]
        [InlineData("highway", "street_lamp")]
        [InlineData("amenity", "bench")]
        [InlineData("amenity", "waste_basket")]
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

        [Theory]
        [InlineData("d4731877cfc14ec48b59d763dce54732", "bench")]
        [InlineData("c17428ca1eb6ed24cb2934cc2d1db7eb", "bench")]
        [InlineData("26d7f7c0799717e4bbba4b9b34dce12a", "waste_basket")]
        [InlineData("00000000000000000000000000000000", null)]
        [InlineData("d4731877cfc14ec48b59d763dce5473", null)]
        [InlineData("GardenBench01", null)]
        public void Furniture_mapping_matches_native_prefab_ids(string prefabId, string value)
        {
            var kind = CityOsmStandalonePoints.NativeFurniture(prefabId);
            Assert.Equal(value, kind == OsmStandalonePoint.None ? null : CityOsmStandalonePoints.Tag(kind).Value);
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
