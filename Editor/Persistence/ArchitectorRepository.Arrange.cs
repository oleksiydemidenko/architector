using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    // Arrange: positions from the canvas geometry (ArchitectorGraphArranger), applied as one layout change.
    public sealed partial class ArchitectorRepository
    {
        public void ArrangeNodes(
            string layerId,
            IReadOnlyCollection<string> nodeIds = null,
            IReadOnlyCollection<string> centers = null)
        {
            var view = LoadLayerView(layerId);
            var arranged = nodeIds == null ? null : new HashSet<string>(nodeIds, StringComparer.Ordinal);
            var portals = view.semantic.nodes.Where(IsPortal).Select(node => node.id).ToHashSet(StringComparer.Ordinal);
            var nodes = view.layout.nodes
                .Where(node => arranged == null || arranged.Contains(node.nodeId))
                .Select(node => new ArchitectorArrangeNode(node.nodeId, node.x, node.y, node.width, node.height, portals.Contains(node.nodeId)))
                .ToArray();
            if (nodes.Length == 0)
                return;
            var edges = view.semantic.edges
                .Select(edge => new ArchitectorArrangeEdge(edge.sourceNodeId, edge.targetNodeId, edge.kind))
                .ToArray();
            var positions = ArchitectorGraphArranger.Arrange(
                nodes,
                edges,
                Settings.geometry.minimumNodeGap,
                Settings.geometry.snapToGrid ? Settings.geometry.gridSize : 1f,
                Settings.arrange.maxGroupRelationships,
                centers,
                Settings.optimization.relationshipMerging,
                Settings.arrange.improvementSeconds);
            MoveNodes(layerId, positions, "Arrange Nodes");
        }
    }
}
