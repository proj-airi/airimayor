using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmTransportWayTests
    {
        [Theory]
        [InlineData("highway", "footway")]
        [InlineData("railway", "rail")]
        [InlineData("railway", "tram")]
        [InlineData("railway", "subway")]
        public void Standalone_transport_uses_standard_linear_way_tags(string key, string value)
        {
            var kind = CityOsmTransportWays.Kind(value);
            var start = new CityOsmJunction { Index = 23, Entity = "23:1", Position = float3.zero };
            var end = new CityOsmJunction { Index = 24, Entity = "24:1", Position = new float3(24, 0, 0) };
            var document = CityOsm.Export(new[]
            {
                new CityOsmRoadSource
                {
                    Index = 25, Entity = "25:1", Prefab = "Network asset", Transport = kind,
                    Start = start, End = end,
                    Curve = new RoadPath(start.Position, new float3(8, 0, 0), new float3(16, 0, 0), end.Position),
                },
            }, Array.Empty<CityOsmBuildingSource>());
            var parsed = OsmDocument.Read(document.Write());
            var way = Assert.Single(parsed.Ways.Values);
            Assert.Equal(value, way.Tags[key]);
            Assert.Equal(kind, CityOsmTransportWays.Kind(way));
            var imported = Assert.Single(CityOsm.Import(parsed).Roads);
            var section = Assert.Single(imported.Sections);
            Assert.Equal(CityOsm.NativeId(start.Index), section.StartNode);
            Assert.Equal(CityOsm.NativeId(end.Index), section.EndNode);
        }
    }
}
