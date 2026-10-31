using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public static class ArchitectorBaselineChanges
    {
        public const string Added = "added";
        public const string Removed = "removed";
        public const string Modified = "modified";
    }

    public readonly struct ArchitectorBaselineChange
    {
        public ArchitectorBaselineChange(string change, string subject, string id, string caption)
        {
            Change = change;
            Subject = subject;
            Id = id;
            Caption = caption;
        }

        public string Change { get; }
        public string Subject { get; }
        public string Id { get; }
        public string Caption { get; }
    }

    public static class ArchitectorBaselineDiff
    {
        public static ArchitectorLayerBaselineRecord Capture(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            string layerId)
        {
            var layer = project.layers.First(candidate => candidate.id == layerId);
            var documents = layers[layerId];
            var relationships = OwnedRelationships(layers, layerId);
            var nodes = ContractNodes(documents.semantic.nodes, relationships.Select(item => item.edge));
            var nodeIds = nodes.Select(node => node.id).ToHashSet(StringComparer.Ordinal);
            return new ArchitectorLayerBaselineRecord
            {
                name = layer.name,
                purpose = layer.purpose ?? string.Empty,
                nodes = nodes.Select(ArchitectorJson.Clone).ToList(),
                nodeLayouts = documents.layout.nodes.Where(node => nodeIds.Contains(node.nodeId)).Select(ArchitectorJson.Clone).ToList(),
                relationships = relationships.Select(item => ArchitectorJson.Clone(item.edge)).ToList(),
                relationshipLayouts = relationships.Select(item => ArchitectorJson.Clone(item.layout)).ToList()
            };
        }

        public static IReadOnlyList<ArchitectorBaselineChange> Compute(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            string layerId)
        {
            var layer = project.layers.First(candidate => candidate.id == layerId);
            var baseline = layer.approval?.baseline;
            if (string.IsNullOrEmpty(layer.approval?.semanticHash) || baseline == null)
                return Array.Empty<ArchitectorBaselineChange>();

            var changes = new List<ArchitectorBaselineChange>();
            if (!string.Equals(baseline.name, layer.name, StringComparison.Ordinal))
                changes.Add(new ArchitectorBaselineChange(ArchitectorBaselineChanges.Modified, "layer", layerId,
                    $"Layer renamed: '{baseline.name}' → '{layer.name}'"));
            if (!string.Equals(baseline.purpose ?? string.Empty, layer.purpose ?? string.Empty, StringComparison.Ordinal))
                changes.Add(new ArchitectorBaselineChange(ArchitectorBaselineChanges.Modified, "layer", layerId, "Purpose changed"));

            var ownedRelationships = OwnedRelationships(layers, layerId).Select(item => item.edge).ToArray();
            var contractNodes = ContractNodes(layers[layerId].semantic.nodes, ownedRelationships);
            var approvedNodes = ContractNodes(baseline.nodes, baseline.relationships);
            var currentNodes = contractNodes.ToDictionary(node => node.id, StringComparer.Ordinal);
            var baselineNodes = approvedNodes.ToDictionary(node => node.id, StringComparer.Ordinal);
            foreach (var node in approvedNodes.Where(node => !currentNodes.ContainsKey(node.id)))
                changes.Add(new ArchitectorBaselineChange(ArchitectorBaselineChanges.Removed, Subject(node), node.id,
                    $"{SubjectCaption(node)} removed: {node.title}"));
            foreach (var node in contractNodes)
            {
                if (!baselineNodes.TryGetValue(node.id, out var approved))
                {
                    changes.Add(new ArchitectorBaselineChange(ArchitectorBaselineChanges.Added, Subject(node), node.id,
                        $"{SubjectCaption(node)} added: {node.title}"));
                    continue;
                }
                var details = NodeDifferences(approved, node);
                if (details.Count > 0)
                    changes.Add(new ArchitectorBaselineChange(ArchitectorBaselineChanges.Modified, Subject(node), node.id,
                        $"{SubjectCaption(node)} changed: {node.title} ({string.Join(", ", details)})"));
            }

            var currentRelationships = ownedRelationships
                .ToDictionary(edge => edge.id, StringComparer.Ordinal);
            var baselineRelationships = baseline.relationships.ToDictionary(edge => edge.id, StringComparer.Ordinal);
            foreach (var edge in baseline.relationships.Where(edge => !currentRelationships.ContainsKey(edge.id)))
                changes.Add(new ArchitectorBaselineChange(ArchitectorBaselineChanges.Removed, "relationship", edge.id,
                    $"Relationship removed: {RelationshipCaption(edge, layerId, project, layers, baseline)}"));
            foreach (var edge in currentRelationships.Values.OrderBy(edge => edge.id, StringComparer.Ordinal))
            {
                if (!baselineRelationships.TryGetValue(edge.id, out var approved))
                {
                    changes.Add(new ArchitectorBaselineChange(ArchitectorBaselineChanges.Added, "relationship", edge.id,
                        $"Relationship added: {RelationshipCaption(edge, layerId, project, layers, baseline)}"));
                    continue;
                }
                if (!string.Equals(approved.kind, edge.kind, StringComparison.Ordinal)
                    || !string.Equals(approved.label ?? string.Empty, edge.label ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(approved.sourceNodeId, edge.sourceNodeId, StringComparison.Ordinal)
                    || !string.Equals(approved.targetNodeId, edge.targetNodeId, StringComparison.Ordinal))
                    changes.Add(new ArchitectorBaselineChange(ArchitectorBaselineChanges.Modified, "relationship", edge.id,
                        $"Relationship changed: {RelationshipCaption(edge, layerId, project, layers, baseline)}"));
            }
            return changes;
        }

        public static bool HasChangedResponsibilities(ArchitectorLayerBaselineRecord baseline, ArchitectorLayerDocument semantic)
        {
            if (baseline == null || semantic == null)
                return false;
            var approved = baseline.nodes.ToDictionary(node => node.id, StringComparer.Ordinal);
            return semantic.nodes.Any(node => approved.TryGetValue(node.id, out var record)
                                              && !string.Equals(record.responsibility ?? string.Empty, node.responsibility ?? string.Empty, StringComparison.Ordinal));
        }

        public static IReadOnlyList<ArchitectorNodeRecord> ContractNodes(
            IEnumerable<ArchitectorNodeRecord> nodes,
            IEnumerable<ArchitectorEdgeRecord> ownedRelationships)
        {
            var targetLayers = ownedRelationships.Select(edge => edge.targetLayerId).ToHashSet(StringComparer.Ordinal);
            return nodes
                .Where(node => !string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal)
                               || targetLayers.Contains(node.portal?.targetLayerId ?? string.Empty))
                .ToArray();
        }

        public static IReadOnlyList<(ArchitectorEdgeRecord edge, ArchitectorEdgeLayoutRecord layout)> OwnedRelationships(
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            string layerId)
        {
            return layers.Values
                .SelectMany(documents => documents.semantic.edges
                    .Where(edge => edge.sourceLayerId == layerId)
                    .Select(edge => (edge, layout: documents.layout.edges.First(layout => layout.edgeId == edge.id))))
                .OrderBy(item => item.edge.id, StringComparer.Ordinal)
                .ToArray();
        }

        private static List<string> NodeDifferences(ArchitectorNodeRecord approved, ArchitectorNodeRecord current)
        {
            var details = new List<string>();
            if (!string.Equals(approved.title, current.title, StringComparison.Ordinal))
                details.Add("title");
            if (!string.Equals(approved.responsibility ?? string.Empty, current.responsibility ?? string.Empty, StringComparison.Ordinal))
                details.Add("responsibility");
            if (!string.Equals(TypeIdentity(approved.type), TypeIdentity(current.type), StringComparison.Ordinal))
                details.Add("type");
            if (!string.Equals(TypeReferenceIdentity(approved.type?.baseType), TypeReferenceIdentity(current.type?.baseType), StringComparison.Ordinal))
                details.Add("base type");
            if (!string.Equals(InterfacesIdentity(approved.type), InterfacesIdentity(current.type), StringComparison.Ordinal))
                details.Add("interfaces");

            var approvedMembers = (approved.members ?? new List<ArchitectorMemberRecord>()).ToDictionary(member => member.id, StringComparer.Ordinal);
            var currentMembers = (current.members ?? new List<ArchitectorMemberRecord>()).ToDictionary(member => member.id, StringComparer.Ordinal);
            var added = currentMembers.Keys.Count(id => !approvedMembers.ContainsKey(id));
            var removed = approvedMembers.Keys.Count(id => !currentMembers.ContainsKey(id));
            var changed = currentMembers.Count(pair => approvedMembers.TryGetValue(pair.Key, out var member)
                                                       && (!string.Equals(member.declaration, pair.Value.declaration, StringComparison.Ordinal)
                                                           || !string.Equals(member.kind, pair.Value.kind, StringComparison.Ordinal)));
            if (added + removed + changed > 0)
                details.Add($"members +{added} −{removed} ~{changed}");
            return details;
        }

        private static string TypeIdentity(ArchitectorTypeRecord type)
        {
            if (type == null)
                return string.Empty;
            return string.Join("|",
                type.mode,
                type.kind,
                type.typeNamespace,
                type.name,
                string.Join(",", type.genericParameters ?? new List<string>()),
                string.Join(",", (type.modifiers ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal)),
                type.assemblyName,
                type.metadataName);
        }

        private static string TypeReferenceIdentity(ArchitectorTypeReferenceRecord reference)
        {
            return reference == null || string.IsNullOrWhiteSpace(reference.name)
                ? string.Empty
                : ArchitectorTypeReference.Key(reference);
        }

        private static string InterfacesIdentity(ArchitectorTypeRecord type)
        {
            return string.Join(",", (type?.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                .Select(ArchitectorTypeReference.Key)
                .OrderBy(value => value, StringComparer.Ordinal));
        }

        private static string Subject(ArchitectorNodeRecord node)
        {
            return string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal) ? "portal" : "node";
        }

        private static string SubjectCaption(ArchitectorNodeRecord node)
        {
            return string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal) ? "Portal" : "Node";
        }

        private static string RelationshipCaption(
            ArchitectorEdgeRecord edge,
            string localLayerId,
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            ArchitectorLayerBaselineRecord baseline)
        {
            return $"{EndpointCaption(edge.sourceLayerId, edge.sourceNodeId, localLayerId, project, layers, baseline)} → " +
                   $"{EndpointCaption(edge.targetLayerId, edge.targetNodeId, localLayerId, project, layers, baseline)} " +
                   $"({ArchitectorEdgeKinds.DisplayName(edge.kind)})";
        }

        private static string EndpointCaption(
            string layerId,
            string nodeId,
            string localLayerId,
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            ArchitectorLayerBaselineRecord baseline)
        {
            var title = layers.TryGetValue(layerId, out var documents)
                ? documents.semantic.nodes.FirstOrDefault(node => node.id == nodeId)?.title
                : null;
            if (title == null && string.Equals(layerId, localLayerId, StringComparison.Ordinal))
                title = baseline.nodes.FirstOrDefault(node => node.id == nodeId)?.title;
            title ??= nodeId;
            if (string.Equals(layerId, localLayerId, StringComparison.Ordinal))
                return title;
            var layerName = project.layers.FirstOrDefault(candidate => candidate.id == layerId)?.name ?? layerId;
            return $"{layerName}/{title}";
        }
    }
}
