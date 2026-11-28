using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Prefabs;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static class CityOsmTransit
    {
        internal const string RequiredBounds = "airimayor:required_bounds";

        internal static bool IsRoute(OsmElement element) => element is OsmRelation && CityOsm.Tag(element, "type") == "route";
        internal static bool StationOwned(OsmNode node) => CityOsm.Tag(node, "airimayor:station") != null;

        internal static bool RemovedWithRoute(OsmNode node, OsmDocument before, OsmDocument after)
        {
            if (CityOsm.Tag(node, "airimayor:waypoint") != "yes") return false;
            var owners = before.Relations.Values.Where(route => IsRoute(route) &&
                route.Members.Contains(new OsmMember(OsmKind.Node, node.Id, "via"))).ToArray();
            return owners.Length != 0 && owners.All(route => !after.Relations.ContainsKey(route.Id));
        }

        internal static bool TryReadTransportType(string value, out TransportType type) =>
            Enum.TryParse(value, true, out type) && type != TransportType.None && type != TransportType.Count
            && string.Equals(value, Enum.GetName(typeof(TransportType), type), StringComparison.OrdinalIgnoreCase);

        internal static float4 ReadBounds(float4 bounds) =>
            new float4(math.floor(bounds.xy) - 1, math.ceil(bounds.zw) + 1);

        internal static string Extent(float4 bounds)
        {
            float4 read = ReadBounds(bounds);
            return string.Join(",", new[] { read.x, read.y, read.z, read.w }
                .Select(value => value.ToString("0", CultureInfo.InvariantCulture)));
        }

        internal static void CheckScope(OsmRelation original)
        {
            string required = CityOsm.Tag(original, RequiredBounds);
            if (required != null)
                throw new FormatException($"Route {original.Id} extends outside this district. Read bounds {required} before changing the route.");
        }

        internal static long[] Stops(OsmRelation route, OsmDocument before, OsmDocument after, float4 bounds)
        {
            var stops = new List<long>();
            var seen = new HashSet<long>();
            foreach (OsmMember member in route.Members)
            {
                if (member.Kind != OsmKind.Node || member.Role != "platform" || !after.Nodes.TryGetValue(member.Id, out OsmNode node)
                    || !CityOsm.IsPlatform(node) || !seen.Add(member.Id))
                    throw new FormatException($"Route {route.Id} needs distinct platform nodes in boarding order.");
                if (!before.Nodes.TryGetValue(member.Id, out OsmNode original) || !CityOsm.IsPlatform(original)
                    || !CityOsmEdit.SameNodePosition(original, node))
                    throw new FormatException($"Build or move platform {member.Id} first, then use its returned node on route {route.Id}.");
                float2 position = CityOsm.Position(node).xz;
                if (math.any(position < bounds.xy) || math.any(position > bounds.zw))
                    throw new FormatException($"Read a district enclosing platform {member.Id} before editing route {route.Id}.");
                stops.Add(member.Id);
            }
            if (stops.Count < 2) throw new FormatException($"Route {route.Id} needs at least two distinct passenger platforms.");
            return stops.ToArray();
        }
    }
}
