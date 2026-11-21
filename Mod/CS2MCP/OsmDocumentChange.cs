using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace CS2MCP
{
    internal sealed class OsmChange
    {
        private readonly XElement m_Root;

        private OsmChange(XElement root) => m_Root = root;

        public static OsmChange Between(OsmDocument before, OsmDocument after)
        {
            before.Validate();
            after.Validate();
            var created = new XElement("create");
            var modified = new XElement("modify");
            var deleted = new XElement("delete");
            foreach (OsmElement edited in after.Elements)
            {
                OsmElement original = before.Find(edited.Kind, edited.Id);
                if (original == null)
                {
                    created.Add(OsmXml.WriteElement(edited));
                }
                else if (!original.SameContent(edited))
                {
                    XElement element = OsmXml.WriteElement(edited);
                    original.Attributes.TryGetValue("version", out string version);
                    element.SetAttributeValue("version", version);
                    modified.Add(element);
                }
            }
            foreach (OsmElement element in before.Elements.Reverse())
            {
                if (after.Find(element.Kind, element.Id) == null)
                {
                    var removal = new XElement(element.Kind.ToString().ToLowerInvariant(), new XAttribute("id", element.Id));
                    if (element.Attributes.TryGetValue("version", out string version))
                    {
                        removal.Add(new XAttribute("version", version));
                    }
                    deleted.Add(removal);
                }
            }
            var root = new XElement("osmChange", new XAttribute("version", "0.6"), new XAttribute("generator", "airimayor"));
            root.Add(new[] { created, modified, deleted }.Where(block => block.HasElements));
            return new OsmChange(root);
        }

        public static OsmChange Read(string xml) => Read(OsmXml.ReadRoot(xml));

        internal static OsmChange Read(XElement root)
        {
            OsmXml.RequireRoot(root, "osmChange");
            foreach (XElement block in root.Elements())
            {
                if (block.Name != "create" && block.Name != "modify" && block.Name != "delete")
                {
                    throw new FormatException($"Unknown OsmChange action '{block.Name}'.");
                }
                if (block.Attribute("if-unused") != null && block.Name != "delete")
                {
                    throw new FormatException("Only an OsmChange deletion accepts if-unused.");
                }
                foreach (XElement item in block.Elements())
                {
                    if (block.Name == "delete")
                    {
                        OsmXml.ParseKind(item.Name);
                        OsmXml.Id(item, "id");
                        OsmXml.ElementVersion(item);
                    }
                    else
                    {
                        OsmXml.ReadElement(item);
                    }
                }
            }
            return new OsmChange(root);
        }

        public string Write() => m_Root.ToString();

        public OsmDocument ApplyTo(OsmDocument source)
        {
            OsmDocument result = source.Copy();
            foreach (XElement block in m_Root.Elements())
            {
                bool conditional = block.Attribute("if-unused") != null;
                var candidates = new HashSet<(OsmKind Kind, long Id)>();
                foreach (XElement item in block.Elements())
                {
                    OsmKind kind = OsmXml.ParseKind(item.Name);
                    long id = OsmXml.Id(item, "id");
                    OsmElement existing = result.Find(kind, id);
                    if (block.Name == "create")
                    {
                        if (existing != null)
                        {
                            throw new FormatException($"OSM {kind} {id} already exists.");
                        }
                        result.Put(OsmXml.ReadElement(item));
                        continue;
                    }
                    if (existing == null)
                    {
                        if (conditional) continue;
                        throw new FormatException($"Cannot {block.Name} missing OSM {kind} {id}.");
                    }
                    long? version = OsmXml.ElementVersion(item);
                    if (version.HasValue && existing.Attributes.TryGetValue("version", out string original)
                        && version.Value < long.Parse(original, CultureInfo.InvariantCulture))
                    {
                        throw new FormatException($"OSM {kind} {id} has version {original}. The edit uses older version {version}.");
                    }
                    if (block.Name == "modify")
                    {
                        OsmElement replacement = OsmXml.ReadElement(item);
                        foreach (var pair in existing.Attributes)
                        {
                            if (!replacement.Attributes.ContainsKey(pair.Key))
                            {
                                replacement.Attributes.Add(pair.Key, pair.Value);
                            }
                        }
                        result.Put(replacement);
                    }
                    else if (conditional)
                    {
                        candidates.Add((kind, id));
                    }
                    else
                    {
                        result.Remove(kind, id);
                    }
                }
                if (candidates.Count > 0)
                {
                    DeleteUnused(result, candidates);
                }
            }
            result.Validate();
            return result;
        }

        private static void DeleteUnused(OsmDocument document, HashSet<(OsmKind Kind, long Id)> candidates)
        {
            var retained = new Queue<OsmElement>();
            foreach (OsmElement element in document.Ways.Values.Cast<OsmElement>().Concat(document.Relations.Values))
            {
                if (!candidates.Contains((element.Kind, element.Id)))
                {
                    retained.Enqueue(element);
                }
            }
            while (retained.Count > 0)
            {
                OsmElement parent = retained.Dequeue();
                if (parent is OsmWay way)
                {
                    foreach (long node in way.NodeIds)
                    {
                        candidates.Remove((OsmKind.Node, node));
                    }
                }
                else if (parent is OsmRelation relation)
                {
                    foreach (OsmMember member in relation.Members)
                    {
                        if (candidates.Remove((member.Kind, member.Id)) && member.Kind != OsmKind.Node)
                        {
                            retained.Enqueue(document.Find(member.Kind, member.Id));
                        }
                    }
                }
            }
            foreach (var candidate in candidates)
            {
                document.Remove(candidate.Kind, candidate.Id);
            }
        }
    }
}
