using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CS2MCP
{
    public sealed class OsmDistrictExchangeTests
    {
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "OsmFixtures", name));

        [Theory]
        [InlineData("interchange")]
        [InlineData("interchange-unmarked")]
        public void Ramp_edits_preserve_the_expected_city_connections(string name)
        {
            OsmDocument baseline = OsmDocument.Read(Fixture(name + "-base.osm"));
            OsmDocument edited = baseline.Apply(Fixture(name + ".osc"));
            OsmDocument expected = OsmDocument.Read(Fixture(name + ".osm"));
            Assert.Equal(expected.Write(), edited.Write());

            CityOsmGeometry city = CityOsm.Import(edited);
            Assert.Equal(expected.Write(), CityOsm.Export(city).Write());
            var through = city.Roads.Where(road => road.Highway == "motorway")
                .SelectMany(road => road.Sections).ToLookup(section => section.StartNode, section => section.EndNode);
            var ramps = city.Roads.Where(road => road.Highway == "motorway_link").ToList();
            Assert.Equal(8, ramps.Count);
            Assert.All(city.Roads, road => Assert.Equal("yes", road.OneWay));
            Assert.Equal(2, city.Roads.Count(road => road.Bridge == "yes" && road.Layer == "1"));
            for (int entry = 0; entry < 4; entry++)
            {
                HashSet<long> reachable = Reachable(through, 1 + 8 * entry);
                for (int exit = 0; exit < 4; exit++)
                {
                    int terminal = 8 + 8 * exit;
                    Assert.Equal(entry == exit, reachable.Contains(terminal));
                    var turns = ramps.Where(ramp => reachable.Contains(ramp.Sections.First().StartNode)
                        && Reachable(through, ramp.Sections.Last().EndNode).Contains(terminal));
                    bool requiresTurn = Math.Abs(exit - entry) % 2 == 1;
                    Assert.Equal(requiresTurn ? 1 : 0, turns.Count());
                }
            }
        }

        [Fact]
        public void Local_revision_moves_the_shared_street_junction_and_school_only()
        {
            OsmDocument original = OsmDocument.Read(Fixture("district.osm"));
            OsmDocument edited = original.Apply(Fixture("district-edit.osc"));
            OsmDocument expected = OsmDocument.Read(Fixture("district-revised.osm"));
            Assert.Equal(expected.Write(), edited.Write());
            Assert.Equal(6, edited.Elements.Count(item => !item.SameContent(original.Find(item.Kind, item.Id))));

            CityOsmGeometry before = CityOsm.Import(original);
            CityOsmGeometry after = CityOsm.Import(edited);
            Assert.Equal(expected.Write(), CityOsm.Export(after).Write());
            var beforeCorners = before.Buildings.Single(b => b.Id == 1108).Footprint;
            var afterCorners = after.Buildings.Single(b => b.Id == 1108).Footprint;
            Assert.Equal(7, after.Buildings.Count);
            Assert.Equal(4, afterCorners.Length);
            for (int i = 0; i < afterCorners.Length; i++)
            {
                Assert.InRange(afterCorners[i].x - beforeCorners[i].x, 2.99f, 3.02f);
                Assert.Equal(beforeCorners[i].z, afterCorners[i].z);
            }
            var approaches = after.Roads.SelectMany(road => road.Sections)
                .Where(section => section.StartNode == 1003 || section.EndNode == 1003).ToList();
            Assert.Equal(3, approaches.Count);
            foreach (var section in approaches)
            {
                var previous = before.Roads.SelectMany(road => road.Sections)
                    .Single(s => s.StartNode == section.StartNode && s.EndNode == section.EndNode);
                float delta = section.StartNode == 1003
                    ? section.Curves.First().A.x - previous.Curves.First().A.x
                    : section.Curves.Last().D.x - previous.Curves.Last().D.x;
                Assert.InRange(delta, -4.01f, -3.99f);
            }
        }

        private static HashSet<long> Reachable(ILookup<long, long> edges, long start)
        {
            var visited = new HashSet<long> { start };
            var pending = new Queue<long>();
            pending.Enqueue(start);
            while (pending.Count > 0)
            {
                foreach (long next in edges[pending.Dequeue()])
                {
                    if (visited.Add(next)) pending.Enqueue(next);
                }
            }
            return visited;
        }
    }
}
