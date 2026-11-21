using CS2MCP;
using Game.Prefabs;
using Unity.Mathematics;
using Xunit;

namespace airimayor.Tests
{
    public class OsmRoadUpgradeTests
    {
        [Fact]
        public void Editing_one_side_preserves_other_upgrades_and_unmapped_native_flags()
        {
            var original = new CompositionFlags(CompositionFlags.General.Lighting, CompositionFlags.Side.PrimaryTrack,
                CompositionFlags.Side.ParkingSpaces);
            var road = new CityOsmRoadSource
            {
                Index = 14, Entity = "14:3", Prefab = "Small Road", Highway = "residential",
                Start = new CityOsmJunction { Index = 1, Entity = "1:2", Position = float3.zero },
                End = new CityOsmJunction { Index = 2, Entity = "2:2", Position = new float3(50, 0, 0) },
                Curve = RoadPath.Straight(float3.zero, new float3(50, 0, 0)), UpgradeTags = OsmRoadUpgrades.Export(original)
            };
            var district = CityOsm.Export(new[] { road }, System.Array.Empty<CityOsmBuildingSource>());
            var way = OsmDocument.Read(district.Write()).Ways[15];
            way.Tags["airimayor:upgrade:trees:left"] = "yes";
            way.Tags.Remove("airimayor:upgrade:parking:right");

            var result = OsmRoadUpgrades.Read(way, original);

            Assert.Equal(CompositionFlags.Side.PrimaryTrack | CompositionFlags.Side.SecondaryBeautification, result.m_Left);
            Assert.Equal(default, result.m_Right);
            Assert.Equal(CompositionFlags.General.Lighting, result.m_General);
            Assert.Equal("yes", OsmRoadUpgrades.Export(result)["airimayor:upgrade:trees:left"]);
        }
    }
}
