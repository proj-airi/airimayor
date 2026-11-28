using System;
using System.Collections.Generic;
using System.Linq;

namespace CS2MCP
{
    internal static class CityOsmFacilityUpgrades
    {
        private const string Prefix = "airimayor:facility_upgrade:";

        internal static string Tag(string asset) => Prefix + asset;

        internal static List<string> Additions(OsmDocument before, OsmDocument after, OsmWay original, OsmWay edited)
        {
            var additions = new List<string>();
            var tags = original == null ? edited.Tags.Keys.AsEnumerable() : original.Tags.Keys.Union(edited.Tags.Keys);
            foreach (string key in tags)
            {
                if (!key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                string oldValue = CityOsm.Tag(original, key);
                string newValue = CityOsm.Tag(edited, key);
                if (oldValue == newValue) continue;
                string asset = key.Substring(Prefix.Length);
                if (oldValue != null)
                    throw new FormatException($"Keep the installed facility upgrade '{asset}' on way {edited.Id}.");
                if (newValue != "yes" || string.IsNullOrWhiteSpace(asset))
                    throw new FormatException($"Set {Prefix}<upgrade name>=yes to install a facility upgrade.");
                additions.Add(asset);
            }
            if (additions.Count == 0) return additions;
            if (original == null || !original.NodeIds.SequenceEqual(edited.NodeIds)
                || original.NodeIds.Any(id => !CityOsmEdit.SameNodePosition(before.Nodes[id], after.Nodes[id])))
                throw new FormatException("Install the facility upgrade on a building with an unchanged lot.");
            CityOsmEdit.CheckEditedTags(original, edited, additions.Select(Tag).ToArray());
            return additions;
        }
    }
}
