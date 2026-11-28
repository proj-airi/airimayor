using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmAdministrativeBoundaryTests
    {
        private static readonly float4 Bounds = new float4(-20, -20, 40, 40);

        [Fact]
        public void Outer_ways_form_one_boundary_and_the_relation_carries_the_edit()
        {
            var before = new OsmDocument();
            var proposal = Boundary();
            var district = before.Edit(OsmChange.Between(before, proposal).Write());
            var creation = Assert.Single(Changes(before, district));
            var relation = Assert.IsType<OsmRelation>(creation.Edited);
            Assert.True(CityOsmAdministrativeBoundaries.IsBoundary(relation));
            Assert.Equal("Northbank", relation.Tags["name"]);
            Assert.Equal(new[] { new float2(0, 0), new float2(16, 0), new float2(16, 12), new float2(0, 12) },
                CityOsmAdministrativeBoundaries.Ring(district, relation, Bounds));

            var edited = district.Copy();
            edited.Nodes[-2].Tags["ele"] = "5";
            Assert.Empty(Changes(district, edited));
            edited.Nodes[-2] = CityOsm.ToNode(-2, new float3(20, 0, 0));
            Assert.IsType<OsmRelation>(Assert.Single(Changes(district, edited)).Edited);
            Assert.Equal(new float2(20, 0), CityOsmAdministrativeBoundaries.Ring(edited, edited.Relations[-1], Bounds)[1]);
        }

        [Fact]
        public void The_entire_boundary_must_fit_the_selection()
        {
            var district = Boundary();
            Assert.Throws<FormatException>(() =>
                CityOsmAdministrativeBoundaries.Ring(district, district.Relations[-1], new float4(0, 0, 15, 15)));
        }

        [Fact]
        public void Native_readback_can_start_at_a_different_corner_and_reverse_the_ring()
        {
            var district = Boundary();
            var ring = CityOsmAdministrativeBoundaries.Ring(district, district.Relations[-1], Bounds);
            var native = new[] { ring[2], ring[1], ring[0], ring[3] };
            Assert.True(CityOsmAdministrativeBoundaries.SameRing(native, ring));
            native[2] += new float2(1, 0);
            Assert.False(CityOsmAdministrativeBoundaries.SameRing(native, ring));
        }

        private static List<CityOsmEditItem> Changes(OsmDocument before, OsmDocument after) =>
            CityOsmEdit.Changes(CityOsm.Import(before), CityOsm.Import(after), Bounds);

        private static OsmDocument Boundary()
        {
            var document = new OsmDocument();
            document.Nodes.Add(-1, CityOsm.ToNode(-1, new float3(0, 0, 0)));
            document.Nodes.Add(-2, CityOsm.ToNode(-2, new float3(16, 0, 0)));
            document.Nodes.Add(-3, CityOsm.ToNode(-3, new float3(16, 0, 12)));
            document.Nodes.Add(-4, CityOsm.ToNode(-4, new float3(0, 0, 12)));
            document.Ways.Add(-1, new OsmWay(-1, new[] { -1L, -2L, -3L }));
            document.Ways.Add(-2, new OsmWay(-2, new[] { -1L, -4L, -3L }));
            var relation = new OsmRelation(-1);
            relation.Tags["type"] = "boundary";
            relation.Tags["boundary"] = "administrative";
            relation.Tags["name"] = "Northbank";
            relation.Members.Add(new OsmMember(OsmKind.Way, -1, "outer"));
            relation.Members.Add(new OsmMember(OsmKind.Way, -2, "outer"));
            document.Relations.Add(relation.Id, relation);
            return document;
        }
    }
}
