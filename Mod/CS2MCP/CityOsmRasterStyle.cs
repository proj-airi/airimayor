using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;

namespace CS2MCP
{
    internal static partial class CityOsmRaster
    {
        private static readonly SKColor BuildingEdge = SKColor.Parse("#b8aa9f");
        private static readonly SKColor GreenLabel = SKColor.Parse("#345a3e");
        private static readonly SKColor GrayLabel = SKColor.Parse("#6d6258");
        private static readonly SKColor WaterLabel = SKColor.Parse("#3e6376");
        private static readonly float[] Motorway = { 1.4f, 1.9f, 2f, 3.5f, 6f, 8f, 10f, 12f, 18f, 21f, 27f, 33f };
        private static readonly float[] Primary = { 1.4f, 1.8f, 1.8f, 3.5f, 5f, 7f, 10f, 12f, 18f, 21f, 27f, 30f };
        private static readonly float[] Secondary = { 1f, 1.1f, 1.1f, 3.5f, 5f, 5f, 9f, 10f, 18f, 21f, 27f, 30f };
        private static readonly float[] Tertiary = { 0f, 0.7f, 0.7f, 2.5f, 4f, 5f, 9f, 10f, 18f, 21f, 27f, 30f };
        private static readonly float[] Link = { 0f, 0f, 0f, 1.5f, 4f, 5f, 7.8f, 9f, 12f, 13f, 16f, 17f };
        private static readonly float[] Local = { 0f, 0f, 0f, 0.6f, 2.5f, 3f, 5f, 6f, 12f, 13f, 17f, 19f };
        private static readonly float[] Service = { 0f, 0f, 0f, 0f, 0f, 2f, 2f, 3.5f, 7f, 8.5f, 11f, 12f };
        private static readonly float[] Path = { 0f, 0f, 0f, 0f, 0f, 0.7f, 1f, 1.3f, 1.3f, 1.3f, 1.6f, 1.8f };

        private readonly struct StrokeStyle
        {
            internal StrokeStyle(float width, float casingWidth, SKColor fill, SKColor casing, int rank, int nameZoom, int group = 0, bool dashed = false)
            {
                Width = width;
                CasingWidth = casingWidth;
                Fill = fill;
                Casing = casing;
                Rank = rank;
                NameZoom = nameZoom;
                Group = group;
                Dashed = dashed;
            }

            internal float Width { get; }
            internal float CasingWidth { get; }
            internal SKColor Fill { get; }
            internal SKColor Casing { get; }
            internal int Rank { get; }
            internal int NameZoom { get; }
            internal int Group { get; }
            internal bool Dashed { get; }
        }

        private readonly struct AreaStyle
        {
            internal AreaStyle(int order, string fill, int labelZoom, SKColor labelColor, SKColor outline = default)
            {
                Fill = SKColor.Parse(fill);
                Outline = outline;
                Order = order;
                LabelZoom = labelZoom;
                LabelColor = labelColor;
            }

            internal SKColor Fill { get; }
            internal SKColor Outline { get; }
            internal int Order { get; }
            internal int LabelZoom { get; }
            internal SKColor LabelColor { get; }
        }

        private static StrokeStyle NetworkStyle(OsmWay way, int zoom)
        {
            var highway = CityOsm.Tag(way, "highway");
            var railway = CityOsm.Tag(way, "railway");
            var waterway = CityOsm.Tag(way, "waterway");
            if (highway != null)
            {
                var road = RoadStyle(way, highway, zoom);
                if (road.Width > 0f) return road;
            }
            if ((railway == "rail" && zoom >= 11) || (railway is "light_rail" or "tram" or "subway" && zoom >= 14))
                return new StrokeStyle(zoom >= 16 ? 2.5f : 1.25f, 0.45f, SKColor.Parse("#707070"), SKColor.Parse("#dddddd"), 5, 17, group: 1);
            if ((waterway is "river" or "canal" && zoom >= 10) || (waterway is "stream" or "drain" or "ditch" && zoom >= 14))
                return new StrokeStyle(waterway is "river" or "canal" ? mathWidth(zoom) : 1.2f, 0f, Water, default, 0, 16, group: 2);
            return default;

            float mathWidth(int level) => Math.Max(0.9f, (level - 12) * 0.78f);
        }

