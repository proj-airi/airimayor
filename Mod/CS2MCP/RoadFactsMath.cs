using System;

namespace CS2MCP
{
    internal static class RoadFactsMath
    {
        public const string RoadClassHighway = "highway";
        public const string RoadClassLarge = "large";
        public const string RoadClassMedium = "medium";
        public const string RoadClassMinor = "minor";
        public const string RoadClassUnknown = "unknown";

        /// <summary>
        /// Display km/h from the baked RoadData limit (baked x 1.5).
        /// </summary>
        public static double ToKmh(float bakedSpeedLimit)
        {
            return Math.Round(bakedSpeedLimit * 1.5f, 0);
        }

        /// <summary>
        /// One fact source: group first, name and width only as fallback.
        /// </summary>
        public static string Classify(string groupName, string prefabName)
        {
            string grouped = ClassifyGroup(groupName);
            return grouped != RoadClassUnknown
                ? grouped
                : ClassifyFallback(prefabName);
        }

        /// <summary>
        /// Native UI group names (Small / Medium / Large / Highways tabs).
        /// </summary>
        public static string ClassifyGroup(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
            {
                return RoadClassUnknown;
            }
            if (groupName.IndexOf("highway", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RoadClassHighway;
            }
            if (groupName.IndexOf("large", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RoadClassLarge;
            }
            if (groupName.IndexOf("medium", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RoadClassMedium;
            }
            if (groupName.IndexOf("small", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RoadClassMinor;
            }
            return RoadClassUnknown;
        }

        /// <summary>
        /// Best effort when the UI group is missing: prefab name only.
        /// </summary>
        public static string ClassifyFallback(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return RoadClassUnknown;
            }
            if (prefabName.IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RoadClassHighway;
            }
            if (prefabName.IndexOf("Large", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RoadClassLarge;
            }
            if (prefabName.IndexOf("Medium", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RoadClassMedium;
            }
            return RoadClassMinor;
        }
    }
}
