using System;
using System.Collections.Generic;
using System.Linq;
using Game.Prefabs;

namespace CS2MCP
{
    internal static class OsmRoadUpgrades
    {
        private static readonly Dictionary<string, CompositionFlags> Features = BuildFeatures();
        internal static readonly string[] EditableRoadTags = new[]
        {
            "highway", "oneway", "lanes", "width", "maxspeed", "bridge", "tunnel", "layer", "airimayor:prefab", "name"
        }.Concat(Features.Keys).ToArray();

        private static Dictionary<string, CompositionFlags> BuildFeatures()
        {
            var features = new Dictionary<string, CompositionFlags>(StringComparer.Ordinal);
            foreach (var side in new[]
            {
                ("grass", CompositionFlags.Side.PrimaryBeautification),
                ("trees", CompositionFlags.Side.SecondaryBeautification),
                ("wide_sidewalk", CompositionFlags.Side.WideSidewalk),
                ("sound_barrier", CompositionFlags.Side.SoundBarrier),
                ("parking", CompositionFlags.Side.ParkingSpaces)
            })
            {
                features.Add("airimayor:upgrade:" + side.Item1 + ":left", new CompositionFlags { m_Left = side.Item2 });
                features.Add("airimayor:upgrade:" + side.Item1 + ":right", new CompositionFlags { m_Right = side.Item2 });
            }
            features.Add("airimayor:upgrade:lighting", new CompositionFlags { m_General = CompositionFlags.General.Lighting });
            features.Add("airimayor:upgrade:median_grass", new CompositionFlags { m_General = CompositionFlags.General.PrimaryMiddleBeautification });
            features.Add("airimayor:upgrade:median_trees", new CompositionFlags { m_General = CompositionFlags.General.SecondaryMiddleBeautification });
            return features;
        }

        internal static Dictionary<string, string> Export(CompositionFlags flags)
        {
            return Features.Where(feature => (flags & feature.Value) != default)
                .ToDictionary(feature => feature.Key, feature => "yes");
        }

        internal static CompositionFlags Read(OsmWay way, CompositionFlags flags)
        {
            foreach (var feature in Features)
            {
                string value = CityOsm.Tag(way, feature.Key);
                if (value == "yes") flags |= feature.Value;
                else if (value == null || value == "no") flags &= ~feature.Value;
                else throw new FormatException($"Way {way.Id} needs yes or no for {feature.Key}.");
            }
            return flags;
        }
    }
}