        private static StrokeStyle RoadStyle(OsmWay way, string highway, int zoom)
        {
            bool link = highway.EndsWith("_link", StringComparison.Ordinal);
            string main = link ? highway.Substring(0, highway.Length - 5) : highway;
            int index = Math.Max(0, zoom - 9);
            if (zoom < 9 && main is not ("motorway" or "trunk")) return default;
            var (widths, fill, casing, rank) = main switch
            {
                "motorway" => (Motorway, "#e892a2", "#dc2a67", 10),
                "trunk" => (Motorway, "#f9b29c", "#c84e2f", 9),
                "primary" => (Primary, "#fcd6a4", "#a06b00", 8),
                "secondary" => (Secondary, "#f7fabf", "#707d05", 7),
                "tertiary" => (Tertiary, "#ffffff", "#8f8f8f", 6),
                "residential" or "unclassified" => (Local, "#ffffff", "#bbbbbb", 5),
                "living_street" => (Local, "#ededed", "#bbbbbb", 5),
                "pedestrian" => (Local, "#dddde8", "#999999", 5),
                "service" or "road" => (Service, "#ffffff", "#bbbbbb", 4),
                "cycleway" => (Path, "#5978c4", "#ffffff", 2),
                "track" => (Path, "#996600", "#ffffff", 2),
                "footway" or "path" or "steps" or "bridleway" => (Path, "#fa8072", "#ffffff", 1),
                _ => (null, null, null, -1),
            };
            if (rank < 0) return default;
            float width = link ? Link[index] : widths[index];
            if (main == "service" && CityOsm.Tag(way, "service") != null)
                width = zoom >= 16 ? width * 0.55f : 0f;
            bool dashed = rank <= 2 || CityOsm.Tag(way, "surface") is "gravel" or "unpaved";
            int nameZoom = rank >= 7 ? 13 : rank == 6 ? 14 : rank == 5 ? 15 : 16;
            float border = rank <= 2 ? 0.45f : zoom >= 17 ? 1f : 0.55f;
            return new StrokeStyle(width, border, SKColor.Parse(fill), SKColor.Parse(casing), link ? rank - 1 : rank, nameZoom, dashed: dashed);
        }

        private static AreaStyle Area(OsmElement element, int zoom)
        {
            if (CityOsm.Tag(element, "building") is string building && building != "no")
                return zoom >= 14 ? new AreaStyle(6, "#d9d0c9", 18, GrayLabel, BuildingEdge) : default;
            string natural = CityOsm.Tag(element, "natural");
            string leisure = CityOsm.Tag(element, "leisure");
            string landuse = CityOsm.Tag(element, "landuse");
            if (natural == "water" || CityOsm.Tag(element, "waterway") == "riverbank" || landuse == "reservoir")
                return new AreaStyle(5, "#aad3df", 14, WaterLabel);
            if (natural == "wood" || landuse == "forest")
                return new AreaStyle(2, "#add19e", 16, GreenLabel);
            if (leisure is "park" or "garden")
                return new AreaStyle(3, "#c8facc", 15, GreenLabel);
            if (leisure is "pitch" or "playground")
                return new AreaStyle(4, "#8ad3af", 17, GreenLabel);
            if (CityOsm.Tag(element, "highway") is "pedestrian" or "footway" && CityOsm.Tag(element, "area") == "yes" && zoom >= 14)
                return new AreaStyle(4, "#dddde8", 17, GrayLabel);
            if (CityOsm.Tag(element, "amenity") is "school" or "university" or "hospital")
                return new AreaStyle(3, "#ffffe5", 16, GrayLabel);
            string color = landuse switch
            {
                "residential" => "#e0dfdf", "commercial" or "retail" => "#f0e0e0", "industrial" => "#ebdbe8",
                "farmland" => "#eef0d5", "grass" or "meadow" => "#cdebb0", "cemetery" => "#aacbaf",
                _ => null,
            };
            return color == null ? default : new AreaStyle(1, color, 16, GrayLabel);
        }

