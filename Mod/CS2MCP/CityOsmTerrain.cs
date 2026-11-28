using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.Mathematics;

namespace CS2MCP
{
    internal readonly struct OsmTerrainLevel
    {
        public OsmTerrainLevel(long wayId, float2[] ring, float4 bounds, float targetHeight)
        {
            WayId = wayId;
            Ring = ring;
            Bounds = bounds;
            TargetHeight = targetHeight;
        }

        public long WayId { get; }
        public float2[] Ring { get; }
        public float4 Bounds { get; }
        public float TargetHeight { get; }
    }

    internal static class CityOsmTerrain
    {
        private const string TerrainTag = "airimayor:terrain";
        private const string TargetHeightTag = "airimayor:target_height";

        internal static OsmTerrainLevel? Read(OsmDocument before, OsmDocument after, float4 district)
        {
            OsmElement[] terrain = after.Elements.Where(element =>
                element.Tags.ContainsKey(TerrainTag) || element.Tags.ContainsKey(TargetHeightTag)).Take(2).ToArray();
            if (terrain.Length == 0) return null;
            if (terrain.Length != 1 || !(terrain[0] is OsmWay way) || before.Ways.ContainsKey(way.Id)
                || CityOsm.Tag(way, "area") != "yes" || CityOsm.Tag(way, TerrainTag) != "level")
            {
                throw new FormatException("Draw one new closed way with area=yes and airimayor:terrain=level.");
            }

            CityOsmEdit.CheckEditedTags(null, way, "area", TerrainTag, TargetHeightTag);
            if (!float.TryParse(CityOsm.Tag(way, TargetHeightTag), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float height) || !math.isfinite(height))
            {
                throw new FormatException($"Terrain way {way.Id} needs a finite airimayor:target_height in city meters.");
            }

            if (!OnlyTerrainEdit(before, after, way))
            {
                throw new FormatException("Submit the terrain edit separately. Read the graded district before editing other city features.");
            }

            float2[] ring = CityOsmAreas.Ring(after, way, district).ToArray();
            float2 minimum = ring[0], maximum = ring[0];
            foreach (float2 point in ring)
            {
                minimum = math.min(minimum, point);
                maximum = math.max(maximum, point);
            }
            return new OsmTerrainLevel(way.Id, ring, new float4(minimum, maximum), height);
        }

        private static bool OnlyTerrainEdit(OsmDocument before, OsmDocument after, OsmWay way)
        {
            if (after.Ways.Count != before.Ways.Count + 1 || after.Relations.Count != before.Relations.Count) return false;
            foreach (OsmElement original in before.Elements)
            {
                OsmElement edited = after.Find(original.Kind, original.Id);
                if (original is OsmNode node)
                {
                    if (!(edited is OsmNode point) || !CityOsmEdit.SameNodePosition(node, point)
                        || node.Tags.Count != point.Tags.Count
                        || node.Tags.Any(tag => tag.Key != "ele" && CityOsm.Tag(point, tag.Key) != tag.Value))
                    {
                        return false;
                    }
                }
                else if (!original.SameContent(edited)) return false;
            }

            var corners = new HashSet<long>(way.NodeIds);
            return after.Nodes.Values.All(node =>
                before.Nodes.ContainsKey(node.Id) || (corners.Contains(node.Id) && node.Tags.Count == 0));
        }
    }
}
