namespace CS2MCP
{
    internal enum OsmStandalonePoint
    {
        None,
        Tree,
        StreetLamp,
        Bench,
        WasteBasket,
    }

    internal static class CityOsmStandalonePoints
    {
        private const string CampgroundBench01 = "dc8d314b0e924bb41b1084ec3bed09b3";
        private const string CampgroundBench02 = "fc5ffc8fb618e0045974f880b5ebd72d";
        private const string GardenBench01 = "d4731877cfc14ec48b59d763dce54732";
        private const string GardenBench02 = "b7e1a81f389a5b44f87bfb05167cd5f3";
        private const string GardenBench03 = "f90b0eedaea0f87498842445a7e9d7c1";
        private const string GardenBench04 = "37c2b903b9a849c40b583328cfda5188";
        private const string PierBench01 = "c17428ca1eb6ed24cb2934cc2d1db7eb";
        private const string PierBench02 = "1fb07bdc3f823b54191c4e858bc55aa3";
        private const string SportParkBench01 = "74bba35ea3f662f4b89660f5af71f0f2";
        private const string SportParkBench02 = "8d6ef4e700c1efc4a87ace9919624027";
        private const string Trashbin01 = "26d7f7c0799717e4bbba4b9b34dce12a";
        private const string Trashbin02 = "4192f77bc9bd9f647a290e97fdb74c32";
        private const string Trashbin03 = "7f2f52b2106aaed41805451f5cee6a54";
        private const string Trashbin04 = "964b562d3f3b4ce4c9444215ec792e81";

        private static readonly (OsmStandalonePoint Kind, string Key, string Value)[] kPointTags =
        {
            (OsmStandalonePoint.Tree, "natural", "tree"),
            (OsmStandalonePoint.StreetLamp, "highway", "street_lamp"),
            (OsmStandalonePoint.Bench, "amenity", "bench"),
            (OsmStandalonePoint.WasteBasket, "amenity", "waste_basket"),
        };

        internal static OsmStandalonePoint Kind(OsmNode node)
        {
            OsmStandalonePoint match = OsmStandalonePoint.None;
            foreach (var (kind, key, value) in kPointTags)
            {
                if (CityOsm.Tag(node, key) != value) continue;
                if (match != OsmStandalonePoint.None) return OsmStandalonePoint.None;
                match = kind;
            }
            return match;
        }

        internal static OsmStandalonePoint NativeFurniture(string prefabId) => prefabId switch
        {
            CampgroundBench01 or CampgroundBench02 or GardenBench01 or GardenBench02
                or GardenBench03 or GardenBench04 or PierBench01 or PierBench02
                or SportParkBench01 or SportParkBench02 => OsmStandalonePoint.Bench,
            Trashbin01 or Trashbin02 or Trashbin03 or Trashbin04 => OsmStandalonePoint.WasteBasket,
            _ => OsmStandalonePoint.None,
        };

        internal static bool IsPoint(OsmNode node) =>
            Kind(node) != OsmStandalonePoint.None || CityOsm.IsPlatform(node);

        internal static (string Key, string Value) Tag(OsmStandalonePoint kind)
        {
            foreach (var (mapped, key, value) in kPointTags)
                if (mapped == kind) return (key, value);
            throw new System.ArgumentOutOfRangeException(nameof(kind));
        }

        internal static void WriteTags(OsmNode node, OsmStandalonePoint kind)
        {
            var (key, value) = Tag(kind);
            node.Tags[key] = value;
        }
    }
}
