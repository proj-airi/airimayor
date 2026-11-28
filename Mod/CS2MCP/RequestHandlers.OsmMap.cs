using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Game.Net;
using SkiaSharp;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse RenderCityMap(BridgeRequest request)
        {
            if (!TryGetOsmBounds(request, out float4 bounds, out BridgeResponse error)) return error;
            OsmDocument map = ReadOsmDocument(bounds, new Dictionary<int, EdgeGeometry>());
            CityOsmFile.MarkMap(map);
            OsmTerrainHeightfield? heightfield = ReadOsmSurface(map, bounds);
            _ = Task.Run(() => CompleteCityMap(request, map, bounds, heightfield));
            return null;
        }

        private static void CompleteCityMap(BridgeRequest request, OsmDocument map, float4 bounds, OsmTerrainHeightfield? heightfield)
        {
            try
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                byte[] png = CityOsmRaster.Render(map, bounds, heightfield);
                request.CancellationToken.ThrowIfCancellationRequested();
                var files = new Dictionary<string, byte[]> { ["map.osm"] = Encoding.UTF8.GetBytes(map.Write()) };
                if (heightfield is OsmTerrainHeightfield grid)
                    files.Add("terrain.json", EncodeTerrain(grid));
                BridgeResponse response = BridgeResponse.Png(png, CityMapPreview(png));
                response.Files = files;
                request.Complete(response);
            }
            catch (OperationCanceledException)
            {
                request.Complete(BridgeResponse.Error(BridgeErrorKind.Conflict, "The map request was cancelled."));
            }
            catch (Exception exception)
            {
                request.Complete(BridgeResponse.Error(BridgeErrorKind.Internal, "The city map could not be drawn. " + exception.Message));
            }
        }

        private static byte[] EncodeTerrain(OsmTerrainHeightfield grid)
        {
            var layout = new
            {
                xMin = grid.XMin, zMin = grid.ZMin, xMax = grid.XMax, zMax = grid.ZMax,
                columns = grid.Columns, rows = grid.Rows, xStep = grid.XStep, zStep = grid.ZStep,
                sampleOrder = grid.SampleOrder, unit = "city meters",
            };
            return BridgeResponse.Json(new { layout, heights = grid.Heights }).Body;
        }

        private static byte[] CityMapPreview(byte[] png)
        {
            using var image = SKImage.FromEncodedData(png);
            float ratio = Math.Min(1f, 480f / Math.Max(image.Width, image.Height));
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(image.Width * ratio)),
                Math.Max(1, (int)Math.Round(image.Height * ratio)));
            using var surface = SKSurface.Create(info);
            surface.Canvas.DrawImage(image, SKRect.Create(info.Width, info.Height),
                new SKSamplingOptions(SKFilterMode.Linear));
            using var preview = surface.Snapshot();
            using var jpeg = preview.Encode(SKEncodedImageFormat.Jpeg, 85);
            return jpeg.ToArray();
        }
    }
}
