using System;
using System.Collections.Generic;
using System.Linq;

namespace CS2MCP
{
    internal enum OsmKind
    {
        Node,
        Way,
        Relation
    }

    internal readonly struct OsmMember : IEquatable<OsmMember>
    {
        public OsmMember(OsmKind kind, long id, string role)
        {
            Kind = kind;
            Id = id;
            Role = role ?? "";
        }

        public OsmKind Kind { get; }
        public long Id { get; }
        public string Role { get; }

        public bool Equals(OsmMember other) => Kind == other.Kind && Id == other.Id && Role == other.Role;
        public override bool Equals(object other) => other is OsmMember member && Equals(member);
        public override int GetHashCode() => ((int)Kind * 397 ^ Id.GetHashCode()) * 397 ^ (Role?.GetHashCode() ?? 0);
    }

    internal abstract class OsmElement
    {
        protected OsmElement(long id) => Id = id;

        public long Id { get; }
        public abstract OsmKind Kind { get; }
        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool SameContent(OsmElement other)
        {
            if (other == null || Kind != other.Kind || Id != other.Id || Tags.Count != other.Tags.Count)
            {
                return false;
            }
            return Tags.All(pair => other.Tags.TryGetValue(pair.Key, out string value) && value == pair.Value)
                && SameGeometry(other);
        }

        protected abstract bool SameGeometry(OsmElement other);
        protected abstract OsmElement CopyGeometry();

        internal OsmElement Copy()
        {
            OsmElement copy = CopyGeometry();
            foreach (var pair in Tags)
            {
                copy.Tags.Add(pair.Key, pair.Value);
            }
            foreach (var pair in Attributes)
            {
                copy.Attributes.Add(pair.Key, pair.Value);
            }
            return copy;
        }
    }

    internal sealed class OsmNode : OsmElement
    {
        public OsmNode(long id, double latitude, double longitude) : base(id)
        {
            Latitude = latitude;
            Longitude = longitude;
        }

        public override OsmKind Kind => OsmKind.Node;
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        protected override bool SameGeometry(OsmElement other)
        {
            var node = (OsmNode)other;
            return Latitude == node.Latitude && Longitude == node.Longitude;
        }

        protected override OsmElement CopyGeometry() => new OsmNode(Id, Latitude, Longitude);
    }

    internal sealed class OsmWay : OsmElement
    {
        public OsmWay(long id, IEnumerable<long> nodes = null) : base(id)
        {
            NodeIds = nodes == null ? new List<long>() : new List<long>(nodes);
        }

        public override OsmKind Kind => OsmKind.Way;
        public List<long> NodeIds { get; }

        protected override bool SameGeometry(OsmElement other) => NodeIds.SequenceEqual(((OsmWay)other).NodeIds);
        protected override OsmElement CopyGeometry() => new OsmWay(Id, NodeIds);
    }

    internal sealed class OsmRelation : OsmElement
    {
        public OsmRelation(long id) : base(id) { }

        public override OsmKind Kind => OsmKind.Relation;
        public List<OsmMember> Members { get; } = new List<OsmMember>();

        protected override bool SameGeometry(OsmElement other) => Members.SequenceEqual(((OsmRelation)other).Members);

        protected override OsmElement CopyGeometry()
        {
            var copy = new OsmRelation(Id);
            copy.Members.AddRange(Members);
            return copy;
        }
    }
}
