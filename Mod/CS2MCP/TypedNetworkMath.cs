using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace CS2MCP
{
    [Flags]
    internal enum TypedNetworkKinds : byte
    {
        None = 0,
        Water = 1,
        Sewage = 2,
        LowVoltage = 4,
        Road = 8,
    }

    internal enum NetworkTopologyClass : byte
    {
        NearMiss,
        UnnodedCrossing,
        TooCloseJunctions,
        IsolatedRoad,
        IsolatedWater,
        IsolatedSewage,
        IsolatedLowVoltage,
    }

    /// <summary>
    /// One top-level net edge in a typed snapshot. Geometry is sampled world
    /// points; classification is already resolved by the caller.
    /// </summary>
    internal readonly struct TypedNetworkEdge
    {
        public TypedNetworkEdge(
            int entityIndex,
            int startNode,
            int endNode,
            float3[] points,
            TypedNetworkKinds kinds,
            bool startOutside,
            bool endOutside)
        {
            EntityIndex = entityIndex;
            StartNode = startNode;
            EndNode = endNode;
            Points = points;
            Kinds = kinds;
            StartOutside = startOutside;
            EndOutside = endOutside;
        }

        public int EntityIndex { get; }
        public int StartNode { get; }
        public int EndNode { get; }
        public float3[] Points { get; }
        public TypedNetworkKinds Kinds { get; }
        public bool StartOutside { get; }
        public bool EndOutside { get; }
    }

    internal readonly struct NetworkTopologyFinding
    {
        public NetworkTopologyFinding(
            NetworkTopologyClass topologyClass,
            int edgeA,
            int edgeB,
            int nodeA,
            int nodeB,
            int componentSize,
            float distanceM,
            float2 anchor)
        {
            Class = topologyClass;
            EdgeA = edgeA;
            EdgeB = edgeB;
            NodeA = nodeA;
            NodeB = nodeB;
            ComponentSize = componentSize;
            DistanceM = distanceM;
            Anchor = anchor;
        }

        public NetworkTopologyClass Class { get; }
        public int EdgeA { get; }
        public int EdgeB { get; }
        public int NodeA { get; }
        public int NodeB { get; }
        public int ComponentSize { get; }
        public float DistanceM { get; }
        public float2 Anchor { get; }
    }

    internal readonly struct NetworkDeadEnd
    {
        public NetworkDeadEnd(int edge, int node, int degree)
        {
            Edge = edge;
            Node = node;
            Degree = degree;
        }

        public int Edge { get; }
        public int Node { get; }
        public int Degree { get; }
    }

    /// <summary>
    /// Typed-network identity, connected components, and road topology QA.
    /// Callers map native prefab layers onto <see cref="TypedNetworkKinds"/>;
    /// this module never sees ECS.
    /// </summary>
    internal static class TypedNetworkMath
    {
        public const TypedNetworkKinds ProductKinds =
            TypedNetworkKinds.Road
            | TypedNetworkKinds.Water
            | TypedNetworkKinds.Sewage
            | TypedNetworkKinds.LowVoltage;

        public const float NearMissMeters = 32f;
        public const float CloseJunctionMeters = 32f;
        public const float CrossingHeightMeters = 4f;
        public const float GridMeters = 96f;

        public static TypedNetworkKinds FromUtilities(bool water, bool sewage, bool lowVoltage)
        {
            TypedNetworkKinds kinds = TypedNetworkKinds.None;
            if (water)
            {
                kinds |= TypedNetworkKinds.Water;
            }
            if (sewage)
            {
                kinds |= TypedNetworkKinds.Sewage;
            }
            if (lowVoltage)
            {
                kinds |= TypedNetworkKinds.LowVoltage;
            }
            return kinds;
        }

        public static TypedNetworkKinds Classify(bool isRoad, bool water, bool sewage, bool lowVoltage)
        {
            if (isRoad)
            {
                return TypedNetworkKinds.Road;
            }
            return FromUtilities(water, sewage, lowVoltage);
        }

        public static bool MatchesFilter(TypedNetworkKinds kinds, TypedNetworkKinds filter)
        {
            if ((kinds & ProductKinds) == TypedNetworkKinds.None)
            {
                return false;
            }
            return filter == ProductKinds || (kinds & filter) != 0;
        }

        public static bool TryParseKind(string raw, out TypedNetworkKinds kind, out string error)
        {
            kind = TypedNetworkKinds.None;
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "kind is required; pass road, water, sewage or low_voltage";
                return false;
            }
            switch (raw.Trim().ToLowerInvariant())
            {
                case "road":
                    kind = TypedNetworkKinds.Road;
                    return true;
                case "water":
                    kind = TypedNetworkKinds.Water;
                    return true;
                case "sewage":
                    kind = TypedNetworkKinds.Sewage;
                    return true;
                case "low_voltage":
                    kind = TypedNetworkKinds.LowVoltage;
                    return true;
                default:
                    error = "kind must be road, water, sewage or low_voltage";
                    return false;
            }
        }

        public static bool TryParseNetworkSort(
            string raw,
            TypedNetworkKinds kind,
            out string sort,
            out string error)
        {
            sort = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
            error = null;
            if (string.IsNullOrEmpty(sort))
            {
                return true;
            }
            if (sort == "distance") return true;
            if (sort == "traffic_volume" || sort == "congestion")
            {
                if (kind != TypedNetworkKinds.Road)
                {
                    error = "sort=traffic_volume and sort=congestion are only valid for kind=road";
                    return false;
                }
                return true;
            }
            if (sort == "load")
            {
                if (kind != TypedNetworkKinds.LowVoltage)
                {
                    error = "sort=load is only valid for kind=low_voltage";
                    return false;
                }
                return true;
            }
            if (kind == TypedNetworkKinds.Road)
            {
                error = "sort must be distance, traffic_volume or congestion";
            }
            else if (kind == TypedNetworkKinds.LowVoltage)
            {
                error = "sort must be distance or load";
            }
            else
            {
                error = "sort must be distance for water and sewage";
            }
            return false;
        }

        public static float NetworkListRank(
            string sort,
            float distance,
            float volumeIndex,
            float congestionIndex,
            float loadRatio = 0f)
        {
            if (sort == "traffic_volume")
            {
                return -volumeIndex;
            }
            if (sort == "congestion")
            {
                return -congestionIndex;
            }
            if (sort == "load")
            {
                return -loadRatio;
            }
            return distance;
        }

        public static float ElectricityLoadRatio(int flow, int capacity)
        {
            if (capacity <= 0)
            {
                return 0f;
            }
            return math.abs(flow) / (float)capacity;
        }

        /// <summary>
        /// Keep the worst-loaded incident flow edge. Bottleneck stays true
        /// once any incident edge is a bottleneck.
        /// </summary>
        public static void ConsiderElectricityEdge(
            int flow,
            int capacity,
            bool bottleneck,
            ref int chosenAbsFlow,
            ref int chosenCapacity,
            ref bool anyBottleneck,
            ref float chosenLoad)
        {
            anyBottleneck |= bottleneck;
            int absFlow = math.abs(flow);
            float load = ElectricityLoadRatio(flow, capacity);
            if (load < chosenLoad || (load == chosenLoad && absFlow <= chosenAbsFlow))
            {
                return;
            }
            chosenLoad = load;
            chosenAbsFlow = absFlow;
            chosenCapacity = capacity;
        }

        public static string TopologyClassName(NetworkTopologyClass topologyClass)
        {
            switch (topologyClass)
            {
                case NetworkTopologyClass.NearMiss:
                    return "near_miss";
                case NetworkTopologyClass.UnnodedCrossing:
                    return "unnoded_crossing";
                case NetworkTopologyClass.TooCloseJunctions:
                    return "too_close_junctions";
                case NetworkTopologyClass.IsolatedRoad:
                    return "isolated_road";
                case NetworkTopologyClass.IsolatedWater:
                    return "isolated_water";
                case NetworkTopologyClass.IsolatedSewage:
                    return "isolated_sewage";
                case NetworkTopologyClass.IsolatedLowVoltage:
                    return "isolated_low_voltage";
                default:
                    return "unknown";
            }
        }

        private static int[] LabelComponents(
            IReadOnlyList<TypedNetworkEdge> edges,
            TypedNetworkKinds kind)
        {
            int count = edges != null ? edges.Count : 0;
            var labels = new int[count];
            var union = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                TypedNetworkEdge edge = edges[i];
                if ((edge.Kinds & kind) == 0)
                {
                    labels[i] = -1;
                    continue;
                }
                Union(union, edge.StartNode, edge.EndNode);
            }
            for (int i = 0; i < count; i++)
            {
                if (labels[i] == -1)
                {
                    continue;
                }
                labels[i] = Find(union, edges[i].StartNode);
            }
            return labels;
        }

        private static Dictionary<int, int> RoadDegrees(IReadOnlyList<TypedNetworkEdge> edges)
        {
            var degrees = new Dictionary<int, int>();
            if (edges == null)
            {
                return degrees;
            }
            for (int i = 0; i < edges.Count; i++)
            {
                TypedNetworkEdge edge = edges[i];
                if ((edge.Kinds & TypedNetworkKinds.Road) == 0)
                {
                    continue;
                }
                AddDegree(degrees, edge.StartNode);
                AddDegree(degrees, edge.EndNode);
            }
            return degrees;
        }

        public static List<NetworkDeadEnd> FindRoadDeadEnds(IReadOnlyList<TypedNetworkEdge> edges)
        {
            var deadEnds = new List<NetworkDeadEnd>();
            Dictionary<int, int> degrees = RoadDegrees(edges);
            if (edges == null)
            {
                return deadEnds;
            }
            for (int i = 0; i < edges.Count; i++)
            {
                TypedNetworkEdge edge = edges[i];
                if ((edge.Kinds & TypedNetworkKinds.Road) == 0)
                {
                    continue;
                }
                if (DegreeOf(degrees, edge.StartNode) == 1)
                {
                    deadEnds.Add(new NetworkDeadEnd(i, edge.StartNode, 1));
                }
                if (DegreeOf(degrees, edge.EndNode) == 1)
                {
                    deadEnds.Add(new NetworkDeadEnd(i, edge.EndNode, 1));
                }
            }
            return deadEnds;
        }

        public static List<NetworkTopologyFinding> FindRoadIssues(IReadOnlyList<TypedNetworkEdge> edges, Bounds2? bounds = null)
        {
            var findings = new List<NetworkTopologyFinding>();
            if (edges == null || edges.Count == 0)
            {
                return findings;
            }

            Dictionary<int, int> degrees = RoadDegrees(edges);
            AddIsolatedComponents(
                edges,
                TypedNetworkKinds.Road,
                NetworkTopologyClass.IsolatedRoad,
                bounds, findings);
            AddTooCloseJunctions(edges, degrees, findings);
            var roadGrid = RoadGrid(edges);
            AddNearMisses(edges, degrees, roadGrid, findings);
            AddUnnodedCrossings(edges, roadGrid, bounds, findings);
            if (bounds.HasValue) findings.RemoveAll(finding => !MathUtils.Intersect(bounds.Value, finding.Anchor));
            return findings;
        }

        /// <summary>
        /// One finding per utility component that does not share a node with any
        /// road edge. Snapshot must still include roads so connectivity is visible.
        /// </summary>
        public static List<NetworkTopologyFinding> FindUtilityIsolatedFindings(
            IReadOnlyList<TypedNetworkEdge> edges,
            TypedNetworkKinds kind, Bounds2? bounds = null)
        {
            var findings = new List<NetworkTopologyFinding>();
            NetworkTopologyClass topologyClass;
            if (kind == TypedNetworkKinds.Water)
            {
                topologyClass = NetworkTopologyClass.IsolatedWater;
            }
            else if (kind == TypedNetworkKinds.Sewage)
            {
                topologyClass = NetworkTopologyClass.IsolatedSewage;
            }
            else if (kind == TypedNetworkKinds.LowVoltage)
            {
                topologyClass = NetworkTopologyClass.IsolatedLowVoltage;
            }
            else
            {
                return findings;
            }
            AddIsolatedComponents(edges, kind, topologyClass, bounds, findings);
            return findings;
        }

        private static void AddIsolatedComponents(
            IReadOnlyList<TypedNetworkEdge> edges,
            TypedNetworkKinds kind,
            NetworkTopologyClass topologyClass,
            Bounds2? bounds,
            List<NetworkTopologyFinding> findings)
        {
            if (edges == null || edges.Count == 0) return;
            int[] labels = LabelComponents(edges, kind);
            var roadNodes = new HashSet<int>();
            if (kind != TypedNetworkKinds.Road)
            {
                foreach (TypedNetworkEdge edge in edges)
                {
                    if ((edge.Kinds & TypedNetworkKinds.Road) == 0) continue;
                    roadNodes.Add(edge.StartNode);
                    roadNodes.Add(edge.EndNode);
                }
            }
            var components = new Dictionary<int, (int Size, bool Connected, int Edge, float2 Site)>();
            for (int i = 0; i < edges.Count; i++)
            {
                int label = labels[i];
                if (label < 0) continue;
                TypedNetworkEdge edge = edges[i];
                if (!components.TryGetValue(label, out var component)) component.Edge = -1;
                component.Size++;
                component.Connected |= kind == TypedNetworkKinds.Road ? edge.StartOutside || edge.EndOutside
                    : roadNodes.Contains(edge.StartNode) || roadNodes.Contains(edge.EndNode);
                if (component.Edge < 0 && TryComponentSite(edge.Points, bounds, out float2 site))
                {
                    component.Edge = i;
                    component.Site = site;
                }
                components[label] = component;
            }
            foreach (var component in components.Values)
            {
                if (component.Connected || component.Edge < 0) continue;
                TypedNetworkEdge edge = edges[component.Edge];
                findings.Add(new NetworkTopologyFinding(topologyClass, component.Edge, -1, edge.StartNode, edge.EndNode,
                    component.Size, 0f, component.Site));
            }
        }

        private static bool TryComponentSite(float3[] points, Bounds2? bounds, out float2 site)
        {
            site = points[points.Length / 2].xz;
            if (!bounds.HasValue || MathUtils.Intersect(bounds.Value, site)) return true;
            for (int i = 1; i < points.Length; i++)
            {
                var line = new Line2.Segment(points[i - 1].xz, points[i].xz);
                if (MathUtils.Intersect(bounds.Value, line, out float2 interval))
                {
                    site = math.lerp(line.a, line.b, math.csum(interval) * 0.5f);
                    return true;
                }
            }
            return false;
        }

        private readonly struct JunctionSite
        {
            public JunctionSite(float3 position, int edgeIndex)
            {
                Position = position;
                EdgeIndex = edgeIndex;
            }

            public float3 Position { get; }
            public int EdgeIndex { get; }
        }

        private static void AddTooCloseJunctions(
            IReadOnlyList<TypedNetworkEdge> edges,
            Dictionary<int, int> degrees,
            List<NetworkTopologyFinding> findings)
        {
            var junctions = new Dictionary<int, JunctionSite>();
            for (int i = 0; i < edges.Count; i++)
            {
                TypedNetworkEdge edge = edges[i];
                if ((edge.Kinds & TypedNetworkKinds.Road) == 0
                    || edge.Points == null
                    || edge.Points.Length < 2)
                {
                    continue;
                }
                if (DegreeOf(degrees, edge.StartNode) >= 3 && !junctions.ContainsKey(edge.StartNode))
                {
                    junctions.Add(edge.StartNode, new JunctionSite(edge.Points[0], i));
                }
                if (DegreeOf(degrees, edge.EndNode) >= 3 && !junctions.ContainsKey(edge.EndNode))
                {
                    junctions.Add(
                        edge.EndNode,
                        new JunctionSite(edge.Points[edge.Points.Length - 1], i));
                }
            }

            var grid = new Dictionary<long, List<int>>();
            foreach (KeyValuePair<int, JunctionSite> junction in junctions)
            {
                int cellX = (int)math.floor(junction.Value.Position.x / CloseJunctionMeters);
                int cellZ = (int)math.floor(junction.Value.Position.z / CloseJunctionMeters);
                long key = CellKey(cellX, cellZ);
                if (!grid.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>();
                    grid[key] = list;
                }
                list.Add(junction.Key);
            }

            foreach (List<int> members in grid.Values)
            {
                float3 origin = junctions[members[0]].Position;
                int cellX = (int)math.floor(origin.x / CloseJunctionMeters);
                int cellZ = (int)math.floor(origin.z / CloseJunctionMeters);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue(CellKey(cellX + dx, cellZ + dz), out List<int> neighbors))
                        {
                            continue;
                        }
                        for (int i = 0; i < members.Count; i++)
                        {
                            int nodeA = members[i];
                            for (int j = 0; j < neighbors.Count; j++)
                            {
                                int nodeB = neighbors[j];
                                if (nodeA >= nodeB)
                                {
                                    continue;
                                }
                                JunctionSite siteA = junctions[nodeA];
                                JunctionSite siteB = junctions[nodeB];
                                float distance = math.distance(siteA.Position.xz, siteB.Position.xz);
                                if (distance >= CloseJunctionMeters)
                                {
                                    continue;
                                }
                                findings.Add(new NetworkTopologyFinding(
                                    NetworkTopologyClass.TooCloseJunctions,
                                    siteA.EdgeIndex,
                                    siteB.EdgeIndex,
                                    nodeA,
                                    nodeB,
                                    0,
                                    distance,
                                    (siteA.Position.xz + siteB.Position.xz) * 0.5f));
                            }
                        }
                    }
                }
            }
        }

        private static void AddNearMisses(
            IReadOnlyList<TypedNetworkEdge> edges,
            Dictionary<int, int> degrees,
            Dictionary<long, List<int>> grid,
            List<NetworkTopologyFinding> findings)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                TypedNetworkEdge edge = edges[i];
                if ((edge.Kinds & TypedNetworkKinds.Road) == 0
                    || edge.Points == null
                    || edge.Points.Length < 2)
                {
                    continue;
                }
                TryNearMiss(edges, degrees, grid, i, edge.StartNode, edge.Points[0], findings);
                TryNearMiss(
                    edges,
                    degrees,
                    grid,
                    i,
                    edge.EndNode,
                    edge.Points[edge.Points.Length - 1],
                    findings);
            }
        }

        private static void TryNearMiss(
            IReadOnlyList<TypedNetworkEdge> edges,
            Dictionary<int, int> degrees,
            Dictionary<long, List<int>> grid,
            int edgeIndex,
            int node,
            float3 position,
            List<NetworkTopologyFinding> findings)
        {
            if (DegreeOf(degrees, node) != 1)
            {
                return;
            }
            float best = NearMissMeters * NearMissMeters;
            int bestEdge = -1;
            TypedNetworkEdge self = edges[edgeIndex];
            int2 min = (int2)math.floor((position.xz - NearMissMeters) / GridMeters);
            int2 max = (int2)math.floor((position.xz + NearMissMeters) / GridMeters);
            var seen = new HashSet<int>();
            for (int x = min.x; x <= max.x; x++)
            for (int z = min.y; z <= max.y; z++)
            {
                if (!grid.TryGetValue(CellKey(x, z), out var candidates)) continue;
                foreach (int i in candidates)
                {
                    if (i == edgeIndex || !seen.Add(i)) continue;
                    TypedNetworkEdge other = edges[i];
                    if (SharesNode(other, self.StartNode) || SharesNode(other, self.EndNode)) continue;
                    float distanceSquared = DistanceSquaredToPolyline(position, other.Points);
                    if (distanceSquared < best)
                    {
                        best = distanceSquared;
                        bestEdge = i;
                    }
                }
            }
            if (bestEdge < 0)
            {
                return;
            }
            findings.Add(new NetworkTopologyFinding(
                NetworkTopologyClass.NearMiss,
                edgeIndex,
                bestEdge,
                node,
                -1,
                0,
                math.sqrt(best),
                position.xz));
        }

        private static Dictionary<long, List<int>> RoadGrid(IReadOnlyList<TypedNetworkEdge> edges)
        {
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                TypedNetworkEdge edge = edges[i];
                if ((edge.Kinds & TypedNetworkKinds.Road) == 0
                    || edge.Points == null
                    || edge.Points.Length < 2)
                {
                    continue;
                }
                PolylineBounds(edge.Points, out float2 boundsMin, out float2 boundsMax);
                int minX = (int)math.floor(boundsMin.x / GridMeters);
                int maxX = (int)math.floor(boundsMax.x / GridMeters);
                int minZ = (int)math.floor(boundsMin.y / GridMeters);
                int maxZ = (int)math.floor(boundsMax.y / GridMeters);
                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        long key = ((long)x << 32) ^ (uint)z;
                        if (!grid.TryGetValue(key, out List<int> list))
                        {
                            list = new List<int>();
                            grid[key] = list;
                        }
                        if (list.Count == 0 || list[list.Count - 1] != i)
                        {
                            list.Add(i);
                        }
                    }
                }
            }

            return grid;
        }

        private static void AddUnnodedCrossings(IReadOnlyList<TypedNetworkEdge> edges, Dictionary<long, List<int>> grid,
            Bounds2? bounds, List<NetworkTopologyFinding> findings)
        {
            var seen = new HashSet<long>();
            foreach (List<int> cell in grid.Values)
            {
                for (int i = 0; i < cell.Count; i++)
                {
                    for (int j = i + 1; j < cell.Count; j++)
                    {
                        int a = cell[i];
                        int b = cell[j];
                        long pair = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                        if (!seen.Add(pair))
                        {
                            continue;
                        }
                        TypedNetworkEdge first = edges[a];
                        TypedNetworkEdge second = edges[b];
                        if (first.StartNode == second.StartNode
                            || first.StartNode == second.EndNode
                            || first.EndNode == second.StartNode
                            || first.EndNode == second.EndNode)
                        {
                            continue;
                        }
                        if (!TryPolylineCrossing(first.Points, second.Points, bounds, out float heightDelta, out float2 site)) continue;
                        findings.Add(new NetworkTopologyFinding(
                            NetworkTopologyClass.UnnodedCrossing,
                            a,
                            b,
                            -1,
                            -1,
                            0,
                            heightDelta, site));
                    }
                }
            }
        }

        private static float DistanceSquaredToPolyline(float3 point, float3[] points)
        {
            float best = float.MaxValue;
            for (int i = 1; i < points.Length; i++)
            {
                float2 start = points[i - 1].xz;
                float2 direction = points[i].xz - start;
                float lengthSquared = math.lengthsq(direction);
                float fraction = lengthSquared < 0.01f * 0.01f
                    ? 0f
                    : math.saturate(math.dot(point.xz - start, direction) / lengthSquared);
                float height = math.lerp(points[i - 1].y, points[i].y, fraction);
                if (math.abs(height - point.y) <= CrossingHeightMeters)
                    best = math.min(best, math.distancesq(point.xz, start + direction * fraction));
            }
            return best;
        }

        private static bool TryPolylineCrossing(float3[] first, float3[] second, Bounds2? bounds, out float heightDelta, out float2 site)
        {
            heightDelta = 0f;
            site = default;
            for (int i = 1; i < first.Length; i++)
            {
                for (int j = 1; j < second.Length; j++)
                {
                    if (!TrySegmentIntersection(
                            first[i - 1].xz,
                            first[i].xz,
                            second[j - 1].xz,
                            second[j].xz,
                            out float t,
                            out float u))
                    {
                        continue;
                    }
                    float y1 = math.lerp(first[i - 1].y, first[i].y, t);
                    float y2 = math.lerp(second[j - 1].y, second[j].y, u);
                    heightDelta = math.abs(y1 - y2);
                    if (heightDelta > CrossingHeightMeters) continue;
                    if ((i == 1 && t <= 0.02f) || (i == first.Length - 1 && t >= 0.98f)
                        || (j == 1 && u <= 0.02f) || (j == second.Length - 1 && u >= 0.98f)) continue;
                    site = math.lerp(first[i - 1].xz, first[i].xz, t);
                    if (!bounds.HasValue || MathUtils.Intersect(bounds.Value, site)) return true;
                }
            }
            return false;
        }

        private static bool TrySegmentIntersection(
            float2 a,
            float2 b,
            float2 c,
            float2 d,
            out float t,
            out float u)
        {
            t = 0f;
            u = 0f;
            float2 r = b - a;
            float2 s = d - c;
            float denom = r.x * s.y - r.y * s.x;
            if (math.abs(denom) < 0.0001f)
            {
                return false;
            }
            float2 ca = c - a;
            t = (ca.x * s.y - ca.y * s.x) / denom;
            u = (ca.x * r.y - ca.y * r.x) / denom;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        private static void PolylineBounds(float3[] points, out float2 min, out float2 max)
        {
            min = points[0].xz;
            max = min;
            for (int i = 1; i < points.Length; i++)
            {
                min = math.min(min, points[i].xz);
                max = math.max(max, points[i].xz);
            }
        }

        private static long CellKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private static bool SharesNode(TypedNetworkEdge edge, int node)
        {
            return edge.StartNode == node || edge.EndNode == node;
        }

        private static void AddDegree(Dictionary<int, int> degrees, int node)
        {
            degrees.TryGetValue(node, out int current);
            degrees[node] = current + 1;
        }

        private static int DegreeOf(Dictionary<int, int> degrees, int node)
        {
            return degrees.TryGetValue(node, out int degree) ? degree : 0;
        }

        private static void Union(Dictionary<int, int> parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
            {
                parent[rootA] = rootB;
            }
        }

        private static int Find(Dictionary<int, int> parent, int node)
        {
            if (!parent.TryGetValue(node, out int next))
            {
                parent[node] = node;
                return node;
            }
            while (next != node)
            {
                parent[node] = parent[next];
                node = next;
                next = parent[node];
            }
            return node;
        }
    }
}
