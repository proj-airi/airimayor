using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.Mathematics;

namespace CS2MCP
{
    internal enum MapGrade
    {
        Ground,
        Tunnel,
        Bridge
    }

    internal struct CityOsmRoadSource
    {
        public int Index;
        public string Entity;
        public string Prefab;
        public string Name;
        public string Highway;
        public Dictionary<string, string> UpgradeTags;
        public TypedNetworkKinds Utility;
        public TypedNetworkKinds Transport;
        public string OneWay;
        public int? Lanes;
        public double? SpeedKmh;
        public float WidthMeters;
        public MapGrade Grade;
        public CityOsmJunction Start;
        public CityOsmJunction End;
        public RoadPath Curve;
    }

    internal struct CityOsmJunction
    {
        public int Index;
        public string Entity;
        public float3 Position;
    }

    internal struct CityOsmBuildingSource
    {
        public int Index;
        public string Entity;
        public string Prefab;
        public string Name;
        public string Building;
        public IReadOnlyList<string> FacilityUpgrades;
        public float3 Position;
        public quaternion Rotation;
        public float2 LotSize;
    }

    internal sealed class CityOsmRoad
    {
        public long Id;
        public string Entity;
        public string Prefab;
        public string Highway;
        public string OneWay;
        public string Bridge;
        public string Tunnel;
        public string Layer;
        public readonly List<CityOsmSection> Sections = new List<CityOsmSection>();
    }

    internal sealed class CityOsmSection
    {
        public long StartNode;
        public long EndNode;
        public readonly List<RoadPath> Curves = new List<RoadPath>();
    }

    internal sealed class CityOsmBuilding
    {
        public long Id;
        public string Entity;
        public string Prefab;
        public string Building;
        public float3[] Footprint;
        public float3? Position;
        public quaternion? Rotation;
    }

    internal sealed class CityOsmGeometry
    {
        internal readonly OsmDocument Document;
        private readonly Dictionary<long, float3> m_Positions;
        private readonly List<CityOsmRoad> m_Roads = new List<CityOsmRoad>();
        private readonly List<CityOsmBuilding> m_Buildings = new List<CityOsmBuilding>();
        private readonly List<string> m_Unprojected = new List<string>();
        private bool m_Dirty = true;

        internal CityOsmGeometry(OsmDocument document)
        {
            Document = document.Copy();
            m_Positions = document.Nodes.Values.ToDictionary(node => node.Id, CityOsm.Position);
            Refresh();
        }

        public IReadOnlyDictionary<long, float3> Positions => m_Positions;
        public IReadOnlyList<CityOsmRoad> Roads { get { Refresh(); return m_Roads; } }
        public IReadOnlyList<CityOsmBuilding> Buildings { get { Refresh(); return m_Buildings; } }
        public IReadOnlyList<string> Unprojected { get { Refresh(); return m_Unprojected; } }

        public void MoveNode(long id, float3 position)
        {
            if (!m_Positions.ContainsKey(id))
            {
                throw new KeyNotFoundException($"Unknown OSM node {id}.");
            }
            if (!math.all(math.isfinite(position.xz)) || float.IsInfinity(position.y))
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }
            m_Positions[id] = position;
            m_Dirty = true;
        }

        public void SetWayTag(long id, string key, string value)
        {
            Dictionary<string, string> tags = Document.Ways[id].Tags;
            if (value == null)
            {
                tags.Remove(key);
            }
            else
            {
                tags[key] = value;
            }
            m_Dirty = true;
        }

