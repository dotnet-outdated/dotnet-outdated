using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DotNetOutdated.Core.Models
{
    [DebuggerDisplay("{" + nameof(GetDebuggerDisplay) + "(),nq}")]
    public sealed class DependencyNode : IEquatable<DependencyNode>
    {
        private readonly HashSet<DependencyNode> _nodes;

        public DependencyNode(string id, Dependency dependencyItem)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException(nameof(id));
            ArgumentNullException.ThrowIfNull(dependencyItem);

            _nodes = new HashSet<DependencyNode>();

            Id = id;
            DependencyItem = dependencyItem;
        }

        public Dependency DependencyItem { get; }
        public string Id { get; }
        public IReadOnlyCollection<DependencyNode> Nodes => _nodes;

        public void AddNode(DependencyNode node)
        {
            ArgumentNullException.ThrowIfNull(node);

            _nodes.Add(node);
        }

        public bool Equals(DependencyNode other)
        {
            if (other == null)
                return false;

            return string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as DependencyNode);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode(StringComparison.OrdinalIgnoreCase);
        }

        private string GetDebuggerDisplay()
        {
            return $"{Id}/{DependencyItem.ResolvedVersion.ToNormalizedString()}";
        }
    }
}
