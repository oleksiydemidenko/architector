using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Architector.Editor.Agents
{
    public static class ArchitectorLayerSummary
    {
        private const int ListedIncomingTargets = 8;

        public static string Describe(ArchitectorRepository repository, string layerId)
        {
            var layer = repository.FindLayer(layerId)
                        ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var documents = repository.LoadLayer(layerId);
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            string Title(string ownerLayerId, string nodeId)
            {
                var key = ownerLayerId + "/" + nodeId;
                if (!titles.TryGetValue(key, out var title))
                {
                    title = repository.LoadLayer(ownerLayerId).semantic.nodes.FirstOrDefault(node => node.id == nodeId)?.title ?? nodeId;
                    titles[key] = title;
                }
                return title;
            }

            var hiddenLayers = documents.semantic.nodes
                .Where(node => node.kind == ArchitectorNodeKinds.LayerPortal && node.portal != null && !repository.IsPortalEndpointVisible(layerId, node))
                .Select(node => node.portal.targetLayerId)
                .ToHashSet(StringComparer.Ordinal);
            string Remote(string remoteLayerId) =>
                repository.GetEntryPath(remoteLayerId) + (hiddenLayers.Contains(remoteLayerId) ? " (hidden here: not approved)" : string.Empty);

            var builder = new StringBuilder();
            builder.Append("Layer: ").Append(repository.GetEntryPath(layerId)).Append('\n');
            builder.Append("Approval: ").Append(repository.GetLayerApprovalStatus(layerId).State).Append('\n');
            if (!string.IsNullOrWhiteSpace(layer.purpose))
                builder.Append("Purpose: ").Append(layer.purpose.Trim()).Append('\n');
            if (layer.scope.folders.Count > 0 || layer.scope.namespaces.Count > 0)
                builder.Append("Scope: ").Append(string.Join(", ", layer.scope.folders.Concat(layer.scope.namespaces))).Append('\n');

            var nodes = documents.semantic.nodes.Where(node => node.kind != ArchitectorNodeKinds.LayerPortal).ToArray();
            builder.Append("Nodes (").Append(nodes.Length).Append("):\n");
            foreach (var node in nodes)
                builder.Append("- ").Append(NodeLine(node)).Append('\n');

            var relationships = repository.GetRelationshipsForLayer(layerId);
            var inside = relationships.Where(edge => edge.sourceLayerId == layerId && edge.targetLayerId == layerId)
                .Select(edge => $"{Title(layerId, edge.sourceNodeId)} -> {Title(layerId, edge.targetNodeId)}{Kind(edge)}")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToArray();
            AppendLines(builder, $"Relationships inside ({inside.Length}):", inside);

            var outgoing = relationships.Where(edge => edge.sourceLayerId == layerId && edge.targetLayerId != layerId)
                .Select(edge => $"{Title(layerId, edge.sourceNodeId)} -> {Remote(edge.targetLayerId)}/{Title(edge.targetLayerId, edge.targetNodeId)}{Kind(edge)}")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToArray();
            AppendLines(builder, $"Outgoing ({outgoing.Length}):", outgoing);

            var incoming = relationships.Where(edge => edge.targetLayerId == layerId && edge.sourceLayerId != layerId)
                .GroupBy(edge => edge.sourceLayerId)
                .Select(group => IncomingLine(Remote(group.Key), group.ToArray(), nodeId => Title(layerId, nodeId)))
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToArray();
            var incomingCount = relationships.Count(edge => edge.targetLayerId == layerId && edge.sourceLayerId != layerId);
            AppendLines(builder, $"Incoming ({incomingCount} from {incoming.Length} layers; relationships with direction=in lists them):", incoming);
            return builder.ToString().TrimEnd('\n');
        }

        private static string NodeLine(ArchitectorNodeRecord node)
        {
            var type = node.type;
            var description = new StringBuilder(node.title);
            if (type != null && type.mode != "none")
            {
                var name = string.IsNullOrEmpty(type.typeNamespace) ? type.name : $"{type.typeNamespace}.{type.name}";
                description.Append(" (").Append(type.mode);
                if (!string.IsNullOrEmpty(type.kind))
                    description.Append(' ').Append(type.kind);
                if (!string.IsNullOrEmpty(name))
                    description.Append(' ').Append(name);
                var bases = new[] { type.baseType?.name }
                    .Concat((type.interfaces ?? new List<ArchitectorTypeReferenceRecord>()).Select(item => item.name))
                    .Where(item => !string.IsNullOrEmpty(item))
                    .ToArray();
                if (bases.Length > 0)
                    description.Append(" : ").Append(string.Join(", ", bases));
                description.Append("; ").Append(node.members.Count).Append(node.members.Count == 1 ? " member)" : " members)");
            }
            if (!string.IsNullOrWhiteSpace(node.responsibility))
                description.Append(" — ").Append(node.responsibility.Trim());
            return description.ToString();
        }

        private static string IncomingLine(string sourceLayer, IReadOnlyList<ArchitectorEdgeRecord> edges, Func<string, string> title)
        {
            var kinds = edges.GroupBy(edge => edge.kind)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key} {group.Count()}");
            var targets = edges.GroupBy(edge => edge.targetNodeId)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => title(group.Key), StringComparer.Ordinal)
                .Select(group => title(group.Key))
                .ToArray();
            var listed = string.Join(", ", targets.Take(ListedIncomingTargets));
            if (targets.Length > ListedIncomingTargets)
                listed += $" and {targets.Length - ListedIncomingTargets} more";
            return $"{sourceLayer}: {edges.Count} ({string.Join(", ", kinds)}) to {listed}";
        }

        private static string Kind(ArchitectorEdgeRecord edge)
        {
            return string.IsNullOrWhiteSpace(edge.label) ? $" {edge.kind}" : $" {edge.kind} \"{edge.label.Trim()}\"";
        }

        private static void AppendLines(StringBuilder builder, string heading, IReadOnlyCollection<string> lines)
        {
            if (lines.Count == 0)
                return;
            builder.Append(heading).Append('\n');
            foreach (var line in lines)
                builder.Append("- ").Append(line).Append('\n');
        }
    }
}
