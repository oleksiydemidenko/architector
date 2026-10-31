using System;
using System.Collections.Generic;

namespace Architector.Editor
{
    public sealed class ArchitectorNodeClipboard
    {
        private ArchitectorNodeTransferData data;

        public bool HasContent => data != null && data.nodes.Count > 0;

        public void Copy(ArchitectorRepository repository, string layerId, IReadOnlyCollection<string> nodeIds)
        {
            if (repository == null)
                throw new ArgumentNullException(nameof(repository));
            data = repository.CaptureNodes(layerId, nodeIds);
        }

        internal ArchitectorNodeTransferData RequireData()
        {
            return data ?? throw new ArchitectorDataException("The Architector clipboard is empty.");
        }
    }

    internal sealed class ArchitectorNodeTransferData
    {
        public readonly List<ArchitectorNodeRecord> nodes = new();
        public readonly List<ArchitectorNodeLayoutRecord> layouts = new();
        public readonly List<ArchitectorEdgeRecord> edges = new();
        public readonly List<ArchitectorEdgeLayoutRecord> edgeLayouts = new();
        public float minX;
        public float minY;
        public float maxX;
        public float maxY;
    }
}
