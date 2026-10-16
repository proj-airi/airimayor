using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using airimayor.Host;
using Game.Simulation;
using Newtonsoft.Json;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// Undistorted city map rasterized from native ECS geometry in game
    /// meters. Optional map range matches map_text; names stay in map_text.
    /// Citywide by default; a requested extent clips directly.
    /// Read-only: runs on the simulation thread like other perception tools.
    /// Shares the vision switch with screenshot (gated in AgentToolSurface).
    /// </summary>
    public sealed partial class RequestHandlers
    {

        private BridgeResponse MapImage(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse cityError))
            {
                return cityError;
            }

            bool hasBounds = request.Query.ContainsKey("x") || request.Query.ContainsKey("z")
                || request.Query.ContainsKey("radius") || request.Query.ContainsKey("xMin")
                || request.Query.ContainsKey("zMin") || request.Query.ContainsKey("xMax")
                || request.Query.ContainsKey("zMax");
            float xMin = 0f;
            float zMin = 0f;
            float xMax = 0f;
            float zMax = 0f;
            if (hasBounds
                && !TryGetWorldBounds(request, out xMin, out zMin, out xMax, out zMax, out BridgeResponse boundsError))
            {
                return boundsError;
            }

            var strokes = new List<MapStroke>();
            var fills = new List<MapPolygon>();
            var collectTimer = Stopwatch.StartNew();
            if (!TryCollectNativeMapGeometry(
                    hasBounds ? xMin : (float?)null,
                    hasBounds ? zMin : (float?)null,
                    hasBounds ? xMax : (float?)null,
                    hasBounds ? zMax : (float?)null,
                    strokes, fills, out string collectError))
            {
                return BridgeResponse.Error(BridgeErrorKind.Internal,
                    "map geometry collection failed"
                    + (string.IsNullOrEmpty(collectError) ? "" : ": " + collectError));
            }
            collectTimer.Stop();
            MapLayering.Result layering = AssignRoadLayers(strokes);
            strokes = MapStrokeJoin.Join(strokes);
            MapFrame frame = hasBounds
                ? MapFrame.FromBounds(xMin, zMin, xMax, zMax)
                : MapFrame.FromData(strokes, fills) ?? MapFrame.World();
            MapScale mapScale = MapImagePaint.ScaleOf(frame, MapImagePaint.WidthPx);
            AgentTimeline.Info("map-image", $"map_image: {strokes.Count} strokes, {fills.Count} footprints, {mapScale}" +
                (hasBounds ? $" (extent {xMin},{zMin} to {xMax},{zMax})" : " (citywide)") +
                $" in {collectTimer.ElapsedMilliseconds}ms");

            Texture2D texture = null;
            try
            {
                texture = ToTexture(MapImagePaint.Rasterize(strokes, fills, frame, BuildWaterSampler()));
                byte[] png = ImageConversion.EncodeToPNG(texture);
                if (png == null || png.Length == 0)
                {
                    return BridgeResponse.Error(BridgeErrorKind.Internal, "map PNG encode failed");
                }
                BridgeResponse response = BridgeResponse.Png(png, ToolPreview.EncodeThumbnail(texture));
                response.Files = MapImageDiagnostics(strokes, fills, frame, mapScale, layering);
                return response;
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(BridgeErrorKind.Internal,
                    $"map render failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }
        }

        /// <summary>
        /// Grade-separation solver still assigns integer layers inside a
        /// grade (stacked ramps). Tunnel / ground / bridge order is a
        /// separate OSM Carto pass.
        /// </summary>
        private static MapLayering.Result AssignRoadLayers(List<MapStroke> strokes)
        {
            var features = new List<MapLayering.Feature>(strokes.Count);
            foreach (MapStroke stroke in strokes)
            {
                features.Add(new MapLayering.Feature
                {
                    X = stroke.X,
                    Y = stroke.Y,
                    Elev = stroke.HasElev ? stroke.Elev : 0.0,
                    HasElev = stroke.HasElev,
                });
            }
            MapLayering.Result result = MapLayering.ComputeLayers(features);
            for (int i = 0; i < strokes.Count && i < result.Layers.Length; i++)
            {
                strokes[i].Layer = result.Layers[i];
            }
            if (!result.Converged)
            {
                AgentTimeline.Warn("map-image", "road layer constraints did not converge; " +
                    "deep stacks are clamped at the top layer");
            }
            if (result.SkippedCells > 0)
            {
                AgentTimeline.Warn("map-image", $"skipped {result.SkippedCells} overfull hash cells; " +
                    "nearby overpasses may draw flat");
            }
            return result;
        }

        private static Texture2D ToTexture(MapImageBuffer buffer)
        {
            var texture = new Texture2D(buffer.Width, buffer.Height, TextureFormat.RGB24, false);
            var colors = new Color[buffer.Pixels.Length];
            for (int row = 0; row < buffer.Height; row++)
            {
                int source = row * buffer.Width;
                int target = (buffer.Height - 1 - row) * buffer.Width;
                for (int col = 0; col < buffer.Width; col++)
                {
                    MapRgb pixel = buffer.Pixels[source + col];
                    colors[target + col] = new Color(pixel.R / 255f, pixel.G / 255f, pixel.B / 255f);
                }
            }
            texture.SetPixels(colors);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// World-space water test built on the simulation thread. Null when
        /// unavailable: the map stays land-only rather than failing.
        /// </summary>
        private Func<double, double, bool> BuildWaterSampler()
        {
            try
            {
                WaterSystem water = World.GetOrCreateSystemManaged<WaterSystem>();
                WaterSurfaceData<SurfaceWater> surface = water.GetSurfaceData(out JobHandle deps);
                deps.Complete();
                return (double worldX, double worldY) =>
                {
                    try
                    {
                        var position = new float3((float)worldX, 0f, (float)worldY);
                        return WaterUtils.SampleDepth(ref surface, position) > 0.05f;
                    }
                    catch
                    {
                        return false;
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Diagnostic overlay for the next live run: game-meter GeoJSON of
        /// what the painter saw, to compare with Carto's Network export. The
        /// session record keeps these beside the call's PNG.
        /// </summary>
        private static Dictionary<string, byte[]> MapImageDiagnostics(
            List<MapStroke> strokes, List<MapPolygon> fills,
            MapFrame frame, MapScale mapScale, MapLayering.Result layering)
        {
            return new Dictionary<string, byte[]>
            {
                ["meta.json"] = Encoding.UTF8.GetBytes(BuildMapMeta(frame, mapScale, strokes, fills, layering)),
                ["source.geojson"] = Encoding.UTF8.GetBytes(BuildMapSourceGeoJson(strokes, fills)),
            };
        }

        private static string BuildMapMeta(
            MapFrame frame, MapScale mapScale, List<MapStroke> strokes, List<MapPolygon> fills,
            MapLayering.Result layering)
        {
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            using (var json = new JsonTextWriter(writer))
            {
                json.WriteStartObject();
                json.WritePropertyName("minX"); json.WriteValue(frame.MinX);
                json.WritePropertyName("minY"); json.WriteValue(frame.MinY);
                json.WritePropertyName("maxX"); json.WriteValue(frame.MaxX);
                json.WritePropertyName("maxY"); json.WriteValue(frame.MaxY);
                json.WritePropertyName("clip"); json.WriteValue(frame.Clip);
                json.WritePropertyName("mapScale"); json.WriteValue(mapScale.ToString());
                json.WritePropertyName("strokes"); json.WriteValue(strokes.Count);
                json.WritePropertyName("fills"); json.WriteValue(fills.Count);
                json.WritePropertyName("converged"); json.WriteValue(layering.Converged);
                json.WritePropertyName("constraints"); json.WriteValue(layering.Constraints);
                json.WritePropertyName("skippedCells"); json.WriteValue(layering.SkippedCells);
                json.WriteEndObject();
                return writer.ToString();
            }
        }

        private static string BuildMapSourceGeoJson(
            List<MapStroke> strokes, List<MapPolygon> fills)
        {
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            using (var json = new JsonTextWriter(writer))
            {
                json.WriteStartObject();
                json.WritePropertyName("type"); json.WriteValue("FeatureCollection");
                json.WritePropertyName("features");
                json.WriteStartArray();
                foreach (MapStroke stroke in strokes)
                {
                    if (stroke.X.Count < 2)
                    {
                        continue;
                    }
                    json.WriteStartObject();
                    json.WritePropertyName("type"); json.WriteValue("Feature");
                    json.WritePropertyName("properties");
                    json.WriteStartObject();
                    json.WritePropertyName("kind"); json.WriteValue("stroke");
                    json.WritePropertyName("style"); json.WriteValue(stroke.Style.ToString());
                    json.WritePropertyName("grade"); json.WriteValue(stroke.Grade.ToString());
                    json.WritePropertyName("layer"); json.WriteValue(stroke.Layer);
                    json.WritePropertyName("widthM"); json.WriteValue(stroke.WidthM);
                    json.WritePropertyName("startNode"); json.WriteValue(stroke.StartNode);
                    json.WritePropertyName("endNode"); json.WriteValue(stroke.EndNode);
                    json.WritePropertyName("elev");
                    if (stroke.HasElev)
                    {
                        json.WriteValue(stroke.Elev);
                    }
                    else
                    {
                        json.WriteNull();
                    }
                    json.WriteEndObject();
                    json.WritePropertyName("geometry");
                    json.WriteStartObject();
                    json.WritePropertyName("type"); json.WriteValue("LineString");
                    json.WritePropertyName("coordinates");
                    json.WriteStartArray();
                    for (int i = 0; i < stroke.X.Count; i++)
                    {
                        WriteCoord(json, stroke.X[i], stroke.Y[i]);
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                foreach (MapPolygon polygon in fills)
                {
                    if (polygon.X.Count < 3)
                    {
                        continue;
                    }
                    json.WriteStartObject();
                    json.WritePropertyName("type"); json.WriteValue("Feature");
                    json.WritePropertyName("properties");
                    json.WriteStartObject();
                    json.WritePropertyName("kind"); json.WriteValue("fill");
                    json.WritePropertyName("fillKind"); json.WriteValue(polygon.Kind.ToString());
                    json.WriteEndObject();
                    json.WritePropertyName("geometry");
                    json.WriteStartObject();
                    json.WritePropertyName("type"); json.WriteValue("Polygon");
                    json.WritePropertyName("coordinates");
                    json.WriteStartArray();
                    json.WriteStartArray();
                    for (int i = 0; i < polygon.X.Count; i++)
                    {
                        WriteCoord(json, polygon.X[i], polygon.Y[i]);
                    }
                    WriteCoord(json, polygon.X[0], polygon.Y[0]);
                    json.WriteEndArray();
                    json.WriteEndArray();
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
                return writer.ToString();
            }
        }

        private static void WriteCoord(JsonTextWriter json, double x, double y)
        {
            json.WriteStartArray();
            json.WriteValue(x);
            json.WriteValue(y);
            json.WriteEndArray();
        }
    }
}
