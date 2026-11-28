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
            string content = map.Write();
            BridgeResponse response = BridgeResponse.Json(new { view = "map", content });
            response.Files = new Dictionary<string, byte[]> { ["map.osm"] = Encoding.UTF8.GetBytes(content) };
            return response;
        }
    }
}
