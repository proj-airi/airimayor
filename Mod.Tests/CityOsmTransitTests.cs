using System;
using System.Linq;
using CS2MCP;
using Game.Prefabs;
using Unity.Mathematics;
using Xunit;

namespace airimayor.Tests
{
    public class CityOsmTransitTests
    {
        private const string District = """
            <osm version="0.6">
              <node id="2" lat="0" lon="0"><tag k="public_transport" v="platform"/><tag k="bus" v="yes"/></node>
              <node id="3" lat="0.001" lon="0"><tag k="public_transport" v="platform"/><tag k="bus" v="yes"/><tag k="airimayor:station" v="10"/></node>
              <node id="4" lat="0.001" lon="0.002"><tag k="public_transport" v="platform"/><tag k="bus" v="yes"/></node>
              <relation id="8">
                <member type="node" ref="2" role="platform"/>
                <member type="node" ref="3" role="platform"/>
                <member type="node" ref="4" role="platform"/>
                <tag k="type" v="route"/><tag k="route" v="bus"/>
              </relation>
            </osm>
            """;

        private static readonly float4 Bounds = new float4(-1, -1, 300, 300);

        [Fact]
        public void Station_and_curb_platforms_keep_the_edited_boarding_order()
        {
            var before = OsmDocument.Read(District);
            var after = OsmDocument.Read(District);
            var route = after.Relations[8];
            route.Members.Reverse();
            Assert.True(CityOsmTransit.StationOwned(after.Nodes[3]));
            Assert.False(CityOsmTransit.StationOwned(after.Nodes[4]));
            Assert.Equal(new long[] { 4, 3, 2 }, CityOsmTransit.Stops(route, before, after, Bounds));
        }

        [Fact]
        public void A_partial_route_names_the_bounds_needed_for_an_edit()
        {
            var route = OsmDocument.Read(District).Relations[8];
            var extent = new float4(0.1f, -2.1f, 9.6f, 20);
            Assert.Equal(new float4(-1, -4, 11, 21), CityOsmTransit.ReadBounds(extent));
            string bounds = CityOsmTransit.Extent(extent);
            route.Tags[CityOsmTransit.RequiredBounds] = bounds;
            Assert.Equal("-1,-4,11,21", bounds);
            Assert.Contains(bounds, Assert.Throws<FormatException>(() => CityOsmTransit.CheckScope(route)).Message);
        }

        [Theory]
        [InlineData("bus", TransportType.Bus)]
        [InlineData("Train", TransportType.Train)]
        [InlineData("ship", TransportType.Ship)]
        [InlineData("taxi", TransportType.Taxi)]
        [InlineData("post", TransportType.Post)]
        public void Transit_query_accepts_named_native_modes(string value, TransportType mode)
        {
            Assert.True(CityOsmTransit.TryReadTransportType(value, out TransportType actual));
            Assert.Equal(mode, actual);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("none")]
        [InlineData("count")]
        [InlineData("road")]
        [InlineData("bus,train")]
        public void Transit_query_rejects_invalid_mode_filters(string value)
        {
            Assert.False(CityOsmTransit.TryReadTransportType(value, out _));
        }

        [Fact]
        public void Every_stop_must_already_exist_inside_the_edited_district()
        {
            var before = OsmDocument.Read(District);
            var after = OsmDocument.Read(District);
            var route = after.Relations[8];
            Assert.Throws<FormatException>(() => CityOsmTransit.Stops(route, before, after, new float4(-1, -1, 12, 120)));
            before.Nodes.Remove(4);
            Assert.Throws<FormatException>(() => CityOsmTransit.Stops(route, before, after, Bounds));
        }

        [Fact]
        public void A_station_shell_and_a_second_visit_cannot_stand_in_for_a_platform()
        {
            var document = OsmDocument.Read(District);
            var route = document.Relations[8];
            var first = route.Members.First();
            route.Members[1] = first;
            Assert.Throws<FormatException>(() => CityOsmTransit.Stops(route, document, document, Bounds));
            route.Members[1] = new OsmMember(OsmKind.Way, 10, "platform");
            Assert.Throws<FormatException>(() => CityOsmTransit.Stops(route, document, document, Bounds));
        }

        [Fact]
        public void Withdrawing_a_route_owns_its_derived_navigation_points()
        {
            var before = OsmDocument.Read(District);
            var via = new OsmNode(7, 0, 0.001);
            via.Tags["airimayor:waypoint"] = "yes";
            before.Nodes.Add(7, via);
            before.Relations[8].Members.Insert(1, new OsmMember(OsmKind.Node, 7, "via"));
            var after = OsmDocument.Read(before.Write());
            after.Relations.Remove(8);
            after.Nodes.Remove(7);
            var change = Assert.Single(CityOsmEdit.Changes(CityOsm.Import(before), CityOsm.Import(after), Bounds));
            Assert.Equal(8, change.Original.Id);
            Assert.Null(change.Edited);
            Assert.True(after.Nodes.ContainsKey(3));
        }
    }
}
