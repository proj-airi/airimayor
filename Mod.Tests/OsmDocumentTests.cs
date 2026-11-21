using System;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace CS2MCP
{
    public sealed class OsmDocumentTests
    {
        private const string District = """
            <osm version="0.6" generator="district editor" upload="never">
              <bounds minlat="0" minlon="-1" maxlat="1" maxlon="1" origin="editor"/>
              <relation id="1">
                <member type="relation" ref="8" role="subarea"/>
                <member type="way" ref="1" role="outer"/>
                <member type="node" ref="1" role="label"/>
                <member type="way" ref="1" role="associatedStreet"/>
                <tag k="type" v="boundary"/>
              </relation>
              <relation id="8"><tag k="type" v="site"/></relation>
              <way id="1" version="3" changeset="184"><nd ref="1"/><nd ref="-2"/><nd ref="3"/><nd ref="1"/>
                <tag k="name:zh" v="中心 &amp; 河畔"/><tag k="building" v="school"/>
              </way>
              <node id="1" lat="0.00134" lon="-0.00021" user="mayor" version="5"/>
              <node id="-2" lat="0.002" lon="0"/>
              <node id="3" lat="0.003" lon="0.001"/>
            </osm>
            """;

        [Fact]
        public void District_preserves_independent_id_spaces_metadata_and_ordered_members()
        {
            OsmDocument document = OsmDocument.Read(OsmDocument.Read(District).Write());

            Assert.Equal("never", document.Attributes["upload"]);
            Assert.Equal("editor", (string)Assert.Single(document.Headers).Attribute("origin"));
            Assert.Equal("mayor", document.Nodes[1].Attributes["user"]);
            Assert.Equal(-0.00021, document.Nodes[1].Longitude);
            Assert.Equal("中心 & 河畔", document.Ways[1].Tags["name:zh"]);
            Assert.Equal("184", document.Ways[1].Attributes["changeset"]);
            Assert.Equal(new long[] { 1, -2, 3, 1 }, document.Ways[1].NodeIds);
            Assert.Equal(new[]
            {
                new OsmMember(OsmKind.Relation, 8, "subarea"), new OsmMember(OsmKind.Way, 1, "outer"),
                new OsmMember(OsmKind.Node, 1, "label"), new OsmMember(OsmKind.Way, 1, "associatedStreet")
            }, document.Relations[1].Members);
        }

        [Fact]
        public void Local_edit_uses_the_same_junction_for_two_ways_and_preserves_untouched_features()
        {
            OsmDocument baseline = OsmDocument.Read(District);
            const string xml = """
                <osmChange version="0.6">
                  <modify><node id="1" lat="0.005" lon="0.006" version="5"/></modify>
                  <create>
                    <way id="-4"><nd ref="1"/><nd ref="-4"/><tag k="highway" v="tertiary"/></way>
                    <node id="-4" lat="0.01" lon="0.02"/>
                  </create>
                </osmChange>
                """;
            OsmChange change = OsmChange.Read(xml);
            Assert.True(XNode.DeepEquals(XElement.Parse(xml), XElement.Parse(change.Write())));
            OsmDocument edited = baseline.Apply(change.Write());

            Assert.Equal(0.005, edited.Nodes[1].Latitude);
            Assert.Equal("mayor", edited.Nodes[1].Attributes["user"]);
            Assert.Equal("tertiary", edited.Ways[-4].Tags["highway"]);
            Assert.Equal(edited.Ways[1].NodeIds[0], edited.Ways[-4].NodeIds[0]);
            Assert.True(baseline.Relations[1].SameContent(edited.Relations[1]));
            Assert.True(baseline.Ways[1].SameContent(edited.Ways[1]));
            Assert.False(baseline.Nodes[1].SameContent(edited.Nodes[1]));
            edited.Ways[1].Tags["building"] = "office";
            Assert.Equal("school", baseline.Ways[1].Tags["building"]);
        }

        [Fact]
        public void A_saved_editor_snapshot_and_a_change_file_produce_the_same_district()
        {
            OsmDocument baseline = OsmDocument.Read(District);
            var saved = XElement.Parse(District);
            XElement node = saved.Elements("node").Single(n => (long)n.Attribute("id") == 1);
            node.SetAttributeValue("lon", 0.00045);
            node.SetAttributeValue("action", "modify");
            foreach (XElement relation in saved.Elements("relation"))
            {
                relation.SetAttributeValue("action", "delete");
            }
            OsmDocument edited = OsmDocument.Read(saved.ToString());
            OsmDocument applied = baseline.Apply("""
                <osmChange version="0.6">
                  <delete><relation id="1"/><relation id="8"/></delete>
                  <modify><node id="1" lat="0.00134" lon="0.00045" version="5"/></modify>
                </osmChange>
                """);

            Assert.Equal(edited.Write(), applied.Write());
        }

        [Fact]
        public void Conditional_deletes_keep_members_transitively_used_by_a_surviving_relation()
        {
            OsmDocument baseline = OsmDocument.Read(District);
            OsmDocument kept = baseline.Apply("""
                <osmChange version="0.6"><delete if-unused="">
                  <node id="-2"/><relation id="8"/><way id="1"/><node id="999"/>
                </delete></osmChange>
                """);
            Assert.Equal(baseline.Write(), kept.Write());
            OsmDocument removed = kept.Apply("""
                <osmChange version="0.6"><delete if-unused="true">
                  <node id="-2"/><relation id="8"/><way id="1"/><relation id="1"/>
                </delete></osmChange>
                """);
            Assert.Equal(new long[] { 1, 3 }, removed.Nodes.Keys);
            Assert.Empty(removed.Ways);
            Assert.Empty(removed.Relations);
        }

        [Fact]
        public void Ordered_file_changes_can_advance_an_object_through_several_revisions()
        {
            OsmDocument applied = OsmDocument.Read(District).Apply("""
                <osmChange>
                  <modify><node id="1" version="6" lat="0.01" lon="0.02"/></modify>
                  <modify><node id="1" version="7" lat="0.03" lon="0.04"/></modify>
                </osmChange>
                """);

            Assert.Equal("7", applied.Nodes[1].Attributes["version"]);
            Assert.Equal(0.03, applied.Nodes[1].Latitude);
            Assert.Equal("mayor", applied.Nodes[1].Attributes["user"]);
        }

        [Fact]
        public void A_snapshot_diff_preserves_shared_junctions_tag_only_edits_and_removals()
        {
            OsmDocument before = OsmDocument.Read(District);
            OsmDocument after = OsmDocument.Read(District);
            after.Nodes[1].Latitude = 0.0021;
            after.Nodes[1].Attributes["version"] = "6";
            after.Ways[1].Tags["building:levels"] = "2";
            after.Nodes.Add(-5, new OsmNode(-5, 0.004, 0.002));
            after.Ways.Add(-5, new OsmWay(-5, new long[] { -5, 1 }));
            after.Relations[1].Members.RemoveAt(0);
            after.Relations.Remove(8);

            string xml = OsmChange.Between(before, after).Write();
            XElement root = XElement.Parse(xml);
            Assert.Equal(new[] { "create", "modify", "delete" }, root.Elements().Select(block => block.Name.LocalName));
            Assert.Equal(new[] { "node", "way" }, root.Element("create").Elements().Select(item => item.Name.LocalName));
            Assert.Equal("5", (string)root.Element("modify").Element("node").Attribute("version"));
            Assert.Equal(8, (int)root.Element("delete").Element("relation").Attribute("id"));
            OsmDocument applied = before.Apply(xml);
            Assert.Equal(after.Elements.Count(), applied.Elements.Count());
            Assert.All(after.Elements, item => Assert.True(item.SameContent(applied.Find(item.Kind, item.Id))));
        }

        [Fact]
        public void Unchanged_snapshot_roundtrip_produces_an_empty_change_set()
        {
            OsmDocument before = OsmDocument.Read(District);
            OsmDocument after = OsmDocument.Read(before.Write());

            string xml = OsmChange.Between(before, after).Write();

            Assert.Empty(XElement.Parse(xml).Elements());
            Assert.Equal(before.Write(), OsmChange.Read(xml).ApplyTo(before).Write());
        }

        [Theory]
        [InlineData("<delete><node id='1'/></delete>")]
        [InlineData("<modify><node id='1' version='4' lat='0' lon='0'/></modify>")]
        [InlineData("<modify><node id='202' lat='0' lon='0'/></modify>")]
        [InlineData("<create><node id='3' lat='0' lon='0'/></create>")]
        [InlineData("<create><relation id='4'><member type='way' ref='8' role='outer'/></relation></create>")]
        public void Failed_changes_leave_the_source_document_intact(string action)
        {
            OsmDocument baseline = OsmDocument.Read(District);
            string original = baseline.Write();
            string xml = "<osmChange version='0.6'><create><node id='151' lat='0' lon='0'/></create>"
                + action + "</osmChange>";
            Assert.Throws<FormatException>(() => baseline.Apply(xml));
            Assert.Equal(original, baseline.Write());
        }

        [Theory]
        [InlineData("<osm version='0.6'><node id='2' lat='NaN' lon='0'/></osm>")]
        [InlineData("<osm version='0.6'><node id='2' lat='0' lon='181'/></osm>")]
        [InlineData("<osm version='0.6'><node id='2' lat='0' lon='1'/><node id='2' lat='1' lon='0'/></osm>")]
        [InlineData("<osm version='0.6'><node id='9223372036854775808' lat='0' lon='0'/></osm>")]
        [InlineData("<osm version='0.6'><way id='1'><nd ref='2'/><nd ref='3'/></way></osm>")]
        [InlineData("<osm version='0.6'><relation id='1'><member type='area' ref='2'/></relation></osm>")]
        [InlineData("<osm version='0.6'><bounds minlat='20' maxlat='10' minlon='0' maxlon='1'/></osm>")]
        [InlineData("<!DOCTYPE osm [<!ENTITY file SYSTEM 'file:///osm-test'>]><osm version='0.6'/>")]
        public void Invalid_snapshots_fail_before_they_can_be_applied(string xml)
        {
            Assert.Throws<FormatException>(() => OsmDocument.Read(xml));
        }
    }
}
