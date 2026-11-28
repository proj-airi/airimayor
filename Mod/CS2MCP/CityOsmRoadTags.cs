using System.Collections.Generic;
using System.Linq;

namespace CS2MCP
{
    internal static class CityOsmRoadTags
    {
        internal static (string Highway, string Service, string Surface) Classify(string roadClass, string prefabId)
        {
            string highway = roadClass switch
            {
                RoadFactsMath.RoadClassHighway => "motorway",
                RoadFactsMath.RoadClassLarge => "primary",
                RoadFactsMath.RoadClassMedium => "secondary",
                RoadFactsMath.RoadClassMinor => "residential",
                _ => "road",
            };
            return prefabId switch
            {
                "4216f9154e203344c956eaa8a0dd0562" or "c77f3872328fd714ba67db542795a1ca"
                    or "550188bbce586f44980f1fc514adb19e" or "b66417a444723b74eb7442a9c2100708"
                    or "5bf90b42984360e4ca3fcdb40f54c357" or "7107bc1fd2e38f148b0c01aadb044101" => ("service", "alley", null),
                "3efb5bce5d3c09f479d866d8fd09e60d" or "e1c349612035f714095477235e64851"
                    or "3474512734a0e954098bfc1de5529c93" or "794ea8ffb80091f4187ef9ce865e23e5"
                    or "2e8f7e9c513d25a4cb2b16151c071a67" or "13a883c5b5a5f6b4480afb70c32d14fe"
                    or "00e2e59a8da1d924c862cce33dbaab88" => ("service", null, null),
                "66c08f6ab6d332b4a8757e848d1ba753" or "48c9b68071119ca49ab92cd2a32cd808" => ("pedestrian", null, null),
                "8eb0c2c920cd0424c9a033a1e57d173f" or "b6deb8db00c63464886f17115a1354b6" => (highway, null, "gravel"),
                _ => (highway, null, null),
            };
        }

        internal static bool IsLinkJunction(int aggregate, int lanes, IEnumerable<(int Aggregate, int Lanes, bool Enters, bool Leaves)> neighbors)
        {
            return neighbors.Where(road => road.Aggregate != 0 && road.Aggregate != aggregate && road.Lanes > lanes)
                .GroupBy(road => road.Aggregate)
                .Any(roads => roads.Count() > 1 && roads.Any(road => road.Enters) && roads.Any(road => road.Leaves));
        }
    }
}
