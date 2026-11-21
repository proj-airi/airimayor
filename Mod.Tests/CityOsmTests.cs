using System;
using System.Linq;
using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmTests
    {
        [Fact]
        public void Native_road_and_lot_survive_an_editor_precision_round_trip()
        {
            CityOsmRoadSource arterial = Arterial();
            CityOsmBuildingSource school = School();
            arterial.Name = "College Avenue";
            school.Name = "Rowan Primary";
            OsmDocument export = CityOsm.Export(new[] { arterial }, new[] { school });
            foreach (OsmNode node in export.Nodes.Values)
            {
                node.Latitude = Math.Round(node.Latitude, 7);
                node.Longitude = Math.Round(node.Longitude, 7);
            }
            OsmDocument reopened = OsmDocument.Read(export.Write());
            Assert.Equal(arterial.Name, reopened.Ways[arterial.Index + 1].Tags["name"]);
            Assert.Equal(school.Name, reopened.Ways[school.Index + 1].Tags["name"]);
            CityOsmGeometry city = CityOsm.Import(reopened);
            CityOsmRoad road = Assert.Single(city.Roads);
            CityOsmSection section = Assert.Single(road.Sections);
            RoadPath course = Assert.Single(section.Curves);
            Assert.Equal(arterial.Curve.A, course.A);
            Assert.Equal(arterial.Curve.B, course.B);
            Assert.Equal(arterial.Curve.C, course.C);
            Assert.Equal(arterial.Curve.D, course.D);
            Assert.Equal(arterial.Entity, road.Entity);
            Assert.Equal(arterial.Prefab, road.Prefab);
            Assert.Equal("yes", road.Bridge);
            Assert.Equal("-1", road.OneWay);
            Assert.Equal(arterial.Start.Entity, reopened.Nodes[section.StartNode].Tags["airimayor:entity"]);
            CityOsmBuilding lot = Assert.Single(city.Buildings);
            Assert.Equal(school.Entity, lot.Entity);
            Assert.Equal(school.Prefab, lot.Prefab);
            Assert.Equal(school.Position, lot.Position.Value);
            Assert.Equal(school.Rotation.value, lot.Rotation.Value.value);
            OsmWay street = reopened.Ways[road.Id];
            Assert.Equal("3", street.Tags["lanes"]);
            Assert.Equal("60", street.Tags["maxspeed"]);
            Assert.Equal("22.5", street.Tags["width"]);
            Assert.Equal("school", reopened.Ways[lot.Id].Tags["amenity"]);
            Assert.Equal("never", reopened.Attributes["upload"]);
            Assert.Equal(reopened.Write(), CityOsm.Export(city).Write());
        }

        [Fact]
        public void A_side_street_at_a_former_shape_point_splits_the_native_curve_exactly()
        {
            var arterial = Arterial();
            OsmDocument osm = CityOsm.Export(new[] { arterial }, Array.Empty<CityOsmBuildingSource>());
            OsmWay way = osm.Ways.Values.Single();
            int index = way.NodeIds.Count / 2;
            long junction = way.NodeIds[index];
            var end = new OsmNode(-1, 0.008, 0.006);
            osm.Nodes.Add(end.Id, end);
            var branch = new OsmWay(-2, new[] { junction, end.Id });
            branch.Tags["highway"] = "residential";
            osm.Ways.Add(branch.Id, branch);

            CityOsmGeometry city = CityOsm.Import(osm);
            float t = index / (float)(way.NodeIds.Count - 1);
            var sections = city.Roads.Single(road => road.Id == way.Id).Sections;
            Assert.Equal(2, sections.Count);
            Assert.Equal(junction, sections[0].EndNode);
            Assert.Equal(junction, sections[1].StartNode);
            Assert.Equal(junction, city.Roads.Single(road => road.Id == branch.Id).Sections.Single().StartNode);
            Assert.True(math.distance(NetworkCourseMath.Point(arterial.Curve, t * 0.5f),
                NetworkCourseMath.Point(Assert.Single(sections[0].Curves), 0.5f)) < 0.001f);
            Assert.True(math.distance(NetworkCourseMath.Point(arterial.Curve, (1f + t) * 0.5f),
                NetworkCourseMath.Point(Assert.Single(sections[1].Curves), 0.5f)) < 0.001f);
        }

        [Fact]
        public void Editing_a_junction_in_world_meters_updates_all_joining_roads_and_keeps_unrelated_tags()
        {
            var arterial = Arterial();
            var branch = new CityOsmRoadSource
            {
                Index = 30,
                Entity = "30:4",
                Prefab = "Small Road",
                Highway = "residential",
                Grade = MapGrade.Ground,
                Start = arterial.End,
                End = Junction(31, new float3(130, 24, 50)),
                Curve = RoadPath.Straight(arterial.End.Position, new float3(130, 24, 50)),
            };
            OsmDocument original = CityOsm.Export(new[] { arterial, branch }, Array.Empty<CityOsmBuildingSource>());
            original.Ways[arterial.Index + 1].Tags["name"] = "College Avenue";
            var relation = new OsmRelation(27);
            relation.Members.Add(new OsmMember(OsmKind.Way, branch.Index + 1, "forward"));
            original.Relations.Add(relation.Id, relation);
            CityOsmGeometry city = CityOsm.Import(original);
            long joint = arterial.End.Index + 1;
            float3 moved = city.Positions[joint] + new float3(-6, 2, 3);
            city.MoveNode(joint, moved);
            city.SetWayTag(branch.Index + 1, "highway", "tertiary");

            OsmDocument edited = CityOsm.Export(city);
            CityOsmGeometry result = CityOsm.Import(OsmDocument.Read(edited.Write()));
            Assert.Equal("College Avenue", edited.Ways[arterial.Index + 1].Tags["name"]);
            Assert.True(edited.Relations[relation.Id].SameContent(relation));
            Assert.Equal(2, edited.Elements.Count(e => !e.SameContent(original.Find(e.Kind, e.Id))));
            Assert.Equal(moved, result.Roads.Single(r => r.Id == arterial.Index + 1).Sections.Last().Curves.Last().D);
            CityOsmRoad side = result.Roads.Single(r => r.Id == branch.Index + 1);
            Assert.Equal(moved, side.Sections.First().Curves.First().A);
            Assert.Equal("tertiary", side.Highway);
        }

        [Fact]
        public void A_rotated_or_translated_lot_retains_a_pose_and_an_irregular_outline_stays_a_polygon()
        {
            var school = School();
            OsmDocument baseline = CityOsm.Export(Array.Empty<CityOsmRoadSource>(), new[] { school });
            OsmWay way = baseline.Ways.Values.Single();
            way.NodeIds.Reverse();
            CityOsmGeometry city = CityOsm.Import(baseline);
            quaternion delta = quaternion.RotateY(0.42f);
            float3 destination = school.Position + new float3(12, 3, 15);
            foreach (long node in way.NodeIds.Distinct())
            {
                city.MoveNode(node, destination + math.mul(delta, city.Positions[node] - school.Position));
            }
            CityOsmBuilding revised = CityOsm.Import(CityOsm.Export(city)).Buildings.Single();
            Assert.True(math.distance(destination, revised.Position.Value) < 0.001f);
            Assert.True(math.distance(math.mul(delta, school.Rotation).value, revised.Rotation.Value.value) < 0.001f);
            city.MoveNode(way.NodeIds[0], city.Positions[way.NodeIds[0]] + new float3(4, 0, 0));
            var irregular = CityOsm.Import(CityOsm.Export(city)).Buildings.Single();
            Assert.Null(irregular.Position);
            Assert.Null(irregular.Rotation);
            Assert.Equal(city.Positions[way.NodeIds[0]], irregular.Footprint[0]);
        }

        [Fact]
        public void Uninterpreted_elevation_text_survives_a_horizontal_world_edit()
        {
            var osm = new OsmDocument();
            var node = new OsmNode(4, 0.01, 0.02);
            node.Tags["ele"] = "unknown";
            osm.Nodes.Add(node.Id, node);
            CityOsmGeometry city = CityOsm.Import(osm);
            float3 point = city.Positions[node.Id];
            Assert.True(float.IsNaN(point.y));
            city.MoveNode(node.Id, point + new float3(5, 0, 0));
            OsmNode result = CityOsm.Export(city).Nodes[node.Id];
            Assert.True(result.Longitude > node.Longitude);
            Assert.Equal("unknown", result.Tags["ele"]);
        }

        private static CityOsmRoadSource Arterial()
        {
            var curve = new RoadPath(new float3(0, 12, 0), new float3(18, 28, 31),
                new float3(60, 32, 50), new float3(90, 20, 71));
            return new CityOsmRoadSource
            {
                Index = 10,
                Entity = "10:7",
                Prefab = "Medium Road",
                Highway = "secondary",
                OneWay = "-1",
                Lanes = 3,
                SpeedKmh = 60,
                WidthMeters = 22.5f,
                Grade = MapGrade.Bridge,
                Start = Junction(20, curve.A),
                End = Junction(21, curve.D),
                Curve = curve,
            };
        }

        private static CityOsmJunction Junction(int index, float3 position) =>
            new CityOsmJunction { Index = index, Entity = index + ":2", Position = position };

        private static CityOsmBuildingSource School() => new CityOsmBuildingSource
        {
            Index = 50,
            Entity = "50:9",
            Prefab = "Elementary School",
            Building = "school",
            Position = new float3(55, 10, 200),
            Rotation = quaternion.EulerXYZ(0.02f, 0.64f, 0f),
            LotSize = new float2(64, 96),
        };
    }
}
