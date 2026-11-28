using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmAreaTests
    {
        private static readonly float4 District = new float4(-100, -100, 100, 100);

        [Fact]
        public void Zone_polygon_uses_the_whole_edited_ring_and_requires_a_single_unambiguous_boundary()
        {
            var document = new OsmDocument();
            var way = new OsmWay(-5, new long[] { -1, -2, -3, -4, -1 });
            way.Tags[CityOsmAreas.Zone] = "EU Low Density Housing";
            document.Ways.Add(way.Id, way);
            float2[] rectangle = { new float2(0, 0), new float2(15, 0), new float2(15, 15), new float2(0, 15) };
            for (int i = 0; i < 4; i++)
                document.Nodes.Add(-i - 1, CityOsm.ToNode(-i - 1, new float3(rectangle[i].x, 0, rectangle[i].y)));
            Assert.Equal(rectangle, CityOsmAreas.Ring(document, way, District));
            Assert.True(CityOsmAreas.Intersects(rectangle, new float4(-5, 3, 20, 5)));
            Assert.False(CityOsmAreas.Intersects(rectangle, new float4(16, 0, 20, 5)));
            Assert.Throws<FormatException>(() => CityOsmAreas.Ring(document, way, new float4(-1, -1, 14, 14)));
            way.NodeIds.Reverse(1, 2);
            Assert.Throws<FormatException>(() => CityOsmAreas.Ring(document, way, District));
        }

        [Fact]
        public void Editor_rounding_preserves_areas_and_one_district_diff_keeps_both_zone_and_road_edits()
        {
            var source = new OsmDocument();
            for (int i = 1; i <= 4; i++)
                source.Nodes.Add(i, CityOsm.ToNode(i, new float3(5.123f * i, 0, i % 2 * 4.123f)));
            var zone = new OsmWay(7, new long[] { 1, 2, 3, 1 });
            source.Ways.Add(zone.Id, zone);
            zone.Tags[CityOsmAreas.Zone] = "EU Low Density Housing";
            source.Ways.Add(8, new OsmWay(8, new long[] { 1, 4 }));
            source.Ways[8].Tags["highway"] = "residential";
            var target = source.Copy();
            foreach (OsmNode node in target.Nodes.Values) node.Latitude = Math.Round(node.Latitude, 7);
            List<CityOsmEditItem> Changes() => CityOsmEdit.Changes(CityOsm.Import(source), CityOsm.Import(target), District);
            Assert.Empty(Changes());
            target.Ways[7].Tags[CityOsmAreas.Zone] = "None";
            target.Ways[8].Tags["lit"] = "yes";
            var edits = Changes();
            Assert.Equal(7, Assert.Single(CityOsmAreas.TakeChanges(edits)).Edited.Id);
            Assert.Equal(8, Assert.Single(edits).Edited.Id);
        }

        [Fact]
        public void Operational_area_expansion_preserves_locked_edge_and_entire_previous_polygon()
        {
            var old = new List<float2> { new float2(0,0), new float2(12,0), new float2(12,12), new float2(0,12) };
            var larger = new List<float2> { old[0], old[1], new float2(22,20), new float2(-10,20) };
            CityOsmAreas.CheckExpansion(old, larger);
            var notch = larger.Take(3).Concat(new[] { new float2(8,20), new float2(8,8), new float2(4,8), new float2(4,20), larger[3] }).ToList();
            Assert.Throws<FormatException>(() => CityOsmAreas.CheckExpansion(old, notch));
            notch[4] = new float2(8, 14);
            notch[5] = new float2(4, 14);
            CityOsmAreas.CheckExpansion(old, notch);
            larger[0] += new float2(-1, 0);
            Assert.Throws<FormatException>(() => CityOsmAreas.CheckExpansion(old, larger));
            Assert.Throws<FormatException>(() => CityOsmAreas.CheckExpansion(old, old));
        }

        [Fact]
        public void Replacing_or_patching_a_district_clears_the_same_zone_and_preserves_ownership_and_ordered_route_members()
        {
            var before = new OsmDocument();
            for (int i = 1; i <= 9; i++)
                before.Nodes.Add(i, CityOsm.ToNode(i, new float3(i * 2, 0, i % 3 * 3)));
            var zone = new OsmWay(11, new long[] { 1, 2, 4, 1 });
            zone.Tags[CityOsmAreas.Zone] = "Commercial";
            before.Ways.Add(11, zone);
            var perimeter = new OsmWay(12, new long[] { 5, 6, 7, 8, 5 });
            perimeter.Tags[CityOsmAreas.Perimeter] = "storage";
            before.Ways.Add(12, perimeter);
            var facility = new OsmRelation(12);
            facility.Tags["type"] = "site";
            facility.Tags["site"] = "operational_area";
            facility.Members.Add(new OsmMember(OsmKind.Node, 9, "owner"));
            facility.Members.Add(new OsmMember(OsmKind.Way, 12, "perimeter"));
            before.Relations.Add(12, facility);
            var route = new OsmRelation(14);
            route.Tags["type"] = "route";
            route.Tags["route"] = "bus";
            route.Members.Add(new OsmMember(OsmKind.Node, 3, "platform"));
            route.Members.Add(new OsmMember(OsmKind.Node, 9, "platform"));
            before.Relations.Add(14, route);
            var target = before.Copy();
            target.Ways.Remove(11);
            foreach (long id in new long[] { 1, 2, 4 }) target.Nodes.Remove(id);
            target.Relations.Remove(14);
            foreach (string content in new[] { target.Write(), OsmChange.Between(before, target).Write() })
            {
                var after = before.Edit(content);
                Assert.True(after.Relations[12].SameContent(facility));
                var changes = CityOsmEdit.Changes(CityOsm.Import(before), CityOsm.Import(after), District);
                Assert.Equal(11, Assert.Single(CityOsmAreas.TakeChanges(changes)).Original.Id);
                Assert.Equal(route.Members, ((OsmRelation)Assert.Single(changes).Original).Members);
            }
            var detached = before.Copy();
            detached.Relations[12].Members.RemoveAt(0);
            Assert.Throws<FormatException>(() => CityOsmEdit.Changes(CityOsm.Import(before), CityOsm.Import(detached), District));
        }
    }
}
