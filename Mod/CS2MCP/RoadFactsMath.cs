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
    }
}
