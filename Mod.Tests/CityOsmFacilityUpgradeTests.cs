using Unity.Mathematics;
using Xunit;

namespace CS2MCP
{
    public sealed class CityOsmFacilityUpgradeTests
    {
        private const string ExistingUpgrade = "School Library";
        private const string NewUpgrade = "School Gym & Pool";
        private static readonly long Facility = CityOsm.NativeId(42);

        [Fact]
        public void Facility_upgrade_round_trips_as_an_addition_to_the_existing_building()
        {
            OsmDocument before = SchoolDistrict();
            var proposal = before.Copy();
            proposal.Ways[Facility].Tags.Add(CityOsmFacilityUpgrades.Tag(NewUpgrade), "yes");

            foreach (string content in new[] { proposal.Write(), OsmChange.Between(before, proposal).Write() })
            {
                var after = before.Edit(content);
                var changes = CityOsmEdit.Changes(CityOsm.Import(before), CityOsm.Import(after), new float4(-40, -40, 40, 40));
                var change = Assert.Single(changes);
                var original = Assert.IsType<OsmWay>(change.Original);
                var edited = Assert.IsType<OsmWay>(change.Edited);
                Assert.Equal(Facility, original.Id);
                Assert.Equal("yes", edited.Tags[CityOsmFacilityUpgrades.Tag(ExistingUpgrade)]);
                Assert.Equal(NewUpgrade, Assert.Single(CityOsmFacilityUpgrades.Additions(before, after, original, edited)));
            }
        }

        [Fact]
        public void Installation_preserves_the_facility_lot_and_existing_upgrades()
        {
            OsmDocument before = SchoolDistrict();
            OsmWay original = before.Ways[Facility];
            var proposal = before.Copy();
            OsmWay edited = proposal.Ways[Facility];
            edited.Tags.Add(CityOsmFacilityUpgrades.Tag(NewUpgrade), "yes");
            var corner = proposal.Nodes[edited.NodeIds[0]];
            double longitude = corner.Longitude;
            corner.Longitude += 0.00001;
            Assert.Throws<FormatException>(() => CityOsmFacilityUpgrades.Additions(before, proposal, original, edited));

            corner.Longitude = longitude;
            edited.Tags.Remove(CityOsmFacilityUpgrades.Tag(ExistingUpgrade));
            Assert.Throws<FormatException>(() => CityOsmFacilityUpgrades.Additions(before, proposal, original, edited));
        }

        private static OsmDocument SchoolDistrict() => CityOsm.Export(Array.Empty<CityOsmRoadSource>(), new[]
        {
            new CityOsmBuildingSource
            {
                Index = 42, Entity = "42:1", Prefab = "School", Building = "school", Rotation = quaternion.identity,
                LotSize = new float2(16, 16), FacilityUpgrades = new[] { ExistingUpgrade },
            },
        });
    }
}
