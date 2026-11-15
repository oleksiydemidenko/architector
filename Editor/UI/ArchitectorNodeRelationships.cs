using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public sealed class ArchitectorNodeRelationshipRow
    {
        public string EdgeId { get; internal set; } = string.Empty;
        public string Kind { get; internal set; } = string.Empty;
        public string Label { get; internal set; } = string.Empty;
        public bool Incoming { get; internal set; }
        // The node at the other end: the target of an outgoing relationship, the source of an incoming one.
        public string LayerId { get; internal set; } = string.Empty;
        public string NodeId { get; internal set; } = string.Empty;
        public string Title { get; internal set; } = string.Empty;
        public string LayerName { get; internal set; } = string.Empty;
        public string TypeKind { get; internal set; } = string.Empty;
        public bool CrossLayer { get; internal set; }
    }

    public sealed class ArchitectorNodeRelationshipGroup
    {
        public bool Incoming { get; internal set; }
        public string Kind { get; internal set; } = string.Empty;
        // Set only for dependency groups: implements, extends and other dependency labels are groups of their own.
        public string Label { get; internal set; } = string.Empty;
        public string Title { get; internal set; } = string.Empty;
        public IReadOnlyList<ArchitectorNodeRelationshipRow> Rows { get; internal set; } = Array.Empty<ArchitectorNodeRelationshipRow>();
    }

    public static class ArchitectorNodeRelationships
    {
        // Groups the relationships of one node for the Inspector: outgoing groups first, then incoming; within a
        // direction by kind in ArchitectorEdgeKinds.All order (a dependency split by its label, unlabeled first), and
        // rows by the title of the node at the other end. In-layer and cross-layer relationships are both included.
        public static IReadOnlyList<ArchitectorNodeRelationshipGroup> Build(
            string layerId,
            string nodeId,
            IEnumerable<ArchitectorEdgeRecord> edges,
            Func<string, string, ArchitectorNodeRecord> findNode,
            Func<string, string> layerName)
        {
            var rows = new List<ArchitectorNodeRelationshipRow>();
            foreach (var edge in edges ?? Enumerable.Empty<ArchitectorEdgeRecord>())
            {
                if (edge == null)
                    continue;
                if (IsEndpoint(edge.sourceLayerId, edge.sourceNodeId, layerId, nodeId))
                    rows.Add(CreateRow(edge, false, edge.targetLayerId, edge.targetNodeId, findNode, layerName));
                if (IsEndpoint(edge.targetLayerId, edge.targetNodeId, layerId, nodeId))
                    rows.Add(CreateRow(edge, true, edge.sourceLayerId, edge.sourceNodeId, findNode, layerName));
            }

            return rows
                .GroupBy(row => (row.Incoming, row.Kind, Label: GroupLabel(row)), GroupKeyComparer.Instance)
                .Select(group => new ArchitectorNodeRelationshipGroup
                {
                    Incoming = group.Key.Incoming,
                    Kind = group.Key.Kind,
                    Label = group.Key.Label,
                    Title = GroupTitle(group.Key.Kind, group.Key.Label),
                    Rows = group
                        .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(row => row.Title, StringComparer.Ordinal)
                        .ThenBy(row => row.LayerName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(row => row.EdgeId, StringComparer.Ordinal)
                        .ToArray()
                })
                .OrderBy(group => group.Incoming)
                .ThenBy(group => KindOrder(group.Kind))
                .ThenBy(group => group.Kind, StringComparer.Ordinal)
                .ThenBy(group => group.Label.Length == 0 ? 0 : 1)
                .ThenBy(group => group.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // The rows the Inspector lists for the kinds switched on in its toolbar: by kind in ArchitectorEdgeKinds.All
        // order, incoming before outgoing, then by the title of the node at the other end.
        public static IReadOnlyList<ArchitectorNodeRelationshipRow> Visible(
            IEnumerable<ArchitectorNodeRelationshipGroup> groups,
            ICollection<string> kinds)
        {
            return (groups ?? Enumerable.Empty<ArchitectorNodeRelationshipGroup>())
                .SelectMany(group => group.Rows)
                .Where(row => kinds == null || kinds.Contains(row.Kind))
                .OrderBy(row => KindOrder(row.Kind))
                .ThenBy(row => row.Kind, StringComparer.Ordinal)
                .ThenBy(row => row.Incoming ? 0 : 1)
                .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Title, StringComparer.Ordinal)
                .ThenBy(row => row.LayerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.EdgeId, StringComparer.Ordinal)
                .ToArray();
        }

        public static string GroupTitle(string kind, string label)
        {
            var name = ArchitectorEdgeKinds.DisplayName(kind);
            return string.IsNullOrEmpty(label) ? name : $"{name} · {label}";
        }

        private static bool IsEndpoint(string edgeLayerId, string edgeNodeId, string layerId, string nodeId)
        {
            return string.Equals(edgeLayerId, layerId, StringComparison.Ordinal)
                   && string.Equals(edgeNodeId, nodeId, StringComparison.Ordinal);
        }

        private static ArchitectorNodeRelationshipRow CreateRow(
            ArchitectorEdgeRecord edge,
            bool incoming,
            string otherLayerId,
            string otherNodeId,
            Func<string, string, ArchitectorNodeRecord> findNode,
            Func<string, string> layerName)
        {
            var other = findNode?.Invoke(otherLayerId, otherNodeId);
            var name = layerName?.Invoke(otherLayerId);
            return new ArchitectorNodeRelationshipRow
            {
                EdgeId = edge.id ?? string.Empty,
                Kind = string.IsNullOrEmpty(edge.kind) ? ArchitectorEdgeKinds.Dependency : edge.kind,
                Label = edge.label?.Trim() ?? string.Empty,
                Incoming = incoming,
                LayerId = otherLayerId ?? string.Empty,
                NodeId = otherNodeId ?? string.Empty,
                Title = string.IsNullOrEmpty(other?.title) ? otherNodeId ?? string.Empty : other.title,
                LayerName = string.IsNullOrEmpty(name) ? otherLayerId ?? string.Empty : name,
                TypeKind = other == null || string.Equals(other.type?.mode ?? "none", "none", StringComparison.Ordinal)
                    ? string.Empty
                    : other.type.kind ?? string.Empty,
                CrossLayer = !string.Equals(edge.sourceLayerId, edge.targetLayerId, StringComparison.Ordinal)
            };
        }

        private static string GroupLabel(ArchitectorNodeRelationshipRow row)
        {
            return string.Equals(row.Kind, ArchitectorEdgeKinds.Dependency, StringComparison.Ordinal) ? row.Label : string.Empty;
        }

        private static int KindOrder(string kind)
        {
            for (var index = 0; index < ArchitectorEdgeKinds.All.Count; index++)
            {
                if (string.Equals(ArchitectorEdgeKinds.All[index], kind, StringComparison.Ordinal))
                    return index;
            }
            return ArchitectorEdgeKinds.All.Count;
        }

        private sealed class GroupKeyComparer : IEqualityComparer<(bool Incoming, string Kind, string Label)>
        {
            public static readonly GroupKeyComparer Instance = new();

            public bool Equals((bool Incoming, string Kind, string Label) left, (bool Incoming, string Kind, string Label) right)
            {
                return left.Incoming == right.Incoming
                       && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
                       && string.Equals(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode((bool Incoming, string Kind, string Label) key)
            {
                unchecked
                {
                    var hash = key.Incoming ? 1 : 0;
                    hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(key.Kind ?? string.Empty);
                    return hash * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(key.Label ?? string.Empty);
                }
            }
        }
    }
}
