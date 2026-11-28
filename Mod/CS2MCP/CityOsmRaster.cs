using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static partial class CityOsmRaster
    {
        private static readonly SKColor Land = SKColor.Parse("#f2efe9");
        private static readonly SKColor Water = SKColor.Parse("#aad3df");
        private static readonly SKColor Boundary = SKColor.Parse("#a555a6");
        private static readonly float[] LabelPositions = { 0.5f, 0.35f, 0.65f, 0.2f, 0.8f };
        private static readonly SKPoint[] AreaLabelOffsets =
        {
            new SKPoint(0.5f, 0.5f), new SKPoint(0.3f, 0.5f), new SKPoint(0.7f, 0.5f),
            new SKPoint(0.5f, 0.3f), new SKPoint(0.5f, 0.7f),
        };

        internal static byte[] Render(OsmDocument map, float4 bounds, OsmTerrainHeightfield? terrain)
        {
            var viewport = new Viewport(bounds);
            using var surface = SKSurface.Create(new SKImageInfo(viewport.Width, viewport.Height));
            var canvas = surface.Canvas;
            var labels = new List<Label>();
            var networks = new List<Network>();
            var bridgeOutlines = new List<(int Layer, SKPath Path)>();
            canvas.Clear(Land);
            if (terrain.HasValue) DrawTerrain(canvas, viewport, terrain.Value);
            try
            {
                foreach (var feature in Areas(map, viewport.Zoom).OrderBy(feature => feature.Style.Order))
                {
                    using var path = RingPath(map, viewport, feature.Rings);
                    if (!Visible(path, viewport) || path.Bounds.Width * path.Bounds.Height < 1f) continue;
                    DrawArea(canvas, path, feature.Style, viewport.Zoom);
                    string name = CityOsm.Tag(feature.Element, "name");
                    if (name == null || viewport.Zoom < feature.Style.LabelZoom) continue;
                    var rect = path.Bounds;
                    rect.Intersect(viewport.Frame);
                    foreach (var offset in AreaLabelOffsets)
                    {
                        var anchor = new SKPoint(rect.Left + offset.X * rect.Width, rect.Top + offset.Y * rect.Height);
                        if (!path.Contains(anchor.X, anchor.Y)) continue;
                        labels.Add(new Label(name, anchor, 0f, 0f, feature.Style.Order >= 5 ? 9 : 12, feature.Style.LabelColor));
                        break;
                    }
                }

                foreach (OsmWay way in map.Ways.Values)
                {
                    var style = NetworkStyle(way, viewport.Zoom);
                    bool outline = CityOsm.Tag(way, "man_made") == "bridge" && Closed(way) && viewport.Zoom >= 14;
                    if (!outline && style.Width <= 0f) continue;
                    if (style.Group == 0 && Closed(way) && CityOsm.Tag(way, "area") == "yes") continue;
                    var path = WayPath(map, viewport, way, outline);
                    if (!Visible(path, viewport))
                    {
                        path.Dispose();
                        continue;
                    }
                    int layer = Layer(way);
                    if (outline) bridgeOutlines.Add((layer, path));
                    else networks.Add(new Network(way, path, style, layer, Structure(way)));
                }
                DrawNetworks(canvas, viewport.Zoom, networks, bridgeOutlines);

                foreach (var relation in map.Relations.Values)
                {
                    if (CityOsm.Tag(relation, "boundary") != "administrative") continue;
                    using var path = RingPath(map, viewport, RelationRings(map, relation));
                    if (!Visible(path, viewport)) continue;
                    using var dash = SKPathEffect.CreateDash(new[] { 5f, 2f, 1f, 2f }, 0f);
                    using var stroke = Paint(Boundary.WithAlpha(190), 1.2f);
                    stroke.PathEffect = dash;
                    canvas.DrawPath(path, stroke);
                    string name = CityOsm.Tag(relation, "name");
                    var center = new SKPoint(path.Bounds.MidX, path.Bounds.MidY);
                    if (!string.IsNullOrWhiteSpace(name) && viewport.Zoom <= 17 && viewport.Frame.Contains(center.X, center.Y))
                        labels.Add(new Label(name, center, 0f, 0f, 15, Boundary));
                }

                DrawPoints(map, canvas, viewport, labels);
                AddNetworkLabels(canvas, viewport, networks, labels);
                DrawLabels(canvas, viewport, labels);
                DrawScaleBar(canvas, viewport);
                using var image = surface.Snapshot();
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                return encoded.ToArray();
            }
            finally
            {
                foreach (var network in networks) network.Path.Dispose();
                foreach (var outline in bridgeOutlines) outline.Path.Dispose();
            }
        }

        private static void DrawTerrain(SKCanvas canvas, Viewport viewport, OsmTerrainHeightfield terrain)
        {
            using var bitmap = new SKBitmap(terrain.Columns, terrain.Rows, SKColorType.Rgba8888, SKAlphaType.Opaque);
            var pixels = new SKColor[terrain.Heights.Length];
            for (int row = 0; row < terrain.Rows; row++)
            {
                int south = Math.Max(0, row - 1) * terrain.Columns;
                int north = Math.Min(terrain.Rows - 1, row + 1) * terrain.Columns;
                int current = row * terrain.Columns;
                int output = (terrain.Rows - row - 1) * terrain.Columns;
                for (int column = 0; column < terrain.Columns; column++)
                {
                    int west = Math.Max(0, column - 1);
                    int east = Math.Min(terrain.Columns - 1, column + 1);
                    float dx = (terrain.Heights[current + east] - terrain.Heights[current + west]) / (float)((east - west) * terrain.XStep);
                    float dz = (terrain.Heights[north + column] - terrain.Heights[south + column]) / (float)((north - south) / terrain.Columns * terrain.ZStep);
                    float light = math.dot(math.normalize(new float3(-dx, 1f, -dz)), new float3(-0.45f, 0.75f, 0.48f));
                    int shade = (int)math.round(20f * math.clamp(light - 0.75f, -1f, 0.25f));
                    pixels[output + column] = new SKColor((byte)(Land.Red + shade), (byte)(Land.Green + shade), (byte)(Land.Blue + shade));
                }
            }
            bitmap.Pixels = pixels;
            SKPoint northwest = viewport.Project(terrain.XMin, terrain.ZMax);
            SKPoint southeast = viewport.Project(terrain.XMax, terrain.ZMin);
            canvas.DrawBitmap(bitmap, new SKRect(northwest.X, northwest.Y, southeast.X, southeast.Y),
                new SKSamplingOptions(SKFilterMode.Linear));
        }

        private static void DrawArea(SKCanvas canvas, SKPath path, AreaStyle style, int zoom)
        {
            using var fill = Paint(style.Fill);
            canvas.DrawPath(path, fill);
            if (style.Outline.Alpha != 0 && zoom >= 15)
            {
                using var outline = Paint(style.Outline, 0.75f);
                canvas.DrawPath(path, outline);
            }
        }

        private static void DrawNetworks(SKCanvas canvas, int zoom, List<Network> networks, List<(int Layer, SKPath Path)> outlines)
        {
            var groups = networks.OrderBy(network => network.Style.Rank).ToLookup(network => (network.Layer, network.Structure));
            var bridges = outlines.ToLookup(outline => outline.Layer);
            var keys = groups.Select(group => group.Key).Concat(bridges.Select(group => (Layer: group.Key, Structure: 1))).Distinct();
            foreach (var key in keys.OrderBy(key => key.Layer).ThenBy(key => key.Structure))
            {
                var network = groups[key];
                if (key.Structure == 1)
                {
                    using var deck = Paint(SKColor.Parse("#ddd9d5"));
                    using var deckOutline = Paint(SKColor.Parse("#666666"), 1.25f);
                    foreach (var outline in bridges[key.Layer])
                    {
                        canvas.DrawPath(outline.Path, deck);
                        canvas.DrawPath(outline.Path, deckOutline);
                    }
                }
                foreach (var line in network.Where(line => line.Style.Group == 2))
                    DrawNetworkStroke(canvas, line, zoom, false);
                foreach (var line in network.Where(line => line.Style.Group != 2))
                    DrawNetworkStroke(canvas, line, zoom, true);
                foreach (var line in network.Where(line => line.Style.Group != 2))
                    DrawNetworkStroke(canvas, line, zoom, false);
            }
        }

        private static void DrawNetworkStroke(SKCanvas canvas, Network line, int zoom, bool casing)
        {
            var style = line.Style;
            bool bridge = line.Structure > 0;
            bool tunnel = line.Structure < 0;
            float width = style.Width;
            SKColor color = style.Fill;
            if (casing)
            {
                if (style.CasingWidth == 0f && !bridge && !tunnel) return;
                width += 2f * (bridge && zoom >= 13 ? Math.Max(style.CasingWidth, 0.7f) : style.CasingWidth);
                color = bridge && zoom >= 13 ? SKColors.Black : tunnel ? SKColor.Parse("#888888") : style.Casing;
            }
            else if (tunnel)
            {
                color = style.Group == 0 && style.Rank >= 7 ? Lighten(color, 0.24f) : color.WithAlpha(145);
            }
            using var paint = Paint(color, width);
            using var dash = tunnel && casing ? SKPathEffect.CreateDash(new[] { 4f, 2f }, 0f)
                : !casing && style.Dashed ? SKPathEffect.CreateDash(new[] { 2.8f, 2f }, 0f)
                : null;
            paint.PathEffect = dash;
            if (bridge || tunnel) paint.StrokeCap = SKStrokeCap.Butt;
            canvas.DrawPath(line.Path, paint);
            if (!casing && style.Group == 1 && zoom >= 15)
            {
                using var sleepers = SKPathEffect.CreateDash(new[] { 5f, 5f }, 0f);
                using var white = Paint(SKColors.White.WithAlpha(tunnel ? (byte)100 : (byte)230), 0.75f);
                white.PathEffect = sleepers;
                canvas.DrawPath(line.Path, white);
            }
        }

        private static void DrawPoints(OsmDocument map, SKCanvas canvas, Viewport viewport, List<Label> labels)
        {
            if (viewport.Zoom < 16) return;
            using var tree = Paint(SKColor.Parse("#8ebd78").WithAlpha(195));
            using var transport = Paint(SKColor.Parse("#0093ae"));
            using var furniture = Paint(SKColor.Parse("#786957"));
            foreach (OsmNode node in map.Nodes.Values)
            {
                bool stop = CityOsm.IsPlatform(node);
                var point = CityOsmStandalonePoints.Kind(node);
                if (!stop && point == OsmStandalonePoint.None) continue;
                var location = viewport.Project(node);
                if (!viewport.Frame.Contains(location.X, location.Y)) continue;
                if (point == OsmStandalonePoint.Tree)
                    canvas.DrawCircle(location, viewport.Zoom >= 19 ? 3.6f : 2.1f, tree);
                else if (stop && viewport.Zoom >= 17)
                    canvas.DrawRoundRect(new SKRect(location.X - 3f, location.Y - 3f, location.X + 3f, location.Y + 3f), 1f, 1f, transport);
                else if (viewport.Zoom >= 19 && point != OsmStandalonePoint.None)
                    canvas.DrawCircle(location, 1.75f, furniture);
                string name = CityOsm.Tag(node, "name");
                if (viewport.Zoom >= 18 && stop && !string.IsNullOrWhiteSpace(name))
                    labels.Add(new Label(name, new SKPoint(location.X, location.Y - 9f), 0f, 0f, 12, transport.Color));
            }
        }

        private static void AddNetworkLabels(SKCanvas canvas, Viewport viewport, List<Network> networks, List<Label> labels)
        {
            using var arrow = Paint(SKColor.Parse("#687587"), 0.85f);
            foreach (var network in networks)
            {
                string name = CityOsm.Tag(network.Way, "name");
                string oneway = CityOsm.Tag(network.Way, "oneway");
                bool named = viewport.Zoom >= network.Style.NameZoom && !string.IsNullOrWhiteSpace(name);
                bool direction = viewport.Zoom >= 16 && network.Structure >= 0 && (oneway == "yes" || oneway == "-1");
                if (!named && !direction) continue;
                using var measure = new SKPathMeasure(network.Path, false);
                if (named && measure.Length >= 42f)
                {
                    foreach (float fraction in LabelPositions)
                    {
                        float distance = measure.Length * fraction;
                        if (!measure.GetPositionAndTangent(distance, out var position, out var tangent) ||
                            !viewport.Frame.Contains(position.X, position.Y)) continue;
                        float angle = (float)Math.Atan2(tangent.Y, tangent.X);
                        if (tangent.X < 0f) angle += (float)Math.PI;
                        float available = 2f * Math.Min(distance, measure.Length - distance);
                        labels.Add(new Label(name, position, angle, available, 4 + network.Style.Rank, SKColor.Parse("#292929")));
                    }
                }
                if (direction && measure.Length > 58f && measure.GetPositionAndTangent(measure.Length * 0.7f, out var center, out var vector))
                {
                    if (oneway == "-1") vector = new SKPoint(-vector.X, -vector.Y);
                    var tip = new SKPoint(center.X + vector.X * 3.5f, center.Y + vector.Y * 3.5f);
                    canvas.DrawLine(center.X - vector.X * 3.5f, center.Y - vector.Y * 3.5f, tip.X, tip.Y, arrow);
                    canvas.DrawLine(center.X - vector.Y * 2.5f, center.Y + vector.X * 2.5f, tip.X, tip.Y, arrow);
                    canvas.DrawLine(center.X + vector.Y * 2.5f, center.Y - vector.X * 2.5f, tip.X, tip.Y, arrow);
                }
            }
        }

        private static void DrawLabels(SKCanvas canvas, Viewport viewport, List<Label> labels)
        {
            using var defaultFont = new SKFont(SKTypeface.Default, 11f);
            using var fill = Paint(SKColors.Black);
            using var halo = Paint(SKColors.White.WithAlpha(230), 2.5f);
            var occupied = new List<SKRect> { ScaleBox(viewport) };
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var label in labels.OrderByDescending(label => label.Priority).ThenByDescending(label => label.Available))
            {
                if (placed.Contains(label.Text)) continue;
                using var typeface = Fallback(defaultFont, label.Text);
                using var fallback = typeface == null ? null : new SKFont(typeface, defaultFont.Size);
                SKFont font = fallback ?? defaultFont;
                float width = font.MeasureText(label.Text);
                if (label.Available > 0f && width + 10f > label.Available) continue;
                float dx = Math.Abs((float)Math.Cos(label.Angle)) * width * 0.5f + Math.Abs((float)Math.Sin(label.Angle)) * 6f + 4f;
                float dy = Math.Abs((float)Math.Sin(label.Angle)) * width * 0.5f + Math.Abs((float)Math.Cos(label.Angle)) * 6f + 4f;
                var box = new SKRect(label.Position.X - dx, label.Position.Y - dy, label.Position.X + dx, label.Position.Y + dy);
                if (!viewport.Frame.Contains(box) || occupied.Any(box.IntersectsWith)) continue;
                int save = canvas.Save();
                canvas.Translate(label.Position.X, label.Position.Y);
                canvas.RotateRadians(label.Angle);
                fill.Color = label.Color;
                canvas.DrawText(label.Text, 0f, 3.5f, SKTextAlign.Center, font, halo);
                canvas.DrawText(label.Text, 0f, 3.5f, SKTextAlign.Center, font, fill);
                canvas.RestoreToCount(save);
                occupied.Add(box);
                placed.Add(label.Text);
            }
        }

        private static SKTypeface Fallback(SKFont font, string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                int codepoint = char.IsSurrogatePair(text, index) ? char.ConvertToUtf32(text, index++) : text[index];
                if (font.GetGlyph(codepoint) == 0) return SKFontManager.Default.MatchCharacter(codepoint);
            }
            return null;
        }

        private static SKRect ScaleBox(Viewport viewport) => new SKRect(5f, viewport.Height - 32f, 175f, viewport.Height - 5f);

        private static void DrawScaleBar(SKCanvas canvas, Viewport viewport)
        {
            double limit = 140d / viewport.Scale;
            double interval = Math.Pow(10d, Math.Floor(Math.Log10(limit)));
            double meters = interval * (limit >= 5d * interval ? 5d : limit >= 2d * interval ? 2d : 1d);
            string text = meters >= 1000d ? (meters / 1000d).ToString("G", CultureInfo.InvariantCulture) + " km"
                : meters.ToString("G", CultureInfo.InvariantCulture) + " m";
            const float left = 11f;
            float right = left + (float)(meters * viewport.Scale);
            float bottom = viewport.Height - 11f;
            using var font = new SKFont(SKTypeface.Default, 10f);
            using var box = Paint(SKColors.White.WithAlpha(218));
            using var stroke = Paint(SKColor.Parse("#424242"), 1.2f);
            using var label = Paint(stroke.Color);
            canvas.DrawRect(new SKRect(left - 4f, bottom - 19f, Math.Max(right, left + font.MeasureText(text)) + 4f, bottom + 4f), box);
            canvas.DrawText(text, left, bottom - 8f, SKTextAlign.Left, font, label);
            canvas.DrawLine(left, bottom, right, bottom, stroke);
            canvas.DrawLine(left, bottom, left, bottom - 4f, stroke);
            canvas.DrawLine(right, bottom, right, bottom - 4f, stroke);
        }

        private static SKPaint Paint(SKColor color, float width = 0f) => new SKPaint
        {
            Color = color, IsAntialias = true, StrokeWidth = width,
            Style = width > 0f ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
            StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round,
        };

        private static SKColor Lighten(SKColor color, float amount) => new SKColor(
            (byte)math.lerp(color.Red, 255, amount), (byte)math.lerp(color.Green, 255, amount), (byte)math.lerp(color.Blue, 255, amount));

        private static bool Visible(SKPath path, Viewport viewport)
        {
            var bounds = path.Bounds;
            bounds.Inflate(24f, 24f);
            return !path.IsEmpty && bounds.IntersectsWith(viewport.Frame);
        }

        private readonly struct Viewport
        {
            internal Viewport(float4 bounds)
            {
                double width = (double)bounds.z - bounds.x;
                double height = (double)bounds.w - bounds.y;
                Scale = Math.Min(1200d / width, 880d / height);
                Width = Math.Max(256, (int)Math.Ceiling(width * Scale));
                Height = Math.Max(200, (int)Math.Ceiling(height * Scale));
                CenterX = ((double)bounds.x + bounds.z) * 0.5d;
                CenterZ = ((double)bounds.y + bounds.w) * 0.5d;
                Zoom = math.clamp((int)Math.Floor(Math.Log(156543.03392804097d * Scale, 2d)), 1, 20);
            }

            internal double Scale { get; }
            private double CenterX { get; }
            private double CenterZ { get; }
            internal int Width { get; }
            internal int Height { get; }
            internal int Zoom { get; }
            internal SKRect Frame => new SKRect(0, 0, Width, Height);
            internal SKPoint Project(double x, double z) =>
                new SKPoint((float)(Width * 0.5d + (x - CenterX) * Scale), (float)(Height * 0.5d - (z - CenterZ) * Scale));
            internal SKPoint Project(OsmNode node) => Project(node.Longitude * CityOsm.LongitudeMeters, node.Latitude * CityOsm.LatitudeMeters);
        }

        private readonly struct Network
        {
            internal Network(OsmWay way, SKPath path, StrokeStyle style, int layer, int structure)
            {
                Way = way;
                Path = path;
                Style = style;
                Layer = layer;
                Structure = structure;
            }

            internal OsmWay Way { get; }
            internal SKPath Path { get; }
            internal StrokeStyle Style { get; }
            internal int Layer { get; }
            internal int Structure { get; }
        }

        private readonly struct Label
        {
            internal Label(string text, SKPoint position, float angle, float available, int priority, SKColor color)
            {
                Text = text;
                Position = position;
                Angle = angle;
                Available = available;
                Priority = priority;
                Color = color;
            }

            internal string Text { get; }
            internal SKPoint Position { get; }
            internal float Angle { get; }
            internal float Available { get; }
            internal int Priority { get; }
            internal SKColor Color { get; }
        }
    }
}
