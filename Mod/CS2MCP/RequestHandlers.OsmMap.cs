using System.Collections.Generic;
using System.Text;
using Game.Net;
using Unity.Mathematics;

namespace CS2MCP
{
    public sealed partial class RequestHandlers
    {
        private BridgeResponse ReadOsmMap(float4 bounds)
        {
            OsmDocument map = ReadOsmDocument(bounds, new Dictionary<int, EdgeGeometry>());
            CityOsmFile.MarkMap(map);
            OsmTerrainHeightfield? heightfield = ReadOsmSurface(map, bounds);
            string content = map.Write();
            var files = new Dictionary<string, byte[]> { ["map.osm"] = Encoding.UTF8.GetBytes(content) };
            object terrain = null;
            if (heightfield is OsmTerrainHeightfield grid)
            {
                const string name = "terrain.json";
                var layout = new
                {
                    xMin = grid.XMin, zMin = grid.ZMin, xMax = grid.XMax, zMax = grid.ZMax,
                    columns = grid.Columns, rows = grid.Rows, xStep = grid.XStep, zStep = grid.ZStep,
                    sampleOrder = grid.SampleOrder, unit = "city meters",
                };
                terrain = new { file = name, layout };
                files.Add(name, BridgeResponse.Json(new { layout, heights = grid.Heights }).Body);
            }
            BridgeResponse response = BridgeResponse.Json(new { view = "map", content, terrain });
            response.Files = files;
            return response;
        }
    }
}
