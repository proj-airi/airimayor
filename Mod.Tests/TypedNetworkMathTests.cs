using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class TypedNetworkMathTests
    {
        [Fact]
        public void Road_classification_does_not_absorb_carried_utilities()
        {
            Assert.Equal(
                TypedNetworkKinds.Road,
                TypedNetworkMath.Classify(true, true, true, true));
            Assert.Equal(
                TypedNetworkKinds.Water | TypedNetworkKinds.Sewage,
                TypedNetworkMath.Classify(false, true, true, false));
        }

        [Fact]
        public void Isolated_pipe_does_not_share_a_node_with_a_road()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(20f, 0f), outsideEnd: true),
                Pipe(2, 20, 21, Xz(100f, 100f), Xz(110f, 100f)),
            };

            var finding = Assert.Single(TypedNetworkMath.FindUtilityIsolatedFindings(edges, TypedNetworkKinds.Water));
            Assert.Equal(1, finding.EdgeA);
        }

        [Fact]
        public void Pipe_sharing_a_road_node_is_not_isolated()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(20f, 0f), outsideEnd: true),
                Pipe(2, 11, 21, Xz(20f, 0f), Xz(20f, 15f)),
            };

            Assert.Empty(TypedNetworkMath.FindUtilityIsolatedFindings(edges, TypedNetworkKinds.Water));
        }

        [Fact]
        public void A_larger_road_component_without_an_outside_connection_remains_isolated()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(20f, 0f)),
                Road(2, 11, 12, Xz(20f, 0f), Xz(40f, 0f), outsideEnd: true),
                Road(3, 20, 21, Xz(0f, 100f), Xz(20f, 100f)),
                Road(4, 21, 22, Xz(20f, 100f), Xz(40f, 100f)),
                Road(5, 22, 23, Xz(40f, 100f), Xz(60f, 100f)),
            };

            List<NetworkTopologyFinding> findings = TypedNetworkMath.FindRoadIssues(edges);

            NetworkTopologyFinding isolated = Assert.Single(findings, f => f.Class == NetworkTopologyClass.IsolatedRoad);
            Assert.Equal(3, isolated.ComponentSize);
            Assert.Equal(2, isolated.EdgeA);
        }

        [Fact]
        public void Road_with_outside_connection_is_not_isolated()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(20f, 0f), outsideEnd: true),
            };

            List<NetworkTopologyFinding> findings = TypedNetworkMath.FindRoadIssues(edges);

            Assert.DoesNotContain(findings, f => f.Class == NetworkTopologyClass.IsolatedRoad);
        }

        [Fact]
        public void Near_miss_is_a_finding_and_a_far_dead_end_is_not()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(20f, 0f), outsideStart: true),
                Road(2, 20, 21, Xz(24f, 4f), Xz(40f, 4f)),
                Road(3, 30, 31, Xz(0f, 80f), Xz(20f, 80f)),
            };

            List<NetworkTopologyFinding> findings = TypedNetworkMath.FindRoadIssues(edges);

            Assert.Contains(findings, f => f.Class == NetworkTopologyClass.NearMiss);
            List<NetworkDeadEnd> deadEnds = TypedNetworkMath.FindRoadDeadEnds(edges);
            Assert.True(deadEnds.Count >= 3);
            Assert.DoesNotContain(
                findings,
                f => f.Class == NetworkTopologyClass.NearMiss && (f.EdgeA == 2 || f.EdgeB == 2));
        }

        [Fact]
        public void Unnoded_crossing_is_reported_and_a_shared_node_is_not()
        {
            TypedNetworkEdge[] crossing =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(20f, 20f), outsideStart: true),
                Road(2, 20, 21, Xz(0f, 20f), Xz(20f, 0f), outsideStart: true),
            };
            TypedNetworkEdge[] noded =
            {
                Road(1, 10, 12, Xz(0f, 0f), Xz(10f, 10f), outsideStart: true),
                Road(2, 12, 21, Xz(10f, 10f), Xz(20f, 0f), outsideEnd: true),
                Road(3, 20, 12, Xz(0f, 20f), Xz(10f, 10f)),
            };

            Assert.Contains(
                TypedNetworkMath.FindRoadIssues(crossing),
                f => f.Class == NetworkTopologyClass.UnnodedCrossing);
            Assert.DoesNotContain(
                TypedNetworkMath.FindRoadIssues(noded),
                f => f.Class == NetworkTopologyClass.UnnodedCrossing);
        }

        [Fact]
        public void Too_close_junctions_under_thirty_two_meters_are_reported()
        {
            TypedNetworkEdge[] edges = TwoJunctions(Xz(31f, 0f), Xz(51f, 0f));

            Assert.Contains(
                TypedNetworkMath.FindRoadIssues(edges),
                f => f.Class == NetworkTopologyClass.TooCloseJunctions
                    && f.DistanceM < TypedNetworkMath.CloseJunctionMeters);
        }

        [Fact]
        public void Too_close_junctions_at_thirty_two_meters_are_not_reported()
        {
            TypedNetworkEdge[] edges = TwoJunctions(Xz(0f, 0f), Xz(32f, 0f));

            Assert.DoesNotContain(
                TypedNetworkMath.FindRoadIssues(edges),
                f => f.Class == NetworkTopologyClass.TooCloseJunctions);
        }

        [Fact]
        public void Too_close_junctions_name_an_edge_at_each_junction_and_their_midpoint()
        {
            TypedNetworkEdge[] edges = TwoJunctions(Xz(31f, 0f), Xz(51f, 0f));

            NetworkTopologyFinding finding = Assert.Single(
                TypedNetworkMath.FindRoadIssues(edges),
                f => f.Class == NetworkTopologyClass.TooCloseJunctions);

            Assert.Equal(0, finding.EdgeA);
            Assert.Equal(3, finding.EdgeB);
            Assert.Equal(41f, finding.Anchor.x, 3);
            Assert.Equal(0f, finding.Anchor.y, 3);
        }

        [Fact]
        public void District_filters_use_the_junction_midpoint_and_the_actual_crossing_location()
        {
            TypedNetworkEdge[] junctions = TwoJunctions(Xz(1000f, 1000f), Xz(1018f, 1000f));
            var bounds = new Bounds2(new float2(1008f, 999f), new float2(1010f, 1001f));
            Assert.Contains(TypedNetworkMath.FindRoadIssues(junctions, bounds), f => f.Class == NetworkTopologyClass.TooCloseJunctions);
            Assert.Empty(TypedNetworkMath.FindRoadIssues(junctions, new Bounds2(new float2(-10f), new float2(10f))));

            var roads = new[] {
                new TypedNetworkEdge(10, 20, 21, new[] { Xz(-100f, 15f), Xz(0f, 15f), Xz(100f, 15f) }, TypedNetworkKinds.Road, true, false),
                Road(11, 22, 23, Xz(0f, 0f), Xz(0f, 80f), outsideEnd: true),
            };
            var crossing = Assert.Single(TypedNetworkMath.FindRoadIssues(roads, new Bounds2(new float2(-1f, 14f), new float2(1f, 16f))));
            Assert.Equal(NetworkTopologyClass.UnnodedCrossing, crossing.Class);
            Assert.Equal(new float2(0f, 15f), crossing.Anchor);
        }

        [Fact]
        public void Bounded_utility_findings_keep_citywide_connections_and_pick_a_local_component_member()
        {
            var district = new Bounds2(new float2(-5f), new float2(5f));
            var pipes = new[] {
                Road(1, 10, 11, Xz(-120f, 0f), Xz(-100f, 0f), outsideStart: true),
                Pipe(2, 11, 12, Xz(-100f, 0f), Xz(10f, 0f)),
                Pipe(3, 20, 21, Xz(-100f, 3f), Xz(-30f, 3f)),
                Pipe(4, 21, 22, Xz(-30f, 3f), Xz(30f, 3f)),
            };
            var isolated = Assert.Single(TypedNetworkMath.FindUtilityIsolatedFindings(pipes, TypedNetworkKinds.Water, district));
            Assert.Equal(2, isolated.ComponentSize);
            Assert.Equal(3, isolated.EdgeA);
            Assert.True(MathUtils.Intersect(district, isolated.Anchor));
        }

        [Fact]
        public void Near_miss_includes_gaps_between_sixteen_and_thirty_two_meters()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(40f, 0f), outsideStart: true),
                Road(2, 20, 21, Xz(20f, 24f), Xz(20f, 44f)),
            };

            List<NetworkTopologyFinding> findings = TypedNetworkMath.FindRoadIssues(edges);

            Assert.Contains(
                findings,
                f => f.Class == NetworkTopologyClass.NearMiss && f.DistanceM > 16f);
            Assert.Contains(
                findings,
                f => f.Class == NetworkTopologyClass.NearMiss
                    && math.distancesq(f.Anchor, new float2(20f, 24f)) < 0.01f);
        }

        [Fact]
        public void Short_dead_end_is_not_a_topology_finding()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(40f, 0f), outsideStart: true),
                Road(2, 11, 12, Xz(40f, 0f), Xz(44f, 0f)),
                Road(3, 10, 13, Xz(0f, 0f), Xz(0f, 30f)),
            };

            Assert.Empty(TypedNetworkMath.FindRoadIssues(edges));
        }

        [Fact]
        public void Isolated_pipe_findings_are_one_per_component()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(20f, 0f), outsideEnd: true),
                Pipe(2, 20, 21, Xz(100f, 100f), Xz(110f, 100f)),
                Pipe(3, 21, 22, Xz(110f, 100f), Xz(120f, 100f)),
                Cable(4, 30, 31, Xz(200f, 200f), Xz(210f, 200f)),
            };

            List<NetworkTopologyFinding> water =
                TypedNetworkMath.FindUtilityIsolatedFindings(edges, TypedNetworkKinds.Water);
            List<NetworkTopologyFinding> cables =
                TypedNetworkMath.FindUtilityIsolatedFindings(edges, TypedNetworkKinds.LowVoltage);

            Assert.Contains(
                water,
                f => f.Class == NetworkTopologyClass.IsolatedWater && f.ComponentSize == 2);
            Assert.Contains(
                cables,
                f => f.Class == NetworkTopologyClass.IsolatedLowVoltage && f.ComponentSize == 1);
            Assert.Empty(
                TypedNetworkMath.FindUtilityIsolatedFindings(edges, TypedNetworkKinds.Road));
        }

        [Fact]
        public void Pipe_sharing_a_road_node_is_not_an_isolated_finding()
        {
            TypedNetworkEdge[] edges =
            {
                Road(1, 10, 11, Xz(0f, 0f), Xz(20f, 0f), outsideEnd: true),
                Pipe(2, 11, 21, Xz(20f, 0f), Xz(20f, 15f)),
            };

            Assert.Empty(
                TypedNetworkMath.FindUtilityIsolatedFindings(edges, TypedNetworkKinds.Water));
        }

        [Fact]
        public void Kind_filter_parsing_requires_a_single_kind()
        {
            Assert.False(TypedNetworkMath.TryParseKind(null, out _, out string missing));
            Assert.Contains("required", missing);
            Assert.False(TypedNetworkMath.TryParseKind("", out _, out string empty));
            Assert.Contains("required", empty);
            Assert.True(TypedNetworkMath.TryParseKind("road", out TypedNetworkKinds road, out _));
            Assert.Equal(TypedNetworkKinds.Road, road);
            Assert.False(TypedNetworkMath.TryParseKind("pipe", out _, out string error));
            Assert.Contains("low_voltage", error);
            Assert.False(TypedNetworkMath.TryParseKind("all", out _, out string all));
            Assert.Contains("low_voltage", all);
        }

        [Fact]
        public void Network_sort_parsing_is_kind_aware()
        {
            Assert.True(
                TypedNetworkMath.TryParseNetworkSort(
                    null,
                    TypedNetworkKinds.Road,
                    out string empty,
                    out _));
            Assert.Null(empty);
            Assert.True(
                TypedNetworkMath.TryParseNetworkSort(
                    "traffic_volume",
                    TypedNetworkKinds.Road,
                    out string volume,
                    out _));
            Assert.Equal("traffic_volume", volume);
            Assert.False(
                TypedNetworkMath.TryParseNetworkSort(
                    "traffic_volume",
                    TypedNetworkKinds.Water,
                    out _,
                    out string waterTraffic));
            Assert.Contains("kind=road", waterTraffic);
            Assert.False(
                TypedNetworkMath.TryParseNetworkSort(
                    "load",
                    TypedNetworkKinds.Road,
                    out _,
                    out string roadLoad));
            Assert.Contains("low_voltage", roadLoad);
            Assert.True(
                TypedNetworkMath.TryParseNetworkSort(
                    "load",
                    TypedNetworkKinds.LowVoltage,
                    out string load,
                    out _));
            Assert.Equal("load", load);
            Assert.True(TypedNetworkMath.TryParseNetworkSort("distance", TypedNetworkKinds.Sewage, out _, out _));
            Assert.False(
                TypedNetworkMath.TryParseNetworkSort(
                    "lanes",
                    TypedNetworkKinds.Road,
                    out _,
                    out string unknown));
            Assert.Contains("congestion", unknown);
        }

        [Fact]
        public void Traffic_sort_ranks_highest_first_and_distance_ranks_nearest()
        {
            Assert.True(
                TypedNetworkMath.NetworkListRank("traffic_volume", 10f, 80f, 5f)
                < TypedNetworkMath.NetworkListRank("traffic_volume", 1f, 10f, 90f));
            Assert.True(
                TypedNetworkMath.NetworkListRank("congestion", 10f, 10f, 40f)
                < TypedNetworkMath.NetworkListRank("congestion", 1f, 80f, 5f));
            Assert.True(
                TypedNetworkMath.NetworkListRank("load", 10f, 0f, 0f, 0.9f)
                < TypedNetworkMath.NetworkListRank("load", 1f, 0f, 0f, 0.2f));
            Assert.True(
                TypedNetworkMath.NetworkListRank("distance", 3f, 0f, 0f)
                < TypedNetworkMath.NetworkListRank("distance", 9f, 100f, 100f));
        }

        [Fact]
        public void Electricity_load_uses_absolute_flow_and_keeps_the_worst_edge()
        {
            Assert.Equal(0f, TypedNetworkMath.ElectricityLoadRatio(10, 0));
            Assert.Equal(0.5f, TypedNetworkMath.ElectricityLoadRatio(-10, 20), 5);

            int flow = 0;
            int capacity = 0;
            bool bottleneck = false;
            float load = -1f;
            TypedNetworkMath.ConsiderElectricityEdge(4, 20, false, ref flow, ref capacity, ref bottleneck, ref load);
            TypedNetworkMath.ConsiderElectricityEdge(-18, 20, true, ref flow, ref capacity, ref bottleneck, ref load);
            TypedNetworkMath.ConsiderElectricityEdge(5, 5, false, ref flow, ref capacity, ref bottleneck, ref load);
            Assert.Equal(5, flow);
            Assert.Equal(5, capacity);
            Assert.True(bottleneck);
            Assert.Equal(1f, load, 5);
        }

        private static TypedNetworkEdge[] TwoJunctions(float3 first, float3 second)
        {
            float3 along = new float3(20f, 0f, 0f);
            float3 across = new float3(0f, 0f, 20f);
            return new[]
            {
                Road(1, 1, 10, first, first - along, outsideEnd: true),
                Road(2, 1, 11, first, first + across),
                Road(3, 1, 12, first, first - across),
                Road(4, 2, 20, second, second + along, outsideEnd: true),
                Road(5, 2, 21, second, second + across),
                Road(6, 2, 22, second, second - across),
            };
        }

        private static TypedNetworkEdge Road(
            int id,
            int start,
            int end,
            float3 a,
            float3 b,
            bool outsideStart = false,
            bool outsideEnd = false)
        {
            return new TypedNetworkEdge(
                id,
                start,
                end,
                new[] { a, b },
                TypedNetworkKinds.Road,
                outsideStart,
                outsideEnd);
        }

        private static TypedNetworkEdge Pipe(int id, int start, int end, float3 a, float3 b)
        {
            return new TypedNetworkEdge(
                id,
                start,
                end,
                new[] { a, b },
                TypedNetworkKinds.Water,
                false,
                false);
        }

        private static TypedNetworkEdge Cable(int id, int start, int end, float3 a, float3 b)
        {
            return new TypedNetworkEdge(
                id,
                start,
                end,
                new[] { a, b },
                TypedNetworkKinds.LowVoltage,
                false,
                false);
        }

        private static float3 Xz(float x, float z)
        {
            return new float3(x, 0f, z);
        }
    }
}
