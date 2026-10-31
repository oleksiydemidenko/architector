using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public sealed partial class ArchitectorRepository
    {
        private readonly Dictionary<string, ArchitectorLayerApprovalStatus> approvalStatusCache =
            new(StringComparer.Ordinal);
        private int approvalStatusCacheRevision = -1;

        public ArchitectorLayerApprovalStatus GetLayerApprovalStatus(string layerId)
        {
            if (approvalStatusCacheRevision != Revision)
            {
                approvalStatusCache.Clear();
                approvalStatusCacheRevision = Revision;
            }
            if (approvalStatusCache.TryGetValue(layerId, out var cached))
                return cached;

            var layer = FindLayer(layerId)
                        ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var hash = ComputeSemanticHashForLayer(Project, LoadAllLayers(), layer.id);
            var state = ArchitectorApproval.State(layer, hash);
            var status = new ArchitectorLayerApprovalStatus(
                layer.id,
                layer.name,
                state,
                hash);
            approvalStatusCache.Add(layerId, status);
            return status;
        }

        public IReadOnlyList<ArchitectorLayerApprovalStatus> GetLayerApprovalStatuses(
            IEnumerable<string> layerIds = null)
        {
            var requested = layerIds == null
                ? Project.layers.Select(layer => layer.id)
                : layerIds.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal);
            return requested.Select(GetLayerApprovalStatus).ToArray();
        }

        public void ApproveLayers(IReadOnlyCollection<string> layerIds)
        {
            if (layerIds == null || layerIds.Count == 0)
                throw new ArchitectorDataException("At least one layer is required for approval.");
            var ids = layerIds.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length != layerIds.Count)
                throw new ArchitectorDataException("Layer approval contains an empty or duplicate ID.");
            var layers = LoadAllLayers();
            ArchitectorApproval.EnsureLayersCanBeApproved(Project, layers, ids);
            var hashes = ids.ToDictionary(
                id => id,
                id =>
                {
                    var layer = FindLayer(id)
                                ?? throw new ArchitectorDataException($"Layer '{id}' does not exist.");
                    return ComputeSemanticHashForLayer(Project, layers, id);
                },
                StringComparer.Ordinal);
            var stale = ids.Where(id =>
            {
                var layer = FindLayer(id);
                return !string.Equals(layer.approval?.semanticHash, hashes[id], StringComparison.Ordinal)
                       || ArchitectorBaselineDiff.HasChangedResponsibilities(layer.approval?.baseline, layers[id].semantic);
            }).ToHashSet(StringComparer.Ordinal);
            if (stale.Count == 0)
                return;

            ExecuteProjectChange(ids.Length == 1 ? "Approve Layer" : "Approve Layers", project =>
            {
                foreach (var id in ids)
                {
                    var layer = project.layers.First(candidate => candidate.id == id);
                    if (!stale.Contains(id))
                        continue;
                    layer.approval = new ArchitectorLayerApprovalRecord
                    {
                        semanticHash = hashes[id],
                        baseline = ArchitectorBaselineDiff.Capture(Project, layers, id)
                    };
                }
            });
        }

        public bool HasUnapprovedResponsibilities(string layerId)
        {
            var layer = FindLayer(layerId);
            if (string.IsNullOrEmpty(layer?.approval?.semanticHash))
                return false;
            return ArchitectorBaselineDiff.HasChangedResponsibilities(layer.approval.baseline, ReadLayer(layerId).semantic);
        }

        public void RevokeLayerApprovals(IReadOnlyCollection<string> layerIds)
        {
            if (layerIds == null || layerIds.Count == 0)
                throw new ArchitectorDataException("At least one layer is required to revoke approval.");
            var ids = layerIds.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length != layerIds.Count)
                throw new ArchitectorDataException("Layer approval revocation contains an empty or duplicate ID.");
            foreach (var id in ids)
            {
                if (FindLayer(id) == null)
                    throw new ArchitectorDataException($"Layer '{id}' does not exist.");
            }
            if (ids.All(id => string.IsNullOrEmpty(FindLayer(id).approval?.semanticHash)))
                return;

            ExecuteProjectChange(ids.Length == 1 ? "Revoke Layer Approval" : "Revoke Layer Approvals", project =>
            {
                foreach (var id in ids)
                    project.layers.First(layer => layer.id == id).approval = new ArchitectorLayerApprovalRecord();
            });
        }

        public IReadOnlyList<ArchitectorBaselineChange> GetBaselineChanges(string layerId)
        {
            if (FindLayer(layerId) == null)
                throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            return ArchitectorBaselineDiff.Compute(Project, LoadAllLayers(), layerId);
        }

        public void RevertLayerToApproved(string layerId)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            if (string.IsNullOrEmpty(layer.approval?.semanticHash))
                throw new ArchitectorDataException($"Layer '{layer.name}' has no approved baseline to revert to.");
            ExecuteRepositoryChange("Revert To Approved", (project, layers) =>
            {
                var record = project.layers.First(candidate => candidate.id == layerId);
                var baseline = record.approval.baseline;
                record.name = baseline.name;
                record.purpose = baseline.purpose ?? string.Empty;
                var documents = layers[layerId];

                RevertPortals(record, baseline, layers);
                RevertNodes(baseline, documents);
                RevertRelationships(layerId, baseline, layers);
            });
        }

        private void RevertPortals(
            ArchitectorLayerRecord layer,
            ArchitectorLayerBaselineRecord baseline,
            IDictionary<string, ArchitectorLayerDocuments> layers)
        {
            var documents = layers[layer.id];
            var approvedPortals = baseline.nodes.Where(IsPortal).ToDictionary(node => node.portal.pairId, StringComparer.Ordinal);
            foreach (var portal in documents.semantic.nodes.Where(IsPortal).ToArray())
            {
                if (approvedPortals.TryGetValue(portal.portal.pairId, out var approved)
                    && string.Equals(approved.portal.targetLayerId, portal.portal.targetLayerId, StringComparison.Ordinal))
                    continue;
                if (documents.semantic.edges.All(edge => edge.targetLayerId != portal.portal.targetLayerId)
                    || layers.TryGetValue(portal.portal.targetLayerId, out var consumer)
                    && consumer.semantic.edges.Any(edge => edge.targetLayerId == layer.id))
                    continue;
                RemoveNodes(documents, new HashSet<string>(new[] { portal.id }, StringComparer.Ordinal));
                if (layers.TryGetValue(portal.portal.targetLayerId, out var target))
                    RemoveNodes(target, new HashSet<string>(new[] { portal.portal.counterpartNodeId }, StringComparer.Ordinal));
                RemoveCrossRelationshipsBetween(layers, layer.id, portal.portal.targetLayerId);
            }

            var size = AlignNodeSizeToGrid(280f, 120f);
            foreach (var approved in approvedPortals.Values)
            {
                if (documents.semantic.nodes.Any(node => IsPortalTo(node, approved.portal.targetLayerId)))
                    continue;
                if (!layers.TryGetValue(approved.portal.targetLayerId, out var target))
                    throw new ArchitectorDataException(
                        $"Cannot revert '{layer.name}': the layer for Portal '{approved.title}' no longer exists.");
                if (target.semantic.nodes.Any(node => IsPortalTo(node, layer.id)))
                    throw new ArchitectorDataException(
                        $"Cannot revert '{layer.name}': layer '{approved.title}' already has another Portal pair to it.");
                documents.semantic.nodes.Add(ArchitectorJson.Clone(approved));
                var approvedLayout = baseline.nodeLayouts.First(node => node.nodeId == approved.id);
                var position = FindAvailablePosition(
                    documents.layout,
                    approvedLayout.x,
                    approvedLayout.y,
                    new ArchitectorSize(approvedLayout.width, approvedLayout.height),
                    null);
                documents.layout.nodes.Add(new ArchitectorNodeLayoutRecord
                {
                    nodeId = approved.id,
                    x = position.x,
                    y = position.y,
                    width = approvedLayout.width,
                    height = approvedLayout.height
                });
                AddPortalNode(
                    target,
                    approved.portal.counterpartNodeId,
                    approved.portal.pairId,
                    layer.id,
                    approved.id,
                    string.Equals(approved.portal.role, ArchitectorPortalRoles.Entry, StringComparison.Ordinal)
                        ? ArchitectorPortalRoles.Exit
                        : ArchitectorPortalRoles.Entry,
                    MakeUniqueNodeTitle(target.semantic, layer.name),
                    0f,
                    0f,
                    size);
            }
        }

        private void RevertNodes(ArchitectorLayerBaselineRecord baseline, ArchitectorLayerDocuments documents)
        {
            var currentLayouts = documents.layout.nodes.ToDictionary(node => node.nodeId, StringComparer.Ordinal);
            var portals = documents.semantic.nodes.Where(IsPortal).ToArray();
            var approvedNodes = baseline.nodes.Where(node => !IsPortal(node)).ToArray();
            documents.semantic.nodes.Clear();
            documents.semantic.nodes.AddRange(approvedNodes.Select(ArchitectorJson.Clone));
            documents.semantic.nodes.AddRange(portals);
            documents.layout.nodes.Clear();
            foreach (var node in approvedNodes.Concat(portals).Where(node => currentLayouts.ContainsKey(node.id)))
                documents.layout.nodes.Add(currentLayouts[node.id]);
            foreach (var node in approvedNodes.Where(node => !currentLayouts.ContainsKey(node.id)))
            {
                var approvedLayout = baseline.nodeLayouts.First(layout => layout.nodeId == node.id);
                var position = FindAvailablePosition(
                    documents.layout,
                    approvedLayout.x,
                    approvedLayout.y,
                    new ArchitectorSize(approvedLayout.width, approvedLayout.height),
                    null);
                documents.layout.nodes.Add(new ArchitectorNodeLayoutRecord
                {
                    nodeId = node.id,
                    x = position.x,
                    y = position.y,
                    width = approvedLayout.width,
                    height = approvedLayout.height
                });
            }
        }

        private static void RevertRelationships(
            string layerId,
            ArchitectorLayerBaselineRecord baseline,
            IDictionary<string, ArchitectorLayerDocuments> layers)
        {
            var restoredNodes = layers[layerId].semantic.nodes.Select(node => node.id).ToHashSet(StringComparer.Ordinal);
            foreach (var documents in layers.Values)
            {
                var removed = documents.semantic.edges
                    .Where(edge => edge.sourceLayerId == layerId
                                   || edge.targetLayerId == layerId && !restoredNodes.Contains(edge.targetNodeId))
                    .Select(edge => edge.id)
                    .ToHashSet(StringComparer.Ordinal);
                documents.semantic.edges.RemoveAll(edge => removed.Contains(edge.id));
                documents.layout.edges.RemoveAll(layout => removed.Contains(layout.edgeId));
            }

            foreach (var edge in baseline.relationships)
            {
                if (!layers.TryGetValue(edge.sourceLayerId, out var owner)
                    || !layers.TryGetValue(edge.targetLayerId, out var target)
                    || owner.semantic.nodes.All(node => node.id != edge.sourceNodeId)
                    || target.semantic.nodes.All(node => node.id != edge.targetNodeId))
                    throw new ArchitectorDataException(
                        $"Cannot revert: an endpoint of approved relationship '{edge.id}' no longer exists outside this layer.");
                owner.semantic.edges.Add(ArchitectorJson.Clone(edge));
                owner.layout.edges.Add(ArchitectorJson.Clone(
                    baseline.relationshipLayouts.FirstOrDefault(layout => layout.edgeId == edge.id)
                    ?? new ArchitectorEdgeLayoutRecord { edgeId = edge.id }));
            }
        }

        public bool IsAgentIntegrationEnabled(string integrationId)
        {
            var record = Settings.agent?.integrations?.FirstOrDefault(candidate =>
                string.Equals(candidate.id, integrationId, StringComparison.Ordinal));
            return record?.enabled ?? true;
        }

        private Dictionary<string, ArchitectorLayerDocuments> LoadAllLayers()
        {
            return Project.layers.ToDictionary(layer => layer.id, layer => ReadLayer(layer.id), StringComparer.Ordinal);
        }

        private static string ComputeSemanticHashForLayer(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            string layerId)
        {
            var layer = project.layers.First(candidate => candidate.id == layerId);
            var layerNames = project.layers.ToDictionary(candidate => candidate.id, candidate => candidate.name, StringComparer.Ordinal);
            var facts = layers.Values.SelectMany(documents => documents.semantic.edges)
                .Where(edge => edge.sourceLayerId == layerId && edge.targetLayerId != layerId)
                .Select(edge =>
                {
                    var source = layers[edge.sourceLayerId].semantic.nodes.First(node => node.id == edge.sourceNodeId);
                    var target = layers[edge.targetLayerId].semantic.nodes.First(node => node.id == edge.targetNodeId);
                    return $"{edge.id}|{edge.kind}|{edge.label}|{edge.sourceLayerId}|{layerNames[edge.sourceLayerId]}|{edge.sourceNodeId}|{source.title}|{edge.targetLayerId}|{layerNames[edge.targetLayerId]}|{edge.targetNodeId}|{target.title}";
                });
            var semantic = ArchitectorJson.Clone(layers[layerId].semantic);
            semantic.nodes = ArchitectorBaselineDiff.ContractNodes(semantic.nodes, semantic.edges).ToList();
            return ArchitectorApproval.ComputeSemanticHash(layer, semantic, facts);
        }
    }
}
