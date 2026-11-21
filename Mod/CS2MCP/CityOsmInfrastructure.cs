using System;

namespace CS2MCP
{
    internal static class CityOsmInfrastructure
    {
        public static TypedNetworkKinds UtilityKinds(OsmWay way)
        {
            string location = CityOsm.Tag(way, "location");
            string power = CityOsm.Tag(way, "power");
            string pipe = CityOsm.Tag(way, "man_made");
            string contents = CityOsm.Tag(way, "substance");
            if ((location != null && location != "underground") || (power != null && power != "cable")
                || (pipe != null && pipe != "pipeline") || (pipe == null && contents != null))
                throw new FormatException($"Way {way.Id} needs an underground cable or water or sewage pipe.");
            var kinds = power == "cable" ? TypedNetworkKinds.LowVoltage : TypedNetworkKinds.None;
            if (pipe == "pipeline")
            {
                foreach (string substance in (contents ?? "").Split(';'))
                {
                    switch (substance)
                    {
                        case "water": kinds |= TypedNetworkKinds.Water; break;
                        case "sewage": kinds |= TypedNetworkKinds.Sewage; break;
                        default: throw new FormatException($"Pipe {way.Id} needs substance=water or sewage.");
                    }
                }
            }
            return kinds;
        }

        public static string PlatformMode(OsmNode node)
        {
            string platform = CityOsm.Tag(node, "public_transport");
            string highway = CityOsm.Tag(node, "highway");
            if ((platform != null && platform != "platform") || (highway != null && highway != "bus_stop") || !CityOsm.IsPlatform(node))
                throw new FormatException($"Stop {node.Id} needs a single bus, tram or taxi platform.");
            string selected = highway == "bus_stop" ? "bus" : null;
            foreach (string mode in new[] { "bus", "tram", "taxi" })
            {
                string value = CityOsm.Tag(node, mode);
                if (value == null) continue;
                if (value != "yes" || (selected != null && selected != mode))
                    throw new FormatException($"Stop {node.Id} needs a single bus, tram or taxi platform.");
                selected = mode;
            }
            return selected ?? throw new FormatException($"Specify bus=yes, tram=yes or taxi=yes on platform {node.Id}.");
        }
    }
}
