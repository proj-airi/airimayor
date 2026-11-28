using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static class CityOsmAdministrativeBoundaries
    {
        internal static bool IsBoundary(OsmRelation relation) =>
            CityOsm.Tag(relation, "type") == "boundary" && CityOsm.Tag(relation, "boundary") == "administrative";

        internal static IEnumerable<long> OuterWays(OsmDocument document) => document.Relations.Values
            .Where(IsBoundary).SelectMany(relation => relation.Members)
            .Where(member => member.Kind == OsmKind.Way && member.Role == "outer").Select(member => member.Id);

        internal static bool IsOutline(OsmWay way) => way == null ||
            way.Tags.All(tag => (tag.Key == "area" && tag.Value == "yes") ||
                (tag.Key == "boundary" && tag.Value == "administrative"));

        internal static bool IsChanged(OsmDocument before, OsmDocument after, OsmRelation original, OsmRelation edited)
        {
            if (original == null || !original.SameContent(edited)) return true;
            foreach (OsmMember member in original.Members)
            {
                if (member.Kind != OsmKind.Way) continue;
                OsmWay previous = before.Ways[member.Id];
                if (!previous.NodeIds.SequenceEqual(after.Ways[member.Id].NodeIds)) return true;
                if (previous.NodeIds.Any(id => !CityOsmEdit.SameHorizontalPosition(before.Nodes[id], after.Nodes[id]))) return true;
            }
            return false;
        }

        internal static List<float2> Ring(OsmDocument document, OsmRelation relation, float4 bounds)
        {
            if (relation.Members.Count == 0 || relation.Members.Any(member => member.Kind != OsmKind.Way || member.Role != "outer"))
                throw new FormatException($"Administrative boundary {relation.Id} needs outer ways for one closed ring.");
            var ways = relation.Members.Select(member => document.Ways[member.Id]).ToList();
            if (ways.Any(way => way.NodeIds.Count < 2))
                throw new FormatException($"Administrative boundary {relation.Id} needs at least two nodes per outer way.");
            var nodes = new List<long>(ways[0].NodeIds);
            ways.RemoveAt(0);
            while (ways.Count > 0)
            {
                long end = nodes[nodes.Count - 1];
                int next = ways.FindIndex(way => way.NodeIds[0] == end || way.NodeIds[way.NodeIds.Count - 1] == end);
                if (next < 0 || end == nodes[0])
                    throw new FormatException($"Administrative boundary {relation.Id} needs connected outer ways forming one ring.");
                var continuation = ways[next].NodeIds;
                nodes.AddRange(continuation[0] == end ? continuation.Skip(1) : continuation.AsEnumerable().Reverse().Skip(1));
                ways.RemoveAt(next);
            }
            return CityOsmAreas.Ring(document, new OsmWay(relation.Id, nodes), bounds);
        }

        internal static bool SameRing(IReadOnlyList<float2> actual, IReadOnlyList<float2> expected)
        {
            if (actual.Count != expected.Count) return false;
            for (int first = 0; first < actual.Count; first++)
            {
                if (math.distance(actual[first], expected[0]) > 0.05f) continue;
                bool forward = true, backward = true;
                for (int i = 1; i < expected.Count && (forward || backward); i++)
                {
                    forward &= math.distance(actual[(first + i) % actual.Count], expected[i]) <= 0.05f;
                    backward &= math.distance(actual[(first + actual.Count - i) % actual.Count], expected[i]) <= 0.05f;
                }
                if (forward || backward) return true;
            }
            return false;
        }
    }
}