        private static IEnumerable<(OsmElement Element, IEnumerable<IReadOnlyList<long>> Rings, AreaStyle Style)> Areas(OsmDocument map, int zoom)
        {
            foreach (var way in map.Ways.Values)
            {
                var style = Area(way, zoom);
                if (style.Fill.Alpha != 0 && Closed(way)) yield return (way, new IReadOnlyList<long>[] { way.NodeIds }, style);
            }
            foreach (var relation in map.Relations.Values)
            {
                var style = Area(relation, zoom);
                if (style.Fill.Alpha != 0 && CityOsm.Tag(relation, "type") == "multipolygon")
                    yield return (relation, RelationRings(map, relation), style);
            }
        }

        private static IEnumerable<IReadOnlyList<long>> RelationRings(OsmDocument map, OsmRelation relation)
        {
            var parts = relation.Members.Where(member => member.Kind == OsmKind.Way && member.Role is "" or "outer" or "inner")
                .GroupBy(member => member.Role == "inner")
                .Select(group => group.Select(member => map.Ways[member.Id].NodeIds).ToList());
            foreach (var group in parts)
            {
                while (group.Count != 0)
                {
                    var ring = new List<long>(group[0]);
                    group.RemoveAt(0);
                    while (ring[0] != ring[ring.Count - 1])
                    {
                        long endpoint = ring[ring.Count - 1];
                        int next = group.FindIndex(part => part[0] == endpoint || part[part.Count - 1] == endpoint);
                        if (next < 0) break;
                        var part = group[next];
                        group.RemoveAt(next);
                        ring.AddRange((part[0] == endpoint ? part : part.AsEnumerable().Reverse()).Skip(1));
                    }
                    if (ring.Count >= 4 && ring[0] == ring[ring.Count - 1]) yield return ring;
                }
            }
        }

        private static SKPath WayPath(OsmDocument map, Viewport viewport, OsmWay way, bool close)
        {
            using var builder = new SKPathBuilder();
            AppendPath(builder, map, viewport, way.NodeIds, close);
            return builder.Detach();
        }

        private static SKPath RingPath(OsmDocument map, Viewport viewport, IEnumerable<IReadOnlyList<long>> rings)
        {
            using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
            foreach (var ring in rings) AppendPath(builder, map, viewport, ring, true);
            return builder.Detach();
        }

        private static void AppendPath(SKPathBuilder builder, OsmDocument map, Viewport viewport, IReadOnlyList<long> nodes, bool close)
        {
            builder.MoveTo(viewport.Project(map.Nodes[nodes[0]]));
            for (int i = 1; i < nodes.Count - (close ? 1 : 0); i++) builder.LineTo(viewport.Project(map.Nodes[nodes[i]]));
            if (close) builder.Close();
        }

        private static bool Closed(OsmWay way) => way.NodeIds.Count >= 4 && way.NodeIds[0] == way.NodeIds[way.NodeIds.Count - 1];

        private static int Structure(OsmWay way)
        {
            if (CityOsm.Tag(way, "tunnel") is string tunnel && tunnel != "no" || CityOsm.Tag(way, "covered") == "yes") return -1;
            return CityOsm.Tag(way, "bridge") is string bridge && bridge != "no" || CityOsm.Tag(way, "man_made") == "bridge" ? 1 : 0;
        }

        private static int Layer(OsmWay way) =>
            int.TryParse(CityOsm.Tag(way, "layer"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int layer) ? layer : Structure(way);
    }
}
