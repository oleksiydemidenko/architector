using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Architector.Editor
{
    public static class ArchitectorApprovalStates
    {
        public const string Draft = "Draft";
        public const string Approved = "Approved";
        public const string Changed = "Changed";
    }

    public readonly struct ArchitectorLayerApprovalStatus
    {
        public ArchitectorLayerApprovalStatus(string layerId, string layerName, string state, string currentHash)
        {
            LayerId = layerId;
            LayerName = layerName;
            State = state;
            CurrentHash = currentHash;
        }

        public string LayerId { get; }
        public string LayerName { get; }
        public string State { get; }
        public string CurrentHash { get; }
    }

    public static class ArchitectorApproval
    {
        public static void EnsureLayersCanBeApproved(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            IEnumerable<string> layerIds)
        {
            var unanchored = ArchitectorConformance.FindUnanchoredLinkedNodes(project, layers, layerIds)
                .FirstOrDefault();
            if (unanchored.Node == null)
                return;
            throw new ArchitectorDataException(
                $"Layer '{unanchored.Layer.name}' cannot be approved because Linked node '{unanchored.Node.title}' has no relationship.");
        }

        public static string ComputeSemanticHash(ArchitectorLayerRecord layer, ArchitectorLayerDocument semantic)
        {
            return ComputeSemanticHash(layer, semantic, Array.Empty<string>());
        }

        public static string ComputeSemanticHash(
            ArchitectorLayerRecord layer,
            ArchitectorLayerDocument semantic,
            IEnumerable<string> relatedRelationshipFacts)
        {
            if (layer == null)
                throw new ArgumentNullException(nameof(layer));
            if (semantic == null)
                throw new ArgumentNullException(nameof(semantic));

            var projection = new ApprovalProjection
            {
                layerId = layer.id ?? string.Empty,
                layerName = layer.name?.Trim() ?? string.Empty,
                purpose = layer.purpose?.Trim() ?? string.Empty,
                semantic = ArchitectorJson.Clone(semantic),
                relatedRelationships = (relatedRelationshipFacts ?? Array.Empty<string>())
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList()
            };
            projection.semantic.nodes = projection.semantic.nodes
                .OrderBy(node => node.id, StringComparer.Ordinal)
                .ToList();
            foreach (var node in projection.semantic.nodes)
            {
                node.responsibility = string.Empty;
                node.members = (node.members ?? new List<ArchitectorMemberRecord>())
                    .OrderBy(member => member.id, StringComparer.Ordinal)
                    .ToList();
                node.type ??= new ArchitectorTypeRecord();
                node.type.modifiers = (node.type.modifiers ?? new List<string>())
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList();
                node.type.sourceDeclarations = new List<ArchitectorSourceDeclarationRecord>();
                node.type.interfaces = (node.type.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                    .OrderBy(ArchitectorTypeReference.Key, StringComparer.Ordinal)
                    .ToList();
            }
            projection.semantic.edges = projection.semantic.edges
                .OrderBy(edge => edge.id, StringComparer.Ordinal)
                .ToList();

            var bytes = Encoding.UTF8.GetBytes(ArchitectorJson.Serialize(projection));
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            return "v1:" + string.Concat(hash.Select(value => value.ToString("x2")));
        }

        public static string State(ArchitectorLayerRecord layer, string currentHash)
        {
            if (layer?.approval == null || string.IsNullOrEmpty(layer.approval.semanticHash))
                return ArchitectorApprovalStates.Draft;
            return string.Equals(layer.approval.semanticHash, currentHash, StringComparison.Ordinal)
                ? ArchitectorApprovalStates.Approved
                : ArchitectorApprovalStates.Changed;
        }

        [Serializable]
        private sealed class ApprovalProjection
        {
            public string layerId = string.Empty;
            public string layerName = string.Empty;
            public string purpose = string.Empty;
            public ArchitectorLayerDocument semantic = new();
            public List<string> relatedRelationships = new();
        }
    }
}