        private void Refresh()
        {
            if (!m_Dirty)
            {
                return;
            }
            m_Roads.Clear();
            m_Buildings.Clear();
            m_Unprojected.Clear();
            CityOsm.ProjectWays(Document, m_Positions, m_Roads, m_Buildings, m_Unprojected);
            m_Dirty = false;
        }
    }

    internal static class CityOsm
    {
        internal const double LongitudeMeters = 111319.49079327358;
        internal const double LatitudeMeters = 110574.27582159436;
        private const float EditorPrecision = 0.012f;
        private const string EntityTag = "airimayor:entity";
        private const string PrefabTag = "airimayor:prefab";
        private const string CurveTag = "airimayor:curve";
        private const string PoseTag = "airimayor:pose";
        private const string EndsTag = "airimayor:ends";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static OsmDocument Export(IEnumerable<CityOsmRoadSource> roads, IEnumerable<CityOsmBuildingSource> buildings)
        {
            var document = new OsmDocument();
            document.Attributes["upload"] = "never";
            var decks = new List<CityOsmRoadSource>();
            foreach (CityOsmRoadSource road in roads)
            {
                var way = new OsmWay(NativeId(road.Index));
                AddNativeWay(document, way, road.Entity, road.Prefab, road.Name);
                if (CityOsmTransportWays.IsTransport(road.Transport)) CityOsmTransportWays.WriteTags(way, road.Transport);
                else if (road.Utility == TypedNetworkKinds.None)
                {
                    way.Tags["highway"] = road.Highway;
                    if (road.UpgradeTags != null)
                        foreach (var tag in road.UpgradeTags) way.Tags.Add(tag.Key, tag.Value);
                }
                else
                {
                    way.Tags["location"] = "underground";
                    if ((road.Utility & TypedNetworkKinds.LowVoltage) != 0) way.Tags["power"] = "cable";
                    var pipes = road.Utility & (TypedNetworkKinds.Water | TypedNetworkKinds.Sewage);
                    if (pipes != TypedNetworkKinds.None)
                    {
                        way.Tags["man_made"] = "pipeline";
                        way.Tags["substance"] = pipes == TypedNetworkKinds.Water ? "water"
                            : pipes == TypedNetworkKinds.Sewage ? "sewage" : "water;sewage";
                    }
                }
                if (road.Lanes > 0)
                {
                    way.Tags["lanes"] = road.Lanes.Value.ToString(Invariant);
                }
                if (road.SpeedKmh > 0)
                {
                    way.Tags["maxspeed"] = road.SpeedKmh.Value.ToString("R", Invariant);
                }
                if (road.WidthMeters > 0f)
                {
                    way.Tags["width"] = road.WidthMeters.ToString("R", Invariant);
                }
                if (road.OneWay != null)
                {
                    way.Tags["oneway"] = road.OneWay;
                }
                if (road.Grade != MapGrade.Ground)
                {
                    bool bridge = road.Grade == MapGrade.Bridge;
                    way.Tags[bridge ? "bridge" : "tunnel"] = "yes";
                    decks.Add(road);
                }
                RoadPath curve = road.Curve;
                way.Tags[CurveTag] = Pack(curve.A, curve.B, curve.C, curve.D);
                way.Tags[EndsTag] = Pack(road.Start.Position, road.End.Position);
                way.NodeIds.Add(AddJunction(document, road.Start));
                int steps = SampleCount(curve);
                for (int i = 1; i < steps; i++)
                {
                    long id = ShapeId(road.Index, i);
                    document.Nodes.Add(id, ToNode(id, NetworkCourseMath.Point(curve, i / (float)steps)));
                    way.NodeIds.Add(id);
                }
                way.NodeIds.Add(AddJunction(document, road.End));
            }
            CityOsmLayers.Assign(document, decks);
            foreach (CityOsmBuildingSource building in buildings)
            {
                var way = new OsmWay(NativeId(building.Index));
                AddNativeWay(document, way, building.Entity, building.Prefab, building.Name);
                way.Tags["building"] = building.Building;
                if (building.FacilityUpgrades != null)
                    foreach (string upgrade in building.FacilityUpgrades)
                        way.Tags[CityOsmFacilityUpgrades.Tag(upgrade)] = "yes";
                if (building.Building == "school")
                {
                    way.Tags["amenity"] = "school";
                }
                way.Tags[PoseTag] = Pack(building.Position.x, building.Position.y, building.Position.z,
                    building.Rotation.value.x, building.Rotation.value.y, building.Rotation.value.z, building.Rotation.value.w,
                    building.LotSize.x, building.LotSize.y);
                float3[] corners = Corners(building.Position, building.Rotation, building.LotSize);
                for (int i = 0; i < corners.Length; i++)
                {
                    long id = ShapeId(building.Index, i);
                    document.Nodes.Add(id, ToNode(id, corners[i]));
                    way.NodeIds.Add(id);
                }
                way.NodeIds.Add(way.NodeIds[0]);
            }
            return document;
        }

        public static CityOsmGeometry Import(OsmDocument document)
        {
            document.Validate();
            return new CityOsmGeometry(document);
        }

        public static OsmDocument Export(CityOsmGeometry city)
        {
            OsmDocument result = city.Document.Copy();
            foreach (OsmNode node in result.Nodes.Values)
            {
                float3 original = Position(node);
                float3 current = city.Positions[node.Id];
                if (original.x != current.x)
                {
                    node.Longitude = current.x / LongitudeMeters;
                }
                if (original.z != current.z)
                {
                    node.Latitude = current.z / LatitudeMeters;
                }
                if (!original.y.Equals(current.y))
                {
                    if (float.IsNaN(current.y))
                    {
                        node.Tags.Remove("ele");
                    }
                    else
                    {
                        node.Tags["ele"] = current.y.ToString("R", Invariant);
                    }
                }
            }
            result.Validate();
            return result;
        }

        internal static void ProjectWays(OsmDocument document, IReadOnlyDictionary<long, float3> positions,
            List<CityOsmRoad> roads, List<CityOsmBuilding> buildings, List<string> unprojected)
        {
            var seen = new HashSet<long>();
            var junctions = new HashSet<long>();
            foreach (OsmWay way in document.Ways.Values)
            {
                if (!IsLinearWay(way))
                {
                    continue;
                }
                foreach (long id in way.NodeIds)
                {
                    if (!seen.Add(id))
                    {
                        junctions.Add(id);
                    }
                }
            }
            foreach (OsmWay way in document.Ways.Values)
            {
                if (IsLinearWay(way))
                {
                    roads.Add(ReadRoad(positions, way, junctions));
                }
                else if (way.Tags.TryGetValue("building", out string kind) && kind != "no"
                    && way.NodeIds.Count >= 4 && way.NodeIds[0] == way.NodeIds[way.NodeIds.Count - 1])
                {
                    buildings.Add(ReadBuilding(positions, way));
                }
                else
                {
                    unprojected.Add("way/" + way.Id);
                }
            }
            foreach (OsmRelation relation in document.Relations.Values)
            {
                unprojected.Add("relation/" + relation.Id);
            }
        }

        internal static bool IsLinearWay(OsmWay way) => IsRoad(way) || IsUtility(way) || CityOsmTransportWays.Kind(way) != TypedNetworkKinds.None;

        private static bool IsRoad(OsmWay way) => way.Tags.ContainsKey("highway") && Tag(way, "area") != "yes";

        internal static bool IsUtility(OsmWay way) => Tag(way, "power") == "cable" || Tag(way, "man_made") == "pipeline";

        internal static bool IsPlatform(OsmNode node) => Tag(node, "public_transport") == "platform" || Tag(node, "highway") == "bus_stop";

        private static CityOsmRoad ReadRoad(IReadOnlyDictionary<long, float3> positions, OsmWay way, HashSet<long> junctions)
        {
            var road = new CityOsmRoad
            {
                Id = way.Id,
                Entity = Tag(way, EntityTag),
                Prefab = Tag(way, PrefabTag),
                Highway = Tag(way, "highway"),
                OneWay = Tag(way, "oneway"),
                Bridge = Tag(way, "bridge"),
                Tunnel = Tag(way, "tunnel"),
                Layer = Tag(way, "layer"),
            };
            float3[] points = way.NodeIds.Select(id => positions[id]).ToArray();
            bool unchanged = MatchesNativeCurve(way, points, out RoadPath native);
            int start = 0;
            int last = points.Length - 1;
            for (int i = 1; i <= last; i++)
            {
                if (i < last && !junctions.Contains(way.NodeIds[i]))
                {
                    continue;
                }
                var section = new CityOsmSection { StartNode = way.NodeIds[start], EndNode = way.NodeIds[i] };
                if (unchanged)
                {
                    section.Curves.Add(Slice(native, start / (float)last, i / (float)last));
                }
                else
                {
                    for (int j = start; j < i; j++)
                    {
                        section.Curves.Add(RoadPath.Straight(points[j], points[j + 1]));
                    }
                }
                road.Sections.Add(section);
                start = i;
            }
            return road;
        }

        private static bool MatchesNativeCurve(OsmWay way, float3[] points, out RoadPath curve)
        {
            curve = default;
            if (!way.Tags.TryGetValue(CurveTag, out string raw) || !way.Tags.TryGetValue(EndsTag, out string ends))
            {
                return false;
            }
            float[] data = Unpack(raw, 12, CurveTag);
            curve = new RoadPath(Vector(data, 0), Vector(data, 3), Vector(data, 6), Vector(data, 9));
            int last = points.Length - 1;
            if (last != SampleCount(curve))
            {
                return false;
            }
            float[] junctions = Unpack(ends, 6, EndsTag);
            for (int i = 0; i <= last; i++)
            {
                float3 expected = i == 0 ? Vector(junctions, 0)
                    : i == last ? Vector(junctions, 3) : NetworkCourseMath.Point(curve, i / (float)last);
                if (!(math.distance(points[i], expected) <= EditorPrecision))
                {
                    return false;
                }
            }
            return true;
        }

        private static RoadPath Slice(RoadPath curve, float start, float end)
        {
            if (start == 0f && end == 1f)
            {
                return curve;
            }
            float3 a = NetworkCourseMath.Point(curve, start);
            float3 d = NetworkCourseMath.Point(curve, end);
            float3 ab = curve.B - curve.A;
            float3 bc = curve.C - curve.B;
            float3 cd = curve.D - curve.C;
            float3 b = a + (end - start) * math.lerp(math.lerp(ab, bc, start), math.lerp(bc, cd, start), start);
            float3 c = d - (end - start) * math.lerp(math.lerp(ab, bc, end), math.lerp(bc, cd, end), end);
            return new RoadPath(a, b, c, d);
        }

        private static CityOsmBuilding ReadBuilding(IReadOnlyDictionary<long, float3> positions, OsmWay way)
        {
            var building = new CityOsmBuilding
            {
                Id = way.Id,
                Building = Tag(way, "building"),
                Prefab = Tag(way, PrefabTag),
                Entity = Tag(way, EntityTag),
                Footprint = way.NodeIds.Take(way.NodeIds.Count - 1).Select(id => positions[id]).ToArray(),
            };
            if (building.Footprint.Length != 4 || !way.Tags.TryGetValue(PoseTag, out string original))
            {
                return building;
            }
            float[] data = Unpack(original, 9, PoseTag);
            float3 position = Vector(data, 0);
            quaternion rotation = new quaternion(data[3], data[4], data[5], data[6]);
            var lot = new float2(data[7], data[8]);
            float3[] expected = Corners(position, rotation, lot);
            var points = new float3[4];
            int first = way.NodeIds.IndexOf(ShapeId(way.Id - 1, 0));
            int second = way.NodeIds.IndexOf(ShapeId(way.Id - 1, 1));
            int step = (second - first + 4) % 4;
            if (first < 0 || (step != 1 && step != 3))
            {
                return building;
            }
            for (int i = 0; i < 4; i++)
            {
                int index = (first + i * step) % 4;
                if (way.NodeIds[index] != ShapeId(way.Id - 1, i))
                {
                    return building;
                }
                points[i] = building.Footprint[index];
            }
            if (CornersMatch(points, expected))
            {
                building.Position = position;
                building.Rotation = rotation;
                return building;
            }
            float3 center = (points[0] + points[1] + points[2] + points[3]) / 4f;
            float2 originalRight = expected[1].xz - expected[0].xz;
            float2 editedRight = points[1].xz - points[0].xz;
            float angle = math.atan2(originalRight.y, originalRight.x) - math.atan2(editedRight.y, editedRight.x);
            quaternion editedRotation = math.mul(quaternion.RotateY(angle), rotation);
            if (CornersMatch(points, Corners(center, editedRotation, lot)))
            {
                building.Position = center;
                building.Rotation = editedRotation;
            }
            return building;
        }

        internal static float3[] Corners(float3 position, quaternion rotation, float2 lot)
        {
            float3 right = math.mul(rotation, new float3(lot.x / 2f, 0f, 0f));
            float3 forward = math.mul(rotation, new float3(0f, 0f, lot.y / 2f));
            return new[] { position - right - forward, position + right - forward,
                position + right + forward, position - right + forward };
        }

        private static bool CornersMatch(float3[] actual, float3[] expected)
        {
            for (int i = 0; i < actual.Length; i++)
            {
                if (!(math.distance(actual[i], expected[i]) <= EditorPrecision))
                {
                    return false;
                }
            }
            return true;
        }

        internal static int SampleCount(RoadPath curve)
        {
            float startBend = math.length(curve.A - 2f * curve.B + curve.C);
            float endBend = math.length(curve.B - 2f * curve.C + curve.D);
            const float chordToleranceMeters = 0.25f;
            return math.clamp((int)math.ceil(math.sqrt(0.75f * math.max(startBend, endBend) / chordToleranceMeters)), 1, 128);
        }

        internal static long NativeId(int index) => 1L + index;
        private static long ShapeId(long index, int sample) => (1L << 32) + index * 256L + sample;

        private static long AddJunction(OsmDocument document, CityOsmJunction junction)
        {
            long id = NativeId(junction.Index);
            if (!document.Nodes.ContainsKey(id))
            {
                var node = ToNode(id, junction.Position);
                node.Tags[EntityTag] = junction.Entity;
                document.Nodes.Add(id, node);
            }
            return id;
        }

        private static void AddNativeWay(OsmDocument document, OsmWay way, string entity, string prefab, string name)
        {
            way.Tags[EntityTag] = entity;
            way.Tags[PrefabTag] = prefab;
            if (!string.IsNullOrWhiteSpace(name))
            {
                way.Tags["name"] = name;
            }
            way.Attributes["version"] = "1";
            document.Ways.Add(way.Id, way);
        }

        internal static OsmNode ToNode(long id, float3 position)
        {
            var node = new OsmNode(id, position.z / LatitudeMeters, position.x / LongitudeMeters);
            node.Attributes["version"] = "1";
            node.Tags["ele"] = position.y.ToString("R", Invariant);
            return node;
        }

        internal static float3 Position(OsmNode node)
        {
            float height = node.Tags.TryGetValue("ele", out string raw) && float.TryParse(raw, NumberStyles.Float, Invariant, out float parsed)
                && math.isfinite(parsed) ? parsed : float.NaN;
            return new float3((float)(node.Longitude * LongitudeMeters), height, (float)(node.Latitude * LatitudeMeters));
        }

        internal static string Tag(OsmElement element, string key)
        {
            return element != null && element.Tags.TryGetValue(key, out string value) ? value : null;
        }

        private static string Pack(params float3[] values)
        {
            return Pack(values.SelectMany(value => new[] { value.x, value.y, value.z }).ToArray());
        }

        private static string Pack(params float[] values)
        {
            return string.Join(",", values.Select(value => value.ToString("R", Invariant)));
        }

        private static float[] Unpack(string raw, int count, string tag)
        {
            string[] parts = raw.Split(',');
            if (parts.Length != count)
            {
                throw new FormatException("Invalid " + tag);
            }
            return parts.Select(part => Number(part, tag)).ToArray();
        }

        private static float Number(string text, string tag)
        {
            if (!float.TryParse(text, NumberStyles.Float, Invariant, out float value) || !math.isfinite(value))
            {
                throw new FormatException("Invalid " + tag + " value");
            }
            return value;
        }

        private static float3 Vector(float[] values, int first) => new float3(values[first], values[first + 1], values[first + 2]);
    }
}
