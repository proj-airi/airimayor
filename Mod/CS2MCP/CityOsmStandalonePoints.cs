namespace CS2MCP
{
    internal enum OsmStandalonePoint
    {
        None,
        Tree,
        StreetLamp,
    }

    internal static class CityOsmStandalonePoints
    {
        internal static OsmStandalonePoint Kind(OsmNode node)
        {
            bool tree = CityOsm.Tag(node, "natural") == "tree";
            bool lamp = CityOsm.Tag(node, "highway") == "street_lamp";
            if (tree == lamp) return OsmStandalonePoint.None;
            return tree ? OsmStandalonePoint.Tree : OsmStandalonePoint.StreetLamp;
        }

        internal static bool IsPoint(OsmNode node) =>
            Kind(node) != OsmStandalonePoint.None || CityOsm.IsPlatform(node);

        internal static void WriteTags(OsmNode node, OsmStandalonePoint kind)
        {
            if (kind == OsmStandalonePoint.Tree) node.Tags["natural"] = "tree";
            else if (kind == OsmStandalonePoint.StreetLamp) node.Tags["highway"] = "street_lamp";
            else throw new System.ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
