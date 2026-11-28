using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Unity.Mathematics;

namespace CS2MCP
{
    internal static class CityOsmFile
    {
        internal const long FirstMapFeatureId = 1L << 61;
        private const string MapGenerator = "airimayor-map";

        internal static void MarkMap(OsmDocument document) => document.Attributes["generator"] = MapGenerator;

        internal static void RequireEditable(OsmDocument document)
        {
            if ((document.Attributes.TryGetValue("generator", out string generator) && generator == MapGenerator)
                || document.Elements.Any(element => element.Id >= FirstMapFeatureId))
                throw new FormatException("Read the editable district before proposing construction. The map view is read-only.");
        }

        public static bool ValidBounds(float4 bounds) => math.all(math.isfinite(bounds))
            && math.all(bounds.xy < bounds.zw)
            && math.all(math.abs((double2)bounds.xz) <= 180d * CityOsm.LongitudeMeters)
            && math.all(math.abs((double2)bounds.yw) <= 90d * CityOsm.LatitudeMeters);

        public static void AddBounds(OsmDocument document, float4 bounds)
        {
            document.Headers.Add(new XElement("bounds",
                new XAttribute("minlat", bounds.y / CityOsm.LatitudeMeters),
                new XAttribute("minlon", bounds.x / CityOsm.LongitudeMeters),
                new XAttribute("maxlat", bounds.w / CityOsm.LatitudeMeters),
                new XAttribute("maxlon", bounds.z / CityOsm.LongitudeMeters)));
        }

        public static string Revision(Guid city, float4 bounds, string content)
        {
            string scope = string.Format(CultureInfo.InvariantCulture, "{0:N}/{1:R}/{2:R}/{3:R}/{4:R}\n",
                city, bounds.x, bounds.y, bounds.z, bounds.w);
            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(scope + content)));
            }
        }
    }
}
