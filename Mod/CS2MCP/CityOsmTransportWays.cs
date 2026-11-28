namespace CS2MCP
{
    internal static class CityOsmTransportWays
    {
        internal static TypedNetworkKinds Kind(string value) => value switch
        {
            "footway" => TypedNetworkKinds.Footway,
            "rail" => TypedNetworkKinds.Rail,
            "tram" => TypedNetworkKinds.Tram,
            "subway" => TypedNetworkKinds.Subway,
            _ => TypedNetworkKinds.None,
        };

        internal static TypedNetworkKinds Kind(OsmWay way)
        {
            if (CityOsm.Tag(way, "area") == "yes") return TypedNetworkKinds.None;
            string highway = CityOsm.Tag(way, "highway");
            string railway = CityOsm.Tag(way, "railway");
            if (highway != null) return highway == "footway" && railway == null
                ? TypedNetworkKinds.Footway : TypedNetworkKinds.None;
            return railway == "footway" ? TypedNetworkKinds.None : Kind(railway);
        }

        internal static bool IsTransport(TypedNetworkKinds kind) =>
            kind == TypedNetworkKinds.Footway || kind == TypedNetworkKinds.Rail ||
            kind == TypedNetworkKinds.Tram || kind == TypedNetworkKinds.Subway;

        internal static void WriteTags(OsmWay way, TypedNetworkKinds kind)
        {
            if (kind == TypedNetworkKinds.Footway) way.Tags["highway"] = "footway";
            else way.Tags["railway"] = kind switch
            {
                TypedNetworkKinds.Rail => "rail",
                TypedNetworkKinds.Tram => "tram",
                TypedNetworkKinds.Subway => "subway",
                _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
            };
        }
    }
}
