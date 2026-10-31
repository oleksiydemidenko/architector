using System;
using System.Collections.Generic;

namespace Architector.Editor
{
    public sealed class ArchitectorCanonicalBatch
    {
        private readonly Dictionary<string, string> documents = new(StringComparer.Ordinal);

        public ArchitectorCanonicalBatch(string actionName = "Apply Canonical Batch")
        {
            ActionName = string.IsNullOrWhiteSpace(actionName) ? "Apply Canonical Batch" : actionName.Trim();
        }

        public string ActionName { get; }
        public IReadOnlyDictionary<string, string> Documents => documents;
        public ArchitectorCanonicalBatch SetDocument(string relativePath, string json)
        {
            documents[NormalizePath(relativePath)] = json ?? throw new ArgumentNullException(nameof(json));
            return this;
        }

        public ArchitectorCanonicalBatch DeleteDocument(string relativePath)
        {
            documents[NormalizePath(relativePath)] = null;
            return this;
        }

        private static string NormalizePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArchitectorDataException("Canonical batch paths cannot be empty.");
            return relativePath.Replace('\\', '/');
        }
    }

    public readonly struct ArchitectorCanonicalBatchResult
    {
        public ArchitectorCanonicalBatchResult(
            int changedFileCount,
            int validatedFileCount,
            int layerCount,
            int portalPairCount,
            IReadOnlyList<ArchitectorApprovalImpact> approvalImpacts = null)
        {
            ChangedFileCount = changedFileCount;
            ValidatedFileCount = validatedFileCount;
            LayerCount = layerCount;
            PortalPairCount = portalPairCount;
            ApprovalImpacts = approvalImpacts ?? Array.Empty<ArchitectorApprovalImpact>();
        }

        public int ChangedFileCount { get; }
        public int ValidatedFileCount { get; }
        public int LayerCount { get; }
        public int PortalPairCount { get; }
        public IReadOnlyList<ArchitectorApprovalImpact> ApprovalImpacts { get; }
        public bool HasChanges => ChangedFileCount > 0;
    }

    public readonly struct ArchitectorApprovalImpact
    {
        public ArchitectorApprovalImpact(string layerId, string layerName, string change, string state)
        {
            LayerId = layerId;
            LayerName = layerName;
            Change = change;
            State = state;
        }

        public string LayerId { get; }
        public string LayerName { get; }
        public string Change { get; }
        public string State { get; }
    }

}
