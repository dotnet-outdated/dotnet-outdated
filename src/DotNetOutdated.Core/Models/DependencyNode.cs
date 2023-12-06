using System;
using System.Collections.Generic;

namespace DotNetOutdated.Core.Models
{
    public sealed class DependencyNode : IEquatable<DependencyNode>
    {
        private readonly HashSet<DependencyNode> _nodes;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0016:Use 'throw' expression", Justification = "Prefer this way")]
        public DependencyNode(string id, Dependency dependencyItem)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException(nameof(id));
            if (dependencyItem == null)
                throw new ArgumentNullException(nameof(dependencyItem));

            _nodes = new HashSet<DependencyNode>();

            Id = id;
            DependencyItem = dependencyItem;
        }

        public Dependency DependencyItem { get; }
        public string Id { get; }
        public IReadOnlyCollection<DependencyNode> Nodes => _nodes;

        public void AddNode(DependencyNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

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
    }
}
