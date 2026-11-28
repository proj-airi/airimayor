using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CS2MCP
{
    internal readonly struct CityOsmQueryTypes
    {
        private static readonly string[] kRoadClasses =
        {
            RoadFactsMath.RoadClassHighway, RoadFactsMath.RoadClassLarge, RoadFactsMath.RoadClassMedium,
            RoadFactsMath.RoadClassMinor, RoadFactsMath.RoadClassUnknown,
        };

        private static readonly string[] kBuildingTypes = { "residential", "commercial", "industrial", "office", "school", "other" };

        private readonly HashSet<string> m_Selected;

        private CityOsmQueryTypes(IEnumerable<string> selected) =>
            m_Selected = new HashSet<string>(selected, StringComparer.Ordinal);

        internal static CityOsmQueryTypes RoadClasses(string json) => Read(json, "road_classes", kRoadClasses);

        internal static CityOsmQueryTypes BuildingTypes(string json) => Read(json, "building_types", kBuildingTypes);

        internal static string BuildingType(string buildingTag) => buildingTag == "yes" ? "other" : buildingTag;

        internal bool Includes(string type) => m_Selected == null || m_Selected.Contains(type);

        private static CityOsmQueryTypes Read(string json, string argument, string[] allowed)
        {
            if (json == null) return default;
            string message = $"Choose one or more of {string.Join(", ", allowed)} for {argument}.";
            string[] selected;
            try
            {
                selected = JsonSerializer.Deserialize<string[]>(json);
            }
            catch (JsonException error)
            {
                throw new FormatException(message, error);
            }
            if (selected == null || selected.Length == 0 || selected.Any(type => Array.IndexOf(allowed, type) < 0))
                throw new FormatException(message);
            return new CityOsmQueryTypes(selected);
        }
    }
}
