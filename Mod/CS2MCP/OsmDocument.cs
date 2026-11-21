using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace CS2MCP
{
    internal sealed class OsmDocument
    {
        public SortedDictionary<long, OsmNode> Nodes { get; } = new SortedDictionary<long, OsmNode>();
        public SortedDictionary<long, OsmWay> Ways { get; } = new SortedDictionary<long, OsmWay>();
        public SortedDictionary<long, OsmRelation> Relations { get; } = new SortedDictionary<long, OsmRelation>();
        public Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "0.6",
            ["generator"] = "airimayor"
        };
        public List<XElement> Headers { get; } = new List<XElement>();
        public IEnumerable<OsmElement> Elements => Nodes.Values.Cast<OsmElement>().Concat(Ways.Values).Concat(Relations.Values);

        public static OsmDocument Read(string xml) => Read(OsmXml.ReadRoot(xml));

        private static OsmDocument Read(XElement root)
        {
            OsmXml.RequireRoot(root, "osm");
            var document = new OsmDocument();
            document.Attributes.Clear();
            foreach (XAttribute attribute in root.Attributes())
            {
                document.Attributes.Add(attribute.Name.ToString(), attribute.Value);
            }
            var ids = new HashSet<(OsmKind, long)>();
            foreach (XElement item in root.Elements())
            {
                if (item.Name == "bounds" || item.Name == "note" || item.Name == "meta")
                {
                    if (item.Name == "bounds")
                    {
                        OsmXml.ValidateBounds(item);
                    }
                    document.Headers.Add(new XElement(item));
                    continue;
                }
                string action = (string)item.Attribute("action");
                if (action != null && action != "create" && action != "modify" && action != "delete")
                {
                    throw new FormatException($"Unknown OSM edit action '{action}'.");
                }
                OsmKind kind = OsmXml.ParseKind(item.Name);
                long id = OsmXml.Id(item, "id");
                if (!ids.Add((kind, id)))
                {
                    throw new FormatException($"Duplicate OSM {kind} {id}.");
                }
                if (action != "delete")
                {
                    OsmElement element = OsmXml.ReadElement(item);
                    element.Attributes.Remove("action");
                    document.Put(element);
                }
            }
            document.Validate();
            return document;
        }

        public string Write()
        {
            Validate();
            var root = new XElement("osm", OsmXml.WriteAttributes(Attributes));
            root.Add(Headers);
            root.Add(Elements.Select(OsmXml.WriteElement));
            return root.ToString();
        }

        public OsmDocument Apply(string osmChangeXml) => OsmChange.Read(osmChangeXml).ApplyTo(this);

        public OsmDocument Edit(string content)
        {
            XElement root = OsmXml.ReadRoot(content);
            return root.Name == "osmChange" ? OsmChange.Read(root).ApplyTo(this) : Read(root);
        }

        public void Validate()
        {
            if (!Attributes.TryGetValue("version", out string version) || version != "0.6")
            {
                throw new FormatException("Expected an OSM 0.6 document.");
            }
            foreach (var pair in Nodes)
            {
                ValidateIdentity(pair.Key, pair.Value);
                OsmXml.ValidateCoordinates(pair.Value.Latitude, pair.Value.Longitude);
            }
            foreach (var pair in Ways)
            {
                ValidateIdentity(pair.Key, pair.Value);
                if (pair.Value.NodeIds.Count < 2)
                {
                    throw new FormatException($"OSM way {pair.Key} needs at least two nodes.");
                }
                foreach (long node in pair.Value.NodeIds)
                {
                    if (!Nodes.ContainsKey(node))
                    {
                        throw new FormatException($"OSM way {pair.Key} references missing node {node}.");
                    }
                }
            }
            foreach (var pair in Relations)
            {
                ValidateIdentity(pair.Key, pair.Value);
                foreach (OsmMember member in pair.Value.Members)
                {
                    if (Find(member.Kind, member.Id) == null)
                    {
                        throw new FormatException($"OSM relation {pair.Key} references missing {member.Kind} {member.Id}.");
                    }
                }
            }
        }

        private static void ValidateIdentity(long key, OsmElement element)
        {
            if (element == null || key == 0 || key != element.Id)
            {
                throw new FormatException($"Invalid OSM element identity at {key}.");
            }
        }

        public OsmElement Find(OsmKind kind, long id)
        {
            switch (kind)
            {
                case OsmKind.Node: return Nodes.TryGetValue(id, out OsmNode node) ? node : null;
                case OsmKind.Way: return Ways.TryGetValue(id, out OsmWay way) ? way : null;
                case OsmKind.Relation: return Relations.TryGetValue(id, out OsmRelation relation) ? relation : null;
                default: throw new FormatException($"Unknown OSM member type {kind}.");
            }
        }

        internal void Put(OsmElement element)
        {
            switch (element)
            {
                case OsmNode node: Nodes[node.Id] = node; break;
                case OsmWay way: Ways[way.Id] = way; break;
                case OsmRelation relation: Relations[relation.Id] = relation; break;
            }
        }

        internal void Remove(OsmKind kind, long id)
        {
            switch (kind)
            {
                case OsmKind.Node: Nodes.Remove(id); break;
                case OsmKind.Way: Ways.Remove(id); break;
                case OsmKind.Relation: Relations.Remove(id); break;
            }
        }

        internal OsmDocument Copy()
        {
            var copy = new OsmDocument();
            copy.Attributes.Clear();
            foreach (var pair in Attributes)
            {
                copy.Attributes.Add(pair.Key, pair.Value);
            }
            copy.Headers.AddRange(Headers.Select(header => new XElement(header)));
            foreach (OsmElement element in Elements)
            {
                copy.Put(element.Copy());
            }
            return copy;
        }
    }

    internal static class OsmXml
    {
        internal static XElement ReadRoot(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                throw new FormatException("Provide an OSM XML document.");
            }
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (var input = new StringReader(xml))
                using (var reader = XmlReader.Create(input, settings))
                {
                    return XDocument.Load(reader).Root;
                }
            }
            catch (XmlException exception)
            {
                throw new FormatException("Invalid OSM XML. " + exception.Message, exception);
            }
        }

        internal static void RequireRoot(XElement root, string name)
        {
            string version = (string)root.Attribute("version");
            if (root.Name != name || (version != "0.6" && !(name == "osmChange" && version == null)))
            {
                throw new FormatException($"Expected <{name} version=\"0.6\">.");
            }
        }

        public static OsmKind ParseKind(XName name)
        {
            if (name == "node") return OsmKind.Node;
            if (name == "way") return OsmKind.Way;
            if (name == "relation") return OsmKind.Relation;
            throw new FormatException($"Unknown OSM element type '{name}'.");
        }

        public static long Id(XElement item, string name)
        {
            if (!long.TryParse((string)item.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id == 0)
            {
                throw new FormatException($"OSM {item.Name} needs a nonzero integer {name}.");
            }
            return id;
        }

        private static string Required(XElement item, string name)
        {
            return (string)item.Attribute(name) ?? throw new FormatException($"OSM {item.Name} needs {name}.");
        }

        private static double Number(XElement item, string name)
        {
            if (!double.TryParse(Required(item, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw new FormatException($"Invalid OSM {name} coordinate.");
            }
            return value;
        }

        public static void ValidateCoordinates(double latitude, double longitude)
        {
            if (!(latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180))
            {
                throw new FormatException("OSM coordinates must be finite and within latitude and longitude limits.");
            }
        }

        public static void ValidateBounds(XElement bounds)
        {
            double south = Number(bounds, "minlat");
            double west = Number(bounds, "minlon");
            double north = Number(bounds, "maxlat");
            double east = Number(bounds, "maxlon");
            ValidateCoordinates(south, west);
            ValidateCoordinates(north, east);
            if (south > north || west > east)
            {
                throw new FormatException("The OSM bounds are reversed.");
            }
        }

        public static OsmElement ReadElement(XElement xml)
        {
            long id = Id(xml, "id");
            OsmElement element;
            switch (ParseKind(xml.Name))
            {
                case OsmKind.Node:
                    var node = new OsmNode(id, Number(xml, "lat"), Number(xml, "lon"));
                    ValidateCoordinates(node.Latitude, node.Longitude);
                    element = node;
                    break;
                case OsmKind.Way: element = new OsmWay(id); break;
                default: element = new OsmRelation(id); break;
            }
            foreach (XAttribute attribute in xml.Attributes())
            {
                if (attribute.Name != "id" && !(element is OsmNode && (attribute.Name == "lat" || attribute.Name == "lon")))
                {
                    element.Attributes.Add(attribute.Name.ToString(), attribute.Value);
                }
            }
            ElementVersion(xml);
            foreach (XElement child in xml.Elements())
            {
                if (child.Name == "tag")
                {
                    string key = Required(child, "k");
                    if (string.IsNullOrEmpty(key) || element.Tags.ContainsKey(key))
                    {
                        throw new FormatException($"OSM {xml.Name} {id} has an empty or duplicate tag key '{key}'.");
                    }
                    element.Tags.Add(key, Required(child, "v"));
                }
                else if (child.Name == "nd" && element is OsmWay way)
                {
                    way.NodeIds.Add(Id(child, "ref"));
                }
                else if (child.Name == "member" && element is OsmRelation relation)
                {
                    relation.Members.Add(new OsmMember(ParseKind(Required(child, "type")), Id(child, "ref"), (string)child.Attribute("role")));
                }
                else
                {
                    throw new FormatException($"Unexpected <{child.Name}> in OSM {xml.Name} {id}.");
                }
            }
            return element;
        }

        public static long? ElementVersion(XElement item)
        {
            string version = (string)item.Attribute("version");
            if (version == null)
            {
                return null;
            }
            if (!long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
            {
                throw new FormatException($"Invalid OSM {item.Name} version '{version}'.");
            }
            return number;
        }

        public static XElement WriteElement(OsmElement element)
        {
            var xml = new XElement(element.Kind.ToString().ToLowerInvariant(), new XAttribute("id", element.Id));
            xml.Add(WriteAttributes(element.Attributes));
            switch (element)
            {
                case OsmNode node:
                    xml.Add(new XAttribute("lat", node.Latitude), new XAttribute("lon", node.Longitude));
                    break;
                case OsmWay way:
                    xml.Add(way.NodeIds.Select(id => new XElement("nd", new XAttribute("ref", id))));
                    break;
                case OsmRelation relation:
                    xml.Add(relation.Members.Select(member => new XElement("member",
                        new XAttribute("type", member.Kind.ToString().ToLowerInvariant()),
                        new XAttribute("ref", member.Id), new XAttribute("role", member.Role))));
                    break;
            }
            xml.Add(element.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new XElement("tag", new XAttribute("k", pair.Key), new XAttribute("v", pair.Value))));
            return xml;
        }

        public static IEnumerable<XAttribute> WriteAttributes(Dictionary<string, string> attributes)
        {
            return attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new XAttribute(pair.Key, pair.Value));
        }
    }
}
