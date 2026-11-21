using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmEditTests
    {
        private static readonly float4 Bounds = new float4(-1000, -1000, 1000, 1000);

        [Fact]
        public void Saving_a_district_at_standard_coordinate_precision_schedules_zero_city_operations()
        {
            var original = new OsmDocument();
            AddNode(original, 1, 45.125f, 80.333f);
            AddNode(original, 2, 71.25f, 90.164f);
            AddRoad(original, 10, 1, 2);
            original.Nodes[1].Tags["ele"] = "23.0";
            var saved = original.Copy();
            foreach (OsmNode node in saved.Nodes.Values)
            {
                node.Latitude = Math.Round(node.Latitude, 7);
                node.Longitude = Math.Round(node.Longitude, 7);
            }
            saved.Nodes[1].Tags["ele"] = "23";
            Assert.Empty(CityOsmEdit.Changes(CityOsm.Import(original), CityOsm.Import(saved), Bounds));
        }

        [Fact]
        public void Attaching_a_street_to_an_existing_shape_node_edits_both_ways()
        {
            var original = new OsmDocument();
            AddNode(original, 1, 0, 0);
            AddNode(original, 2, 100, 0);
            AddNode(original, 3, 200, 0);
            AddRoad(original, 10, 1, 2, 3);
            var edited = original.Copy();
            AddNode(edited, 4, 100, 100);
            AddRoad(edited, 11, 2, 4);

            CityOsmGeometry target = CityOsm.Import(edited);
            var changes = CityOsmEdit.Changes(CityOsm.Import(original), target, Bounds);
            Assert.Equal(new long[] { 10, 11 }, changes.Select(item => item.Edited.Id));
            Assert.Equal(2, target.Roads.Single(r => r.Id == 10).Sections.Count);
            Assert.Equal(3, target.Roads.SelectMany(r => r.Sections).Count(s => s.StartNode == 2 || s.EndNode == 2));
        }

        [Fact]
        public void Adding_a_street_at_an_existing_junction_preserves_the_boundary_road()
        {
            var original = new OsmDocument();
            AddNode(original, 1, -800, 20);
            AddNode(original, 2, 50, 50);
            AddRoad(original, 10, 1, 2);
            var edited = original.Copy();
            AddNode(edited, 3, 80, 70);
            AddRoad(edited, 11, 2, 3);
            float4 extent = new float4(0, 0, 100, 100);
            var changes = CityOsmEdit.Changes(CityOsm.Import(original), CityOsm.Import(edited), extent);
            Assert.Equal(11L, Assert.Single(changes).Edited.Id);

            edited.Nodes[2].Longitude += 0.00001;
            Assert.Throws<FormatException>(() => CityOsmEdit.Changes(CityOsm.Import(original), CityOsm.Import(edited), extent));
        }

        [Fact]
        public void Fitted_courses_reduce_a_sampled_bend_and_preserve_the_terminal_junctions()
        {
            var original = new RoadPath(new float3(0, 0, 0), new float3(30, 0, 0),
                new float3(40, 0, 12), new float3(64, 0, 24));
            var nodes = new List<long>();
            var section = new CityOsmSection { StartNode = 1, EndNode = 17 };
            float3 previous = original.A;
            for (int i = 0; i <= 16; i++)
            {
                float3 point = NetworkCourseMath.Point(original, i / 16f);
                nodes.Add(i + 1);
                if (i > 0) section.Curves.Add(RoadPath.Straight(previous, point));
                previous = point;
            }

            var pieces = CityOsmEdit.Courses(nodes, 0, 16, section).ToList();
            Assert.InRange(pieces.Count, 1, 8);
            Assert.Equal(1, pieces.First().Start);
            Assert.Equal(17, pieces.Last().End);
            for (int i = 0; i < pieces.Count; i++)
            {
                Assert.Equal(section.Curves[(int)pieces[i].Start - 1].A, pieces[i].Curve.A);
                if (i > 0) Assert.Equal(pieces[i - 1].End, pieces[i].Start);
            }
            Assert.Contains(pieces, piece => math.lengthsq(math.cross(piece.Curve.B - piece.Curve.A,
                piece.Curve.D - piece.Curve.A)) > 0.001f);
        }

        [Fact]
        public void A_new_road_cannot_treat_an_unchanged_curb_stop_as_a_road_junction()
        {
            var original = new OsmDocument();
            AddNode(original, 1, 0, 0);
            original.Nodes[1].Tags["public_transport"] = "platform";
            var edited = original.Copy();
            AddNode(edited, 2, 50, 0);
            AddRoad(edited, 10, 1, 2);
            Assert.Throws<FormatException>(() => CityOsmEdit.Changes(CityOsm.Import(original), CityOsm.Import(edited), Bounds));
        }

        [Fact]
        public void A_rectangular_building_lot_supplies_orientation_and_rejects_a_distorted_asset()
        {
            var building = new CityOsmBuilding
            {
                Id = 4,
                Footprint = CityOsm.Corners(new float3(10, 23, 66), quaternion.RotateY(0.6f), new float2(32, 48))
            };
            var pose = CityOsmEdit.LotPose(building);
            Assert.True(math.distance(pose.Size, new float2(32, 48)) < 0.001f);
            Assert.True(math.distance(pose.Position, new float3(10, 23, 66)) < 0.001f);
            Assert.True(math.distance(pose.Rotation.value, quaternion.RotateY(0.6f).value) < 0.001f);
            building.Footprint[2].x += 2f;
            Assert.Throws<FormatException>(() => CityOsmEdit.LotPose(building));
        }

        [Fact]
        public void Native_node_identity_is_read_only()
        {
            var original = new OsmDocument();
            AddNode(original, 1, 0, 0);
            AddNode(original, 2, 50, 0);
            AddRoad(original, 10, 1, 2);
            var edited = original.Copy();
            edited.Nodes[2].Tags["airimayor:entity"] = "2:9";
            Assert.Throws<FormatException>(() => CityOsmEdit.Changes(CityOsm.Import(original), CityOsm.Import(edited), Bounds));
        }

        [Fact]
        public void A_bridge_and_its_approach_share_the_resolved_deck_and_keep_explicit_elevation()
        {
            var osm = new OsmDocument();
            AddNode(osm, 1, 0, 0);
            AddNode(osm, 2, 100, 0);
            AddNode(osm, 3, 200, 0);
            osm.Nodes[3].Tags["ele"] = "57";
            AddRoad(osm, 10, 1, 2);
            AddRoad(osm, 11, 2, 3);
            osm.Ways[11].Tags["bridge"] = "yes";
            osm.Ways[11].Tags["layer"] = "2";
            var city = CityOsm.Import(osm);
            var heights = new Dictionary<long, float>();
            CityOsmEdit.SupplyDeckHeights(osm.Ways[11], city, heights, 2, 4, 10, new float2(-30, 60), _ => 19f);
            foreach (var pair in heights)
            {
                float3 point = city.Positions[pair.Key];
                point.y = pair.Value;
                city.MoveNode(pair.Key, point);
            }
            Assert.Equal(39f, city.Roads.Single(r => r.Id == 10).Sections[0].Curves[0].D.y);
            RoadPath deck = city.Roads.Single(r => r.Id == 11).Sections[0].Curves[0];
            Assert.Equal(39f, deck.A.y);
            Assert.Equal(57f, deck.D.y);
        }

        private static void AddNode(OsmDocument document, long id, float x, float z)
        {
            document.Nodes.Add(id, new OsmNode(id, z / 110574.27582159436, x / 111319.49079327358));
        }

        private static void AddRoad(OsmDocument document, long id, params long[] nodes)
        {
            var road = new OsmWay(id, nodes);
            road.Tags["highway"] = "residential";
            document.Ways.Add(id, road);
        }
    }
}
