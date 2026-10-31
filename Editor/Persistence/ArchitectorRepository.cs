using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Architector.Editor
{
    public sealed partial class ArchitectorRepository
    {
        private readonly Stack<ArchitectorUndoRecord> undo = new();
        private readonly Stack<ArchitectorUndoRecord> redo = new();
        private readonly HashSet<string> unconnectedPortalPairIds = new(StringComparer.Ordinal);
        private int portalRelationshipIndexRevision = -1;
        private IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>>
            allPortalRelationshipRowsByLayerId =
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>>(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>>
            visiblePortalRelationshipRowsByLayerId =
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>>(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, string> relationshipCaptionsById =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, ArchitectorLayerDocuments> layerDocuments = new(StringComparer.Ordinal);
        private Dictionary<string, string> layerErrors = new(StringComparer.Ordinal);
        private Dictionary<string, string> diskFiles = new(StringComparer.Ordinal);
        private Dictionary<string, ArchitectorFileStamp> diskStamps = new(StringComparer.Ordinal);
        private Dictionary<string, ArchitectorFileStamp> rejectedDiskStamps;

        public ArchitectorRepository(ArchitectorPaths paths)
        {
            Paths = paths ?? throw new ArgumentNullException(nameof(paths));
            Load();
        }

        public event Action Changed;
        public event Action<string> ExternalChangesReloaded;

        public ArchitectorPaths Paths { get; }
        public ArchitectorProjectDocument Project { get; private set; }
        public ArchitectorSettingsDocument Settings { get; private set; }
        public int Revision { get; private set; }
        public string ExternalChangeError { get; private set; } = string.Empty;
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public string NextUndoName => CanUndo ? undo.Peek().name : string.Empty;
        public string NextRedoName => CanRedo ? redo.Peek().name : string.Empty;

        public void Load()
        {
            EnsureStorageExists();
            var files = ReadDiskFiles(out var stamps);
            Install(ArchitectorStorageCodec.Decode(files), ReadSettings(files), files, stamps);
            Revision++;
        }

        public ArchitectorCanonicalBatchResult DryRunCanonicalBatch(ArchitectorCanonicalBatch batch)
        {
            return ApplyCanonicalBatchCore(batch, true);
        }

        public ArchitectorCanonicalBatchResult ApplyCanonicalBatch(ArchitectorCanonicalBatch batch)
        {
            return ApplyCanonicalBatchCore(batch, false);
        }

        public ArchitectorCanonicalBatchResult ValidateExternalChanges()
        {
            var files = ReadDiskFiles(out _);
            var decoded = DecodeComplete(files);
            var portalPairs = ArchitectorValidation.ValidateRepository(decoded.Project, ReadSettings(files), decoded.Layers);
            return new ArchitectorCanonicalBatchResult(0, files.Count, decoded.Layers.Count, portalPairs);
        }

        public ArchitectorCanonicalBatchResult ReloadExternalChanges()
        {
            var files = ReadDiskFiles(out var stamps);
            var decoded = DecodeComplete(files);
            var settings = ReadSettings(files);
            var portalPairs = ArchitectorValidation.ValidateRepository(decoded.Project, settings, decoded.Layers);
            Install(decoded, settings, files, stamps);
            undo.Clear();
            redo.Clear();
            Revision++;
            Changed?.Invoke();
            return new ArchitectorCanonicalBatchResult(0, files.Count, decoded.Layers.Count, portalPairs);
        }

        public bool RefreshExternalChanges()
        {
            var currentStamps = ReadDiskStamps();
            if (StampsEqual(currentStamps, diskStamps))
            {
                rejectedDiskStamps = null;
                if (ExternalChangeError.Length > 0)
                {
                    ExternalChangeError = string.Empty;
                    Changed?.Invoke();
                }
                return false;
            }
            if (rejectedDiskStamps != null && StampsEqual(currentStamps, rejectedDiskStamps))
                return false;

            var files = ReadDiskFiles(out var stamps);
            if (FilesEqual(files, diskFiles))
            {
                diskStamps = stamps;
                rejectedDiskStamps = null;
                if (ExternalChangeError.Length > 0)
                {
                    ExternalChangeError = string.Empty;
                    Changed?.Invoke();
                }
                return false;
            }

            ArchitectorDecodedRepository decoded;
            ArchitectorSettingsDocument settings;
            try
            {
                decoded = DecodeComplete(files);
                settings = ReadSettings(files);
            }
            catch (ArchitectorDataException exception)
            {
                rejectedDiskStamps = stamps;
                ExternalChangeError = $"Architector files changed on disk but cannot be loaded: {exception.Message}";
                Changed?.Invoke();
                return false;
            }

            if (layerErrors.Count == 0
                && string.Equals(ArchitectorJson.Serialize(settings), ArchitectorJson.Serialize(Settings), StringComparison.Ordinal)
                && FilesEqual(
                    ArchitectorStorageCodec.Encode(decoded.Project, decoded.Layers),
                    ArchitectorStorageCodec.Encode(Project, layerDocuments)))
            {
                diskFiles = files;
                diskStamps = stamps;
                rejectedDiskStamps = null;
                if (ExternalChangeError.Length > 0)
                {
                    ExternalChangeError = string.Empty;
                    Changed?.Invoke();
                }
                return false;
            }

            var summary = DescribeExternalChanges(diskFiles, files, decoded.Project);
            Install(decoded, settings, files, stamps);
            undo.Clear();
            redo.Clear();
            Revision++;
            Changed?.Invoke();
            ExternalChangesReloaded?.Invoke(summary);
            return true;
        }

        public ArchitectorGroupRecord FindGroup(string id)
        {
            return Project.groups.FirstOrDefault(group => group.id == id);
        }

        public ArchitectorLayerRecord FindLayer(string id)
        {
            return Project.layers.FirstOrDefault(layer => layer.id == id);
        }

        public string GetEntryPath(string id)
        {
            return ArchitectorEntryPaths.Describe(Project, id);
        }

        public ArchitectorLayerDocuments LoadLayer(string layerId)
        {
            var documents = ReadLayer(layerId);
            return CloneLayer(documents);
        }

        private static ArchitectorLayerDocuments CloneLayer(ArchitectorLayerDocuments documents)
        {
            var clone = new ArchitectorLayerDocuments(
                ArchitectorJson.Clone(documents.semantic),
                ArchitectorJson.Clone(documents.layout));
            ArchitectorValidation.Validate(clone.semantic, clone.layout);
            return clone;
        }

        public ArchitectorLayerDocuments LoadLayerView(string layerId)
        {
            var current = LoadLayer(layerId);
            var projectedEdges = new List<ArchitectorEdgeRecord>();
            var projectedLayouts = new List<ArchitectorEdgeLayoutRecord>();
            foreach (var owner in Project.layers)
            {
                var ownerDocuments = owner.id == layerId ? current : ReadLayer(owner.id);
                foreach (var edge in ownerDocuments.semantic.edges.Where(edge =>
                             (edge.sourceLayerId == layerId || edge.targetLayerId == layerId)
                             && (current.layout.visibleRelationshipKinds
                                 & ArchitectorRelationshipVisibility.Flag(edge.kind)) != 0))
                {
                    var projection = ArchitectorJson.Clone(edge);
                    if (edge.sourceLayerId != edge.targetLayerId)
                    {
                        var remoteLayerId = edge.sourceLayerId == layerId
                            ? edge.targetLayerId
                            : edge.sourceLayerId;
                        var portal = current.semantic.nodes.Single(node => IsPortalTo(node, remoteLayerId));
                        if (edge.sourceLayerId != layerId)
                            projection.sourceNodeId = portal.id;
                        if (edge.targetLayerId != layerId)
                            projection.targetNodeId = portal.id;
                    }
                    projectedEdges.Add(projection);
                    projectedLayouts.Add(ArchitectorJson.Clone(
                        ownerDocuments.layout.edges.Single(layout => layout.edgeId == edge.id)));
                }
            }
            var hiddenPortalIds = current.semantic.nodes
                .Where(node => IsPortal(node) && !IsPortalEndpointVisible(layerId, node))
                .Select(node => node.id)
                .ToHashSet(StringComparer.Ordinal);
            var hiddenEdgeIds = projectedEdges
                .Where(edge => hiddenPortalIds.Contains(edge.sourceNodeId) || hiddenPortalIds.Contains(edge.targetNodeId))
                .Select(edge => edge.id)
                .ToHashSet(StringComparer.Ordinal);
            return new ArchitectorLayerDocuments(
                new ArchitectorLayerDocument
                {
                    schemaVersion = current.semantic.schemaVersion,
                    layerId = layerId,
                    nodes = current.semantic.nodes.Where(node => !hiddenPortalIds.Contains(node.id)).ToList(),
                    edges = projectedEdges.Where(edge => !hiddenEdgeIds.Contains(edge.id)).ToList()
                },
                new ArchitectorLayerLayoutDocument
                {
                    schemaVersion = current.layout.schemaVersion,
                    layerId = layerId,
                    nodes = current.layout.nodes.Where(node => !hiddenPortalIds.Contains(node.nodeId)).ToList(),
                    edges = projectedLayouts.Where(layout => !hiddenEdgeIds.Contains(layout.edgeId)).ToList()
                });
        }

        public bool IsPortalEndpointVisible(string layerId, ArchitectorNodeRecord portal)
        {
            if (!IsPortal(portal))
                return true;
            return !HasApprovalBaseline(layerId) || HasApprovalBaseline(portal.portal.targetLayerId);
        }

        public int GetHiddenPortalCount(string layerId)
        {
            return ReadLayer(layerId).semantic.nodes.Count(node => IsPortal(node) && !IsPortalEndpointVisible(layerId, node));
        }

        private bool HasApprovalBaseline(string layerId)
        {
            return !string.IsNullOrEmpty(FindLayer(layerId)?.approval?.semanticHash);
        }

        public string CreateEmptyNode(string layerId, float requestedX, float requestedY)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var nodeId = NewId("node");
            ExecuteLayerChange("Create Empty Node", layer, (semantic, layout) =>
            {
                var title = MakeUniqueNodeTitle(semantic, "New Node");
                var size = AlignNodeSizeToGrid(280f, 120f);
                var position = FindAvailablePosition(layout, requestedX, requestedY, size, null);
                semantic.nodes.Add(new ArchitectorNodeRecord
                {
                    id = nodeId,
                    title = title,
                    type = new ArchitectorTypeRecord { mode = "none" }
                });
                layout.nodes.Add(new ArchitectorNodeLayoutRecord
                {
                    nodeId = nodeId,
                    x = position.x,
                    y = position.y,
                    width = size.width,
                    height = size.height
                });
            });
            return nodeId;
        }

        public void RenameEmptyNode(string layerId, string nodeId, string title)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteLayerChange("Rename Empty Node", layer, (semantic, _) =>
            {
                var node = semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeId)
                           ?? throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                if (IsPortal(node) || !string.Equals(node.type?.mode, "none", StringComparison.Ordinal))
                    throw new ArchitectorDataException("Only Empty Nodes can be renamed with this command.");
                node.title = title?.Trim() ?? string.Empty;
            });
        }

        public void UpdateNodeResponsibility(string layerId, string nodeId, string responsibility)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var normalized = ArchitectorNodeResponsibility.Normalize(responsibility);
            ExecuteLayerChange("Change Node Responsibility", layer, (semantic, _) =>
            {
                var node = semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeId)
                           ?? throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                if (IsPortal(node))
                    throw new ArchitectorDataException("Layer Portals cannot have a responsibility.");
                node.responsibility = normalized;
            });
        }

        public string ConvertEmptyNodeToManual(string layerId, string nodeId, string replacementTitle = null)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var replacementId = NewId("node");
            ExecuteLayerSetChange("Convert Empty Node To Manual Type", Project.layers, documentsByLayerId =>
            {
                var semantic = documentsByLayerId[layerId].semantic;
                var layout = documentsByLayerId[layerId].layout;
                var semanticIndex = semantic.nodes.FindIndex(candidate => candidate.id == nodeId);
                if (semanticIndex < 0)
                    throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                var emptyNode = semantic.nodes[semanticIndex];
                if (IsPortal(emptyNode) || !string.Equals(emptyNode.type?.mode, "none", StringComparison.Ordinal))
                    throw new ArchitectorDataException("Only Empty Nodes can be converted to a Manual Type.");
                var nodeLayout = layout.nodes.First(candidate => candidate.nodeId == nodeId);
                var convertedTitle = replacementTitle == null ? emptyNode.title : replacementTitle.Trim();
                var typeName = MakeUniqueManualTypeName(semantic, MakeTypeName(convertedTitle));

                semantic.nodes[semanticIndex] = new ArchitectorNodeRecord
                {
                    id = replacementId,
                    title = convertedTitle,
                    responsibility = emptyNode.responsibility,
                    type = new ArchitectorTypeRecord
                    {
                        mode = "declared",
                        kind = "class",
                        name = typeName,
                        modifiers = new List<string> { "public" }
                    }
                };
                nodeLayout.nodeId = replacementId;
                RemoveRelationshipsForNodes(
                    documentsByLayerId,
                    layerId,
                    new HashSet<string>(new[] { nodeId }, StringComparer.Ordinal));
            });
            return replacementId;
        }

        public string CreateManualNode(string layerId, float requestedX, float requestedY)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var nodeId = NewId("node");
            ExecuteLayerChange("Create Manual Type", layer, (semantic, layout) =>
            {
                var title = MakeUniqueNodeTitle(semantic, "New Type");
                var typeName = MakeUniqueManualTypeName(semantic, "NewType");
                var size = AlignNodeSizeToGrid(280f, 160f);
                var position = FindAvailablePosition(layout, requestedX, requestedY, size, null);
                semantic.nodes.Add(new ArchitectorNodeRecord
                {
                    id = nodeId,
                    title = title,
                    type = new ArchitectorTypeRecord
                    {
                        mode = "declared",
                        kind = "class",
                        name = typeName,
                        modifiers = new List<string> { "public" }
                    }
                });
                layout.nodes.Add(new ArchitectorNodeLayoutRecord
                {
                    nodeId = nodeId,
                    x = position.x,
                    y = position.y,
                    width = size.width,
                    height = size.height
                });
            });
            return nodeId;
        }

        public string CreateLinkedNode(
            string layerId,
            float requestedX,
            float requestedY,
            ArchitectorIndexedType indexedType)
        {
            if (indexedType == null)
                throw new ArgumentNullException(nameof(indexedType));
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var nodeId = NewId("node");
            ExecuteLayerChange("Create Linked Type", layer, (semantic, layout) =>
            {
                EnsureLinkedTypeIsUnique(semantic, indexedType, null);
                var size = AlignNodeSizeToGrid(280f, 160f);
                var position = FindAvailablePosition(layout, requestedX, requestedY, size, null);
                semantic.nodes.Add(CreateLinkedNodeRecord(nodeId, MakeUniqueNodeTitle(semantic, indexedType.DisplayName), indexedType));
                layout.nodes.Add(new ArchitectorNodeLayoutRecord
                {
                    nodeId = nodeId,
                    x = position.x,
                    y = position.y,
                    width = size.width,
                    height = size.height
                });
            });
            return nodeId;
        }

        public string ConvertEmptyNodeToLinked(string layerId, string nodeId, ArchitectorIndexedType indexedType)
        {
            if (indexedType == null)
                throw new ArgumentNullException(nameof(indexedType));
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var replacementId = NewId("node");
            ExecuteLayerSetChange("Link Existing Type", Project.layers, documentsByLayerId =>
            {
                var semantic = documentsByLayerId[layerId].semantic;
                var layout = documentsByLayerId[layerId].layout;
                var semanticIndex = semantic.nodes.FindIndex(candidate => candidate.id == nodeId);
                if (semanticIndex < 0)
                    throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                var emptyNode = semantic.nodes[semanticIndex];
                if (IsPortal(emptyNode) || !string.Equals(emptyNode.type?.mode, "none", StringComparison.Ordinal))
                    throw new ArchitectorDataException("Only Empty Nodes can be linked to an indexed type.");
                EnsureLinkedTypeIsUnique(semantic, indexedType, nodeId);
                semantic.nodes[semanticIndex] = CreateLinkedNodeRecord(replacementId, emptyNode.title, indexedType);
                semantic.nodes[semanticIndex].responsibility = emptyNode.responsibility;
                layout.nodes.First(candidate => candidate.nodeId == nodeId).nodeId = replacementId;
                RemoveRelationshipsForNodes(
                    documentsByLayerId,
                    layerId,
                    new HashSet<string>(new[] { nodeId }, StringComparer.Ordinal));
            });
            return replacementId;
        }

        public void LinkManualNode(
            string layerId,
            string nodeId,
            ArchitectorIndexedType indexedType,
            IReadOnlyList<ArchitectorIndexedMember> indexedMembers = null)
        {
            if (indexedType == null)
                throw new ArgumentNullException(nameof(indexedType));
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteLayerChange("Link Manual Type", layer, (semantic, _) =>
            {
                var index = semantic.nodes.FindIndex(candidate => candidate.id == nodeId);
                if (index < 0)
                    throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                var manual = semantic.nodes[index];
                if (!string.Equals(manual.type?.mode, "declared", StringComparison.Ordinal))
                    throw new ArchitectorDataException("Only Manual Types can use this link operation.");
                EnsureLinkedTypeIsUnique(semantic, indexedType, nodeId);
                var linked = CreateLinkedNodeRecord(nodeId, manual.title, indexedType);
                linked.responsibility = manual.responsibility;
                linked.type.baseType = manual.type.baseType == null ? null : ArchitectorJson.Clone(manual.type.baseType);
                linked.type.interfaces = (manual.type.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                    .Select(ArchitectorJson.Clone)
                    .ToList();
                var candidates = indexedMembers ?? Array.Empty<ArchitectorIndexedMember>();
                linked.members = manual.members.Select(member =>
                {
                    var matches = candidates.Where(candidate =>
                        string.Equals(candidate.Kind, member.kind, StringComparison.Ordinal)
                        && string.Equals(candidate.Declaration, member.declaration, StringComparison.Ordinal)).ToArray();
                    if (matches.Length != 1)
                        throw new ArchitectorDataException(
                            $"Draft member '{member.declaration}' requires one exact linked-member match before '{manual.title}' can be linked.");
                    return new ArchitectorMemberRecord
                    {
                        id = member.id,
                        kind = matches[0].Kind,
                        declaration = matches[0].Declaration,
                        symbolKey = matches[0].SymbolKey
                    };
                }).ToList();
                semantic.nodes[index] = linked;
            });
        }

        private static ArchitectorNodeRecord CreateLinkedNodeRecord(
            string nodeId,
            string title,
            ArchitectorIndexedType indexedType)
        {
            return new ArchitectorNodeRecord
            {
                id = nodeId,
                title = title,
                type = new ArchitectorTypeRecord
                {
                    mode = "linked",
                    kind = indexedType.Kind,
                    typeNamespace = indexedType.Namespace,
                    name = indexedType.DisplayName,
                    genericParameters = indexedType.GenericParameters.ToList(),
                    modifiers = indexedType.Modifiers.ToList(),
                    assemblyName = indexedType.AssemblyName,
                    metadataName = indexedType.MetadataName,
                    sourceDeclarations = indexedType.SourceDeclarations
                        .Select(declaration => ArchitectorJson.Clone(declaration))
                        .ToList()
                }
            };
        }

        private static void EnsureLinkedTypeIsUnique(
            ArchitectorLayerDocument semantic,
            ArchitectorIndexedType indexedType,
            string ignoredNodeId)
        {
            var key = indexedType.Key;
            if (semantic.nodes.Any(node => node.id != ignoredNodeId
                                           && string.Equals(node.type?.mode, "linked", StringComparison.Ordinal)
                                           && string.Equals(
                                               ArchitectorTypeIdentity.Key(node.type.assemblyName, node.type.metadataName),
                                               key,
                                               StringComparison.Ordinal)))
                throw new ArchitectorDataException($"Type '{indexedType.FullDisplayName}' is already represented in this layer.");
        }

        public string AddLinkedMember(string layerId, string nodeId, ArchitectorIndexedMember indexedMember)
        {
            if (indexedMember == null)
                throw new ArgumentNullException(nameof(indexedMember));
            return AddLinkedMembers(layerId, nodeId, new[] { indexedMember }).Single();
        }

        public IReadOnlyList<string> AddLinkedMembers(
            string layerId,
            string nodeId,
            IReadOnlyCollection<ArchitectorIndexedMember> indexedMembers)
        {
            if (indexedMembers == null)
                throw new ArgumentNullException(nameof(indexedMembers));
            if (indexedMembers.Count == 0)
                return Array.Empty<string>();
            if (indexedMembers.Any(member => member == null))
                throw new ArgumentException("Linked contract member selection cannot contain null values.", nameof(indexedMembers));
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var pending = indexedMembers.Select(member => (member, id: NewId("member"))).ToArray();
            ExecuteLayerChange(pending.Length == 1 ? "Add Linked Contract Member" : "Add Linked Contract Members", layer, (semantic, _) =>
            {
                var node = semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeId)
                           ?? throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                if (!string.Equals(node.type?.mode, "linked", StringComparison.Ordinal))
                    throw new ArchitectorDataException("Contract members can only be linked to a Linked Type node.");
                node.members ??= new List<ArchitectorMemberRecord>();
                var existingKeys = new HashSet<string>(node.members.Select(member => member.symbolKey), StringComparer.Ordinal);
                foreach (var item in pending)
                {
                    if (!existingKeys.Add(item.member.SymbolKey))
                        throw new ArchitectorDataException($"Member '{item.member.Declaration}' is already selected.");
                    node.members.Add(new ArchitectorMemberRecord
                    {
                        id = item.id,
                        kind = item.member.Kind,
                        declaration = item.member.Declaration,
                        symbolKey = item.member.SymbolKey
                    });
                }
                node.members = ArchitectorMemberOrder.SortStable(node.members);
            });
            return pending.Select(item => item.id).ToArray();
        }

        public void RemoveLinkedMember(string layerId, string nodeId, string memberId)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteLayerChange("Remove Linked Contract Member", layer, (semantic, _) =>
            {
                var node = semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeId)
                           ?? throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                if (!string.Equals(node.type?.mode, "linked", StringComparison.Ordinal))
                    throw new ArchitectorDataException("Contract members can only be removed from a Linked Type node with this command.");
                if (node.members == null || node.members.RemoveAll(member => member.id == memberId) != 1)
                    throw new ArchitectorDataException($"Contract member '{memberId}' does not exist on node '{node.title}'.");
            });
        }

        public ArchitectorPortalPair CreateLayerPortalPair(
            string sourceLayerId,
            string targetLayerId,
            float requestedX,
            float requestedY)
        {
            if (string.Equals(sourceLayerId, targetLayerId, StringComparison.Ordinal))
                throw new ArchitectorDataException("A layer cannot contain a Portal to itself.");
            var sourceLayer = FindLayer(sourceLayerId)
                              ?? throw new ArchitectorDataException($"Layer '{sourceLayerId}' does not exist.");
            var targetLayer = FindLayer(targetLayerId)
                              ?? throw new ArchitectorDataException($"Layer '{targetLayerId}' does not exist.");
            var pairId = NewId("portal-pair");
            var sourceNodeId = NewId("node");
            var targetNodeId = NewId("node");
            var result = new ArchitectorPortalPair(pairId, sourceNodeId, targetNodeId);

            unconnectedPortalPairIds.Add(pairId);
            ExecuteLayerSetChange("Create Layer Portal", new[] { sourceLayer, targetLayer }, documentsByLayerId =>
            {
                var source = documentsByLayerId[sourceLayer.id];
                var target = documentsByLayerId[targetLayer.id];
                if (source.semantic.nodes.Any(node => IsPortalTo(node, targetLayer.id))
                    || target.semantic.nodes.Any(node => IsPortalTo(node, sourceLayer.id)))
                    throw new ArchitectorDataException(
                        $"A Layer Portal pair between '{sourceLayer.name}' and '{targetLayer.name}' already exists.");

                var size = AlignNodeSizeToGrid(280f, 120f);
                AddPortalNode(
                    source,
                    sourceNodeId,
                    pairId,
                    targetLayer.id,
                    targetNodeId,
                    ArchitectorPortalRoles.Exit,
                    MakeUniqueNodeTitle(source.semantic, targetLayer.name),
                    requestedX,
                    requestedY,
                    size);
                AddPortalNode(
                    target,
                    targetNodeId,
                    pairId,
                    sourceLayer.id,
                    sourceNodeId,
                    ArchitectorPortalRoles.Entry,
                    MakeUniqueNodeTitle(target.semantic, sourceLayer.name),
                    requestedX,
                    requestedY,
                    size);
            });
            return result;
        }

        public void UpdateManualNode(
            string layerId,
            string nodeId,
            string title,
            string kind,
            string typeNamespace,
            string typeName,
            IReadOnlyList<string> genericParameters,
            IReadOnlyList<string> modifiers,
            IReadOnlyList<ArchitectorMemberRecord> members,
            ArchitectorTypeReferenceRecord baseType = null,
            IReadOnlyList<ArchitectorTypeReferenceRecord> interfaces = null)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteLayerChange("Edit Manual Type", layer, (semantic, _) =>
            {
                var node = semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeId)
                           ?? throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                if (!string.Equals(node.type?.mode, "declared", StringComparison.Ordinal))
                    throw new ArchitectorDataException("Only manually declared nodes can be edited as a manual type.");

                node.title = title?.Trim() ?? string.Empty;
                node.type.kind = kind?.Trim() ?? string.Empty;
                node.type.typeNamespace = typeNamespace?.Trim() ?? string.Empty;
                node.type.name = typeName?.Trim() ?? string.Empty;
                node.type.genericParameters = genericParameters?.Select(value => value.Trim()).Where(value => value.Length > 0).ToList() ?? new List<string>();
                node.type.modifiers = modifiers?.Select(value => value.Trim()).Where(value => value.Length > 0).ToList() ?? new List<string>();
                node.type.assemblyName = string.Empty;
                node.type.metadataName = string.Empty;
                node.type.sourceDeclarations = new List<ArchitectorSourceDeclarationRecord>();
                node.type.baseType = baseType == null ? null : ArchitectorJson.Clone(baseType);
                node.type.interfaces = (interfaces ?? Array.Empty<ArchitectorTypeReferenceRecord>())
                    .Where(reference => reference != null)
                    .Select(ArchitectorJson.Clone)
                    .OrderBy(ArchitectorTypeReference.Key, StringComparer.Ordinal)
                    .ToList();
                node.members = ArchitectorMemberOrder.SortStable(
                    members?.Select(member => ArchitectorJson.Clone(member)) ?? Enumerable.Empty<ArchitectorMemberRecord>());
            });
        }

        public void UpdateTypeFacts(
            string layerId,
            string nodeId,
            ArchitectorTypeReferenceRecord baseType,
            IReadOnlyList<ArchitectorTypeReferenceRecord> interfaces)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteLayerChange("Edit Type Facts", layer, (semantic, _) =>
            {
                var node = semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeId)
                           ?? throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                if (string.Equals(node.type?.mode, "none", StringComparison.Ordinal))
                    throw new ArchitectorDataException("Empty and Portal nodes cannot contain type facts.");
                node.type.baseType = baseType == null ? null : ArchitectorJson.Clone(baseType);
                node.type.interfaces = (interfaces ?? Array.Empty<ArchitectorTypeReferenceRecord>())
                    .Where(reference => reference != null)
                    .Select(ArchitectorJson.Clone)
                    .OrderBy(ArchitectorTypeReference.Key, StringComparer.Ordinal)
                    .ToList();
            });
        }

        public void MoveNode(string layerId, string nodeId, float requestedX, float requestedY)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteLayerChange("Move Node", layer, (semantic, layout) =>
            {
                var nodeLayout = layout.nodes.FirstOrDefault(candidate => candidate.nodeId == nodeId)
                                 ?? throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                var position = FindAvailablePosition(
                    layout,
                    requestedX,
                    requestedY,
                    new ArchitectorSize(nodeLayout.width, nodeLayout.height),
                    nodeId);
                nodeLayout.x = position.x;
                nodeLayout.y = position.y;
            });
        }

        public void MoveNodes(string layerId, IReadOnlyDictionary<string, ArchitectorPoint> requestedPositions)
        {
            if (requestedPositions == null || requestedPositions.Count == 0)
                return;
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteLayerChange("Move Nodes", layer, (_, layout) =>
            {
                var knownIds = new HashSet<string>(layout.nodes.Select(node => node.nodeId), StringComparer.Ordinal);
                foreach (var nodeId in requestedPositions.Keys)
                {
                    if (!knownIds.Contains(nodeId))
                        throw new ArchitectorDataException($"Node '{nodeId}' does not exist in layer '{layer.name}'.");
                }

                var positions = FindAvailableGroupPositions(layout, requestedPositions);
                foreach (var nodeLayout in layout.nodes)
                {
                    if (!positions.TryGetValue(nodeLayout.nodeId, out var position))
                        continue;
                    nodeLayout.x = position.x;
                    nodeLayout.y = position.y;
                }
            });
        }

        public void DeleteNode(string layerId, string nodeId)
        {
            DeleteNodes(layerId, new[] { nodeId });
        }

        public void DeleteNodes(string layerId, IReadOnlyCollection<string> nodeIds)
        {
            if (nodeIds == null || nodeIds.Count == 0)
                return;
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var ids = new HashSet<string>(nodeIds, StringComparer.Ordinal);
            var currentDocuments = LoadLayer(layerId);
            if (currentDocuments.semantic.nodes.Count(node => ids.Contains(node.id)) != ids.Count)
                throw new ArchitectorDataException("One or more selected nodes no longer exist.");
            var selectedPortals = currentDocuments.semantic.nodes
                .Where(node => ids.Contains(node.id) && IsPortal(node))
                .ToArray();
            if (selectedPortals.Length > 0)
            {
                DeleteNodesAndPortalCounterparts(layer, ids, selectedPortals);
                return;
            }

            ExecuteLayerSetChange("Delete Nodes", Project.layers, documentsByLayerId =>
            {
                var documents = documentsByLayerId[layerId];
                var semantic = documents.semantic;
                var layout = documents.layout;
                if (semantic.nodes.Count(node => ids.Contains(node.id)) != ids.Count)
                    throw new ArchitectorDataException("One or more selected nodes no longer exist.");
                semantic.nodes.RemoveAll(node => ids.Contains(node.id));
                layout.nodes.RemoveAll(node => ids.Contains(node.nodeId));
                RemoveRelationshipsForNodes(documentsByLayerId, layerId, ids);
            });
        }

        public string CreateEdge(
            string layerId,
            string sourceNodeId,
            string targetNodeId,
            string preferredTargetSide)
        {
            return CreateEdge(
                layerId,
                sourceNodeId,
                targetNodeId,
                ArchitectorEdgeKinds.Dependency,
                preferredTargetSide);
        }

        public string CreateEdge(
            string layerId,
            string sourceNodeId,
            string targetNodeId,
            string kind,
            string preferredTargetSide)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            kind = NormalizeEdgeKind(kind);
            var edgeId = NewId("edge");
            var displayKind = ArchitectorEdgeKinds.DisplayName(kind);
            ExecuteLayerChange($"Create {displayKind}", layer, (semantic, layout) =>
            {
                if (semantic.nodes.All(node => node.id != sourceNodeId) || semantic.nodes.All(node => node.id != targetNodeId))
                    throw new ArchitectorDataException("The source or target node no longer exists.");
                if (sourceNodeId == targetNodeId)
                    throw new ArchitectorDataException("A node cannot have a relationship with itself.");
                var sourceTitle = semantic.nodes.First(node => node.id == sourceNodeId).title;
                var targetTitle = semantic.nodes.First(node => node.id == targetNodeId).title;
                if (semantic.nodes.Any(node =>
                        (node.id == sourceNodeId || node.id == targetNodeId) && IsPortal(node)))
                    throw new ArchitectorDataException("Portals display relationships and cannot be relationship endpoints.");
                if (semantic.edges.Any(edge =>
                        edge.sourceNodeId == sourceNodeId
                        && edge.targetNodeId == targetNodeId
                        && string.Equals(edge.kind, kind, StringComparison.Ordinal)))
                    throw new ArchitectorDataException(
                        $"A {displayKind} relationship from '{sourceTitle}' to '{targetTitle}' already exists.");
                semantic.edges.Add(new ArchitectorEdgeRecord
                {
                    id = edgeId,
                    sourceLayerId = layerId,
                    sourceNodeId = sourceNodeId,
                    targetLayerId = layerId,
                    targetNodeId = targetNodeId,
                    kind = kind
                });
                layout.edges.Add(new ArchitectorEdgeLayoutRecord
                {
                    edgeId = edgeId,
                    preferredSourceSide = "auto",
                    preferredTargetSide = NormalizeEdgeSide(preferredTargetSide)
                });
                layout.visibleRelationshipKinds |= ArchitectorRelationshipVisibility.Flag(kind);
            });
            return edgeId;
        }

        public string CreateCrossLayerEdge(
            string sourceLayerId,
            string sourceNodeId,
            string targetLayerId,
            string targetNodeId,
            string kind,
            string label = "")
        {
            if (sourceLayerId == targetLayerId)
                return CreateEdge(sourceLayerId, sourceNodeId, targetNodeId, kind, "auto");
            var sourceLayer = FindLayer(sourceLayerId)
                              ?? throw new ArchitectorDataException($"Layer '{sourceLayerId}' does not exist.");
            var targetLayer = FindLayer(targetLayerId)
                              ?? throw new ArchitectorDataException($"Layer '{targetLayerId}' does not exist.");
            kind = NormalizeEdgeKind(kind);
            var edgeId = NewId("edge");
            ExecuteLayerSetChange(
                $"Create Cross-Layer {ArchitectorEdgeKinds.DisplayName(kind)}",
                new[] { sourceLayer, targetLayer },
                documents =>
                {
                    var source = documents[sourceLayerId];
                    var target = documents[targetLayerId];
                    var sourceNode = source.semantic.nodes.FirstOrDefault(node => node.id == sourceNodeId && !IsPortal(node))
                                     ?? throw new ArchitectorDataException("The source node does not exist or is a Portal.");
                    var targetNode = target.semantic.nodes.FirstOrDefault(node => node.id == targetNodeId && !IsPortal(node))
                                     ?? throw new ArchitectorDataException("The target node does not exist or is a Portal.");
                    var sourcePortal = source.semantic.nodes.FirstOrDefault(node => IsPortalTo(node, targetLayerId));
                    var targetPortal = target.semantic.nodes.FirstOrDefault(node => IsPortalTo(node, sourceLayerId));
                    if (sourcePortal == null && targetPortal == null)
                    {
                        var pairId = NewId("portal-pair");
                        var sourcePortalId = NewId("node");
                        var targetPortalId = NewId("node");
                        var size = AlignNodeSizeToGrid(280f, 120f);
                        AddPortalNode(source, sourcePortalId, pairId, targetLayerId, targetPortalId,
                            ArchitectorPortalRoles.Exit, MakeUniqueNodeTitle(source.semantic, targetLayer.name),
                            0f, 0f, size);
                        AddPortalNode(target, targetPortalId, pairId, sourceLayerId, sourcePortalId,
                            ArchitectorPortalRoles.Entry, MakeUniqueNodeTitle(target.semantic, sourceLayer.name),
                            0f, 0f, size);
                    }
                    else if (sourcePortal == null || targetPortal == null)
                    {
                        throw new ArchitectorDataException("The Portal pair between the endpoint layers is incomplete.");
                    }
                    if (source.semantic.edges.Any(edge =>
                            edge.sourceNodeId == sourceNodeId
                            && edge.targetLayerId == targetLayerId
                            && edge.targetNodeId == targetNodeId
                            && edge.kind == kind))
                        throw new ArchitectorDataException(
                            $"A {ArchitectorEdgeKinds.DisplayName(kind)} relationship from '{sourceNode.title}' to '{targetNode.title}' already exists.");
                    source.semantic.edges.Add(new ArchitectorEdgeRecord
                    {
                        id = edgeId,
                        sourceLayerId = sourceLayerId,
                        sourceNodeId = sourceNodeId,
                        targetLayerId = targetLayerId,
                        targetNodeId = targetNodeId,
                        kind = kind,
                        label = label?.Trim() ?? string.Empty
                    });
                    source.layout.edges.Add(new ArchitectorEdgeLayoutRecord { edgeId = edgeId });
                    source.layout.visibleRelationshipKinds |= ArchitectorRelationshipVisibility.Flag(kind);
                    target.layout.visibleRelationshipKinds |= ArchitectorRelationshipVisibility.Flag(kind);
                });
            return edgeId;
        }

        public void DeleteEdge(string layerId, string edgeId)
        {
            var owner = FindRelationshipOwner(edgeId);
            ExecuteLayerChange("Delete Relationship", owner, (semantic, layout) =>
            {
                if (semantic.edges.RemoveAll(edge => edge.id == edgeId) == 0)
                    throw new ArchitectorDataException($"Relationship '{edgeId}' does not exist.");
                layout.edges.RemoveAll(edge => edge.edgeId == edgeId);
            });
        }

        public void UpdateEdgeKind(string layerId, string edgeId, string kind, string label = null)
        {
            var existing = FindRelationship(edgeId)
                           ?? throw new ArchitectorDataException($"Relationship '{edgeId}' does not exist.");
            var layer = FindRelationshipOwner(edgeId);
            var targetLayer = FindLayer(existing.targetLayerId);
            kind = NormalizeEdgeKind(kind);
            ExecuteLayerSetChange("Change Relationship Type", new[] { layer, targetLayer }, documentsByLayerId =>
            {
                var semantic = documentsByLayerId[layer.id].semantic;
                var edge = semantic.edges.FirstOrDefault(candidate => candidate.id == edgeId)
                           ?? throw new ArchitectorDataException($"Relationship '{edgeId}' does not exist in layer '{layer.name}'.");
                var sourceTitle = semantic.nodes.First(node => node.id == edge.sourceNodeId).title;
                var targetTitle = string.Equals(edge.targetLayerId, layer.id, StringComparison.Ordinal)
                    ? semantic.nodes.First(node => node.id == edge.targetNodeId).title
                    : documentsByLayerId[edge.targetLayerId].semantic.nodes.First(node => node.id == edge.targetNodeId).title;
                var displayKind = ArchitectorEdgeKinds.DisplayName(kind);
                if (semantic.edges.Any(candidate =>
                        candidate.id != edge.id
                        && candidate.sourceNodeId == edge.sourceNodeId
                        && candidate.targetLayerId == edge.targetLayerId
                        && candidate.targetNodeId == edge.targetNodeId
                        && string.Equals(candidate.kind, kind, StringComparison.Ordinal)))
                {
                    throw new ArchitectorDataException(
                        $"A {displayKind} relationship from '{sourceTitle}' to '{targetTitle}' already exists.");
                }
                edge.kind = kind;
                if (label != null)
                    edge.label = label.Trim();
                documentsByLayerId[layer.id].layout.visibleRelationshipKinds |= ArchitectorRelationshipVisibility.Flag(kind);
                documentsByLayerId[edge.targetLayerId].layout.visibleRelationshipKinds |= ArchitectorRelationshipVisibility.Flag(kind);
            });
        }

        public void SetRelationshipVisibility(string layerId, int visibleKinds)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteLayerChange("Change Relationship Visibility", layer, (_, layout) =>
                layout.visibleRelationshipKinds = visibleKinds & ArchitectorRelationshipVisibility.All);
        }

        public ArchitectorEdgeRecord FindRelationship(string edgeId)
        {
            foreach (var layer in Project.layers)
            {
                var edge = ReadLayer(layer.id).semantic.edges.FirstOrDefault(candidate => candidate.id == edgeId);
                if (edge != null)
                    return ArchitectorJson.Clone(edge);
            }
            return null;
        }

        public IReadOnlyList<ArchitectorEdgeRecord> GetRelationshipsForLayer(string layerId)
        {
            if (FindLayer(layerId) == null)
                throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            return Project.layers.SelectMany(layer => ReadLayer(layer.id).semantic.edges)
                .Where(edge => edge.sourceLayerId == layerId || edge.targetLayerId == layerId)
                .OrderBy(edge => edge.id, StringComparer.Ordinal)
                .Select(ArchitectorJson.Clone)
                .ToArray();
        }

        public IReadOnlyList<ArchitectorEdgeRecord> GetPortalRelationships(
            string layerId,
            string portalNodeId,
            bool visibleOnly = true)
        {
            return GetPortalRelationshipRows(layerId, portalNodeId, visibleOnly)
                .Select(row => row.Relationship)
                .ToArray();
        }

        public IReadOnlyDictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>> GetLayerPortalRelationshipRows(
            string layerId,
            bool visibleOnly = true)
        {
            EnsurePortalRelationshipIndex();
            var index = visibleOnly
                ? visiblePortalRelationshipRowsByLayerId
                : allPortalRelationshipRowsByLayerId;
            if (!index.TryGetValue(layerId, out var rowsByPortalNodeId))
                throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            return rowsByPortalNodeId;
        }

        public IReadOnlyList<ArchitectorPortalRelationshipRow> GetPortalRelationshipRows(
            string layerId,
            string portalNodeId,
            bool visibleOnly = true)
        {
            var rowsByPortalNodeId = GetLayerPortalRelationshipRows(layerId, visibleOnly);
            if (!rowsByPortalNodeId.TryGetValue(portalNodeId, out var rows))
                throw new ArchitectorDataException($"Portal '{portalNodeId}' does not exist.");
            return rows;
        }

        public string RelationshipCaption(ArchitectorEdgeRecord edge)
        {
            if (edge == null)
                throw new ArgumentNullException(nameof(edge));
            EnsurePortalRelationshipIndex();
            if (relationshipCaptionsById.TryGetValue(edge.id, out var caption))
                return caption;
            throw new ArchitectorDataException($"Relationship '{edge.id}' does not exist.");
        }

        private void EnsurePortalRelationshipIndex()
        {
            if (portalRelationshipIndexRevision == Revision)
                return;

            var documentsByLayerId = Project.layers.ToDictionary(
                layer => layer.id,
                layer => ReadLayer(layer.id),
                StringComparer.Ordinal);
            var mutableRowsByLayerId = new Dictionary<string, Dictionary<string, List<ArchitectorPortalRelationshipRow>>>(
                StringComparer.Ordinal);
            var captionsById = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var layer in Project.layers)
            {
                mutableRowsByLayerId.Add(
                    layer.id,
                    documentsByLayerId[layer.id].semantic.nodes
                        .Where(IsPortal)
                        .ToDictionary(
                            portal => portal.id,
                            _ => new List<ArchitectorPortalRelationshipRow>(),
                            StringComparer.Ordinal));
            }

            foreach (var edge in documentsByLayerId.Values.SelectMany(documents => documents.semantic.edges))
            {
                var source = FindRelationshipEndpoint(documentsByLayerId, edge.sourceLayerId, edge.sourceNodeId, edge.id, "source");
                var target = FindRelationshipEndpoint(documentsByLayerId, edge.targetLayerId, edge.targetNodeId, edge.id, "target");
                var caption = $"{source.title} → {target.title}";
                if (!captionsById.TryAdd(edge.id, caption))
                    throw new ArchitectorDataException($"Relationship id '{edge.id}' is duplicated across layers.");
                if (string.Equals(edge.sourceLayerId, edge.targetLayerId, StringComparison.Ordinal))
                    continue;

                var row = new ArchitectorPortalRelationshipRow(edge, caption);
                AddPortalRelationshipRow(
                    documentsByLayerId,
                    mutableRowsByLayerId,
                    edge.sourceLayerId,
                    edge.targetLayerId,
                    row);
                AddPortalRelationshipRow(
                    documentsByLayerId,
                    mutableRowsByLayerId,
                    edge.targetLayerId,
                    edge.sourceLayerId,
                    row);
            }

            var allRowsByLayerId = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>>(
                StringComparer.Ordinal);
            var visibleRowsByLayerId = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>>(
                StringComparer.Ordinal);
            foreach (var layer in Project.layers)
            {
                var visibleKinds = documentsByLayerId[layer.id].layout.visibleRelationshipKinds;
                var allRowsByPortalId = new Dictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>(StringComparer.Ordinal);
                var visibleRowsByPortalId = new Dictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>(StringComparer.Ordinal);
                foreach (var portalRows in mutableRowsByLayerId[layer.id])
                {
                    var ordered = portalRows.Value
                        .OrderBy(row => row.Relationship.id, StringComparer.Ordinal)
                        .ToArray();
                    allRowsByPortalId.Add(portalRows.Key, ordered);
                    visibleRowsByPortalId.Add(
                        portalRows.Key,
                        ordered.Where(row =>
                                (visibleKinds & ArchitectorRelationshipVisibility.Flag(row.Relationship.kind)) != 0)
                            .ToArray());
                }
                allRowsByLayerId.Add(layer.id, allRowsByPortalId);
                visibleRowsByLayerId.Add(layer.id, visibleRowsByPortalId);
            }

            allPortalRelationshipRowsByLayerId = allRowsByLayerId;
            visiblePortalRelationshipRowsByLayerId = visibleRowsByLayerId;
            relationshipCaptionsById = captionsById;
            portalRelationshipIndexRevision = Revision;
        }

        private static ArchitectorNodeRecord FindRelationshipEndpoint(
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> documentsByLayerId,
            string layerId,
            string nodeId,
            string relationshipId,
            string endpointName)
        {
            if (!documentsByLayerId.TryGetValue(layerId, out var documents))
                throw new ArchitectorDataException(
                    $"Relationship '{relationshipId}' references missing {endpointName} layer '{layerId}'.");
            return documents.semantic.nodes.FirstOrDefault(node => node.id == nodeId)
                   ?? throw new ArchitectorDataException(
                       $"Relationship '{relationshipId}' references missing {endpointName} node '{nodeId}' in layer '{layerId}'.");
        }

        private static void AddPortalRelationshipRow(
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> documentsByLayerId,
            IReadOnlyDictionary<string, Dictionary<string, List<ArchitectorPortalRelationshipRow>>> rowsByLayerId,
            string localLayerId,
            string remoteLayerId,
            ArchitectorPortalRelationshipRow row)
        {
            var portals = documentsByLayerId[localLayerId].semantic.nodes
                .Where(node => IsPortalTo(node, remoteLayerId))
                .ToArray();
            if (portals.Length != 1)
                throw new ArchitectorDataException(
                    $"Cross-layer relationship '{row.Relationship.id}' requires exactly one Portal from layer '{localLayerId}' to '{remoteLayerId}', but found {portals.Length}.");
            rowsByLayerId[localLayerId][portals[0].id].Add(row);
        }

        private ArchitectorLayerRecord FindRelationshipOwner(string edgeId)
        {
            return Project.layers.FirstOrDefault(layer =>
                       ReadLayer(layer.id).semantic.edges.Any(edge => edge.id == edgeId))
                   ?? throw new ArchitectorDataException($"Relationship '{edgeId}' does not exist.");
        }

        public void ClearNodeEdges(string layerId, IReadOnlyCollection<string> nodeIds)
        {
            if (nodeIds == null || nodeIds.Count == 0)
                return;
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var ids = new HashSet<string>(nodeIds, StringComparer.Ordinal);
            ExecuteLayerSetChange("Clear Node Relationships", Project.layers, documentsByLayerId =>
            {
                var semantic = documentsByLayerId[layerId].semantic;
                if (semantic.nodes.Count(node => ids.Contains(node.id)) != ids.Count)
                    throw new ArchitectorDataException("One or more selected nodes no longer exist.");
                RemoveRelationshipsForNodes(documentsByLayerId, layerId, ids);
                foreach (var portal in semantic.nodes.Where(node => ids.Contains(node.id) && IsPortal(node)))
                    RemoveCrossRelationshipsBetween(documentsByLayerId, layerId, portal.portal.targetLayerId);
            });
        }

        internal ArchitectorNodeTransferData CaptureNodes(string layerId, IReadOnlyCollection<string> nodeIds)
        {
            if (nodeIds == null || nodeIds.Count == 0)
                throw new ArchitectorDataException("At least one node must be selected for Copy.");
            var ids = new HashSet<string>(nodeIds, StringComparer.Ordinal);
            var documents = LoadLayer(layerId);
            if (documents.semantic.nodes.Count(node => ids.Contains(node.id)) != ids.Count)
                throw new ArchitectorDataException("One or more selected nodes no longer exist.");
            if (documents.semantic.nodes.Any(node => ids.Contains(node.id) && IsPortal(node)))
                throw new ArchitectorDataException("Layer Portals cannot be copied or duplicated independently from their pair.");

            var transfer = new ArchitectorNodeTransferData();
            transfer.nodes.AddRange(documents.semantic.nodes.Where(node => ids.Contains(node.id)).Select(ArchitectorJson.Clone));
            transfer.layouts.AddRange(documents.layout.nodes.Where(node => ids.Contains(node.nodeId)).Select(ArchitectorJson.Clone));
            transfer.edges.AddRange(documents.semantic.edges
                .Where(edge => edge.sourceLayerId == layerId
                               && edge.targetLayerId == layerId
                               && ids.Contains(edge.sourceNodeId)
                               && ids.Contains(edge.targetNodeId))
                .Select(ArchitectorJson.Clone));
            var edgeIds = new HashSet<string>(transfer.edges.Select(edge => edge.id), StringComparer.Ordinal);
            transfer.edgeLayouts.AddRange(documents.layout.edges.Where(edge => edgeIds.Contains(edge.edgeId)).Select(ArchitectorJson.Clone));
            transfer.minX = transfer.layouts.Min(node => node.x);
            transfer.minY = transfer.layouts.Min(node => node.y);
            transfer.maxX = transfer.layouts.Max(node => node.x + node.width);
            transfer.maxY = transfer.layouts.Max(node => node.y + node.height);
            return transfer;
        }

        public IReadOnlyList<string> PasteNodes(string layerId, ArchitectorNodeClipboard clipboard, float centerX, float centerY)
        {
            if (clipboard == null)
                throw new ArgumentNullException(nameof(clipboard));
            return InsertTransferredNodes("Paste Nodes", layerId, clipboard.RequireData(), centerX, centerY);
        }

        public IReadOnlyList<string> DuplicateNodes(string layerId, IReadOnlyCollection<string> nodeIds)
        {
            var transfer = CaptureNodes(layerId, nodeIds);
            var grid = Settings.geometry.snapToGrid ? Settings.geometry.gridSize : 24f;
            var centerX = (transfer.minX + transfer.maxX) * 0.5f + grid;
            var centerY = (transfer.minY + transfer.maxY) * 0.5f + grid;
            return InsertTransferredNodes("Duplicate Nodes", layerId, transfer, centerX, centerY);
        }

        private IReadOnlyList<string> InsertTransferredNodes(
            string actionName,
            string layerId,
            ArchitectorNodeTransferData transfer,
            float centerX,
            float centerY)
        {
            var layer = FindLayer(layerId) ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            var insertedIds = new List<string>();
            ExecuteLayerChange(actionName, layer, (semantic, layout) =>
            {
                var nodeIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
                var edgeIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
                var requestedPositions = new Dictionary<string, ArchitectorPoint>(StringComparer.Ordinal);
                var sourceCenterX = (transfer.minX + transfer.maxX) * 0.5f;
                var sourceCenterY = (transfer.minY + transfer.maxY) * 0.5f;

                foreach (var source in transfer.nodes)
                {
                    var copy = ArchitectorJson.Clone(source);
                    var newId = NewId("node");
                    nodeIdMap[source.id] = newId;
                    insertedIds.Add(newId);
                    copy.id = newId;
                    copy.title = MakeUniqueNodeTitle(semantic, source.title);
                    copy.members = (copy.members ?? new List<ArchitectorMemberRecord>()).Select(member =>
                    {
                        member.id = NewId("member");
                        return member;
                    }).ToList();

                    if (string.Equals(copy.type?.mode, "declared", StringComparison.Ordinal))
                        copy.type.name = MakeUniqueManualTypeName(semantic, copy.type.typeNamespace, copy.type.name);
                    else if (string.Equals(copy.type?.mode, "linked", StringComparison.Ordinal))
                        EnsureLinkedTypeIsNotRepresented(semantic, copy.type);
                    semantic.nodes.Add(copy);

                    var sourceLayout = transfer.layouts.First(node => node.nodeId == source.id);
                    var layoutCopy = ArchitectorJson.Clone(sourceLayout);
                    layoutCopy.nodeId = newId;
                    layoutCopy.x += centerX - sourceCenterX;
                    layoutCopy.y += centerY - sourceCenterY;
                    layout.nodes.Add(layoutCopy);
                    requestedPositions[newId] = new ArchitectorPoint(layoutCopy.x, layoutCopy.y);
                }

                foreach (var sourceEdge in transfer.edges)
                {
                    var edgeCopy = ArchitectorJson.Clone(sourceEdge);
                    var newEdgeId = NewId("edge");
                    edgeIdMap[sourceEdge.id] = newEdgeId;
                    edgeCopy.id = newEdgeId;
                    edgeCopy.sourceLayerId = layerId;
                    edgeCopy.sourceNodeId = nodeIdMap[sourceEdge.sourceNodeId];
                    edgeCopy.targetLayerId = layerId;
                    edgeCopy.targetNodeId = nodeIdMap[sourceEdge.targetNodeId];
                    semantic.edges.Add(edgeCopy);
                }

                foreach (var sourceEdgeLayout in transfer.edgeLayouts)
                {
                    if (!edgeIdMap.TryGetValue(sourceEdgeLayout.edgeId, out var newEdgeId))
                        continue;
                    var edgeLayoutCopy = ArchitectorJson.Clone(sourceEdgeLayout);
                    edgeLayoutCopy.edgeId = newEdgeId;
                    layout.edges.Add(edgeLayoutCopy);
                }

                var positions = FindAvailableGroupPositions(layout, requestedPositions);
                foreach (var nodeLayout in layout.nodes)
                {
                    if (!positions.TryGetValue(nodeLayout.nodeId, out var position))
                        continue;
                    nodeLayout.x = position.x;
                    nodeLayout.y = position.y;
                }
            });
            return insertedIds;
        }

        public string CreateGroup(string parentGroupId, string preferredName, int? insertionIndex = null)
        {
            var id = NewId("group");
            ExecuteProjectChange("Create Group", project =>
            {
                var group = new ArchitectorGroupRecord
                {
                    id = id,
                    name = MakeUniqueName(project, parentGroupId, preferredName),
                    parentGroupId = parentGroupId ?? string.Empty
                };
                project.groups.Add(group);
                Insert(GetGroupIds(project, parentGroupId), id, insertionIndex);
            });
            return id;
        }

        public string CreateLayer(string parentGroupId, string preferredName, int? insertionIndex = null)
        {
            var id = NewId("layer");
            ExecuteRepositoryChange("Create Layer", (project, layers) =>
            {
                project.layers.Add(new ArchitectorLayerRecord
                {
                    id = id,
                    name = MakeUniqueName(project, parentGroupId, preferredName),
                    parentGroupId = parentGroupId ?? string.Empty,
                    semanticPath = ArchitectorPaths.LayerPath(id),
                    layoutPath = ArchitectorPaths.LayoutPath(id)
                });
                Insert(GetLayerIds(project, parentGroupId), id, insertionIndex);
                layers.Add(id, new ArchitectorLayerDocuments(
                    new ArchitectorLayerDocument { layerId = id },
                    new ArchitectorLayerLayoutDocument { layerId = id }));
            });
            return id;
        }

        public void UpdateLayerPurpose(string layerId, string purpose)
        {
            if (FindLayer(layerId) == null)
                throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteProjectChange("Change Layer Purpose", project =>
            {
                project.layers.First(layer => layer.id == layerId).purpose = purpose?.Trim() ?? string.Empty;
            });
        }

        public void UpdateLayerScope(string layerId, IEnumerable<string> folders, IEnumerable<string> namespaces)
        {
            if (FindLayer(layerId) == null)
                throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            ExecuteProjectChange("Change Layer Code Scope", project =>
            {
                project.layers.First(layer => layer.id == layerId).scope = new ArchitectorLayerScopeRecord
                {
                    folders = NormalizeScopeEntries(folders, true),
                    namespaces = NormalizeScopeEntries(namespaces, false)
                };
            });
        }

        private static List<string> NormalizeScopeEntries(IEnumerable<string> values, bool paths)
        {
            return (values ?? Array.Empty<string>())
                .Select(value => (value ?? string.Empty).Trim())
                .Select(value => paths ? value.Replace('\\', '/').TrimEnd('/') : value)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
        }

        public void MoveEntries(
            IReadOnlyList<string> entryIds,
            string targetParentGroupId,
            int? groupInsertionIndex = null,
            int? layerInsertionIndex = null)
        {
            if (entryIds == null || entryIds.Count == 0)
                return;
            var orderedIds = entryIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (orderedIds.Length != entryIds.Count)
                throw new ArchitectorDataException("Explorer selection contains an invalid or duplicate entry.");

            ExecuteProjectChange(orderedIds.Length == 1 ? "Move Entry" : "Move Entries", project =>
            {
                MoveEntriesInProject(
                    project,
                    orderedIds,
                    targetParentGroupId,
                    groupInsertionIndex,
                    layerInsertionIndex);
            });
        }

        private static void MoveEntriesInProject(
            ArchitectorProjectDocument project,
            IReadOnlyList<string> orderedIds,
            string targetParentGroupId,
            int? groupInsertionIndex = null,
            int? layerInsertionIndex = null)
        {
            var targetParentId = targetParentGroupId ?? string.Empty;
            if (!string.IsNullOrEmpty(targetParentId) && project.groups.All(group => group.id != targetParentId))
                throw new ArchitectorDataException("The destination group no longer exists.");

            var movedGroups = orderedIds
                .Select(id => project.groups.FirstOrDefault(group => group.id == id))
                .Where(group => group != null)
                .ToArray();
            var movedLayers = orderedIds
                .Select(id => project.layers.FirstOrDefault(layer => layer.id == id))
                .Where(layer => layer != null)
                .ToArray();
            if (movedGroups.Length + movedLayers.Length != orderedIds.Count)
                throw new ArchitectorDataException("One or more Explorer entries no longer exist.");

            var sourceParents = movedGroups.Select(group => group.parentGroupId)
                .Concat(movedLayers.Select(layer => layer.parentGroupId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (sourceParents.Length != 1)
                throw new ArchitectorDataException("Only entries from the same Explorer folder can be moved together.");

            foreach (var movedGroup in movedGroups)
            {
                if (IsSameOrDescendantGroup(project, targetParentId, movedGroup.id))
                    throw new ArchitectorDataException("A group cannot be moved into itself or one of its descendants.");
            }

            var targetGroupIds = GetGroupIds(project, targetParentId);
            var targetLayerIds = GetLayerIds(project, targetParentId);
            var movedGroupIds = new HashSet<string>(movedGroups.Select(group => group.id), StringComparer.Ordinal);
            var movedLayerIds = new HashSet<string>(movedLayers.Select(layer => layer.id), StringComparer.Ordinal);
            var adjustedGroupIndex = AdjustInsertionIndex(targetGroupIds, movedGroupIds, groupInsertionIndex);
            var adjustedLayerIndex = AdjustInsertionIndex(targetLayerIds, movedLayerIds, layerInsertionIndex);

            foreach (var movedGroup in movedGroups)
                GetGroupIds(project, movedGroup.parentGroupId).Remove(movedGroup.id);
            foreach (var movedLayer in movedLayers)
                GetLayerIds(project, movedLayer.parentGroupId).Remove(movedLayer.id);

            foreach (var movedGroup in movedGroups)
                movedGroup.parentGroupId = targetParentId;
            foreach (var movedLayer in movedLayers)
                movedLayer.parentGroupId = targetParentId;

            InsertRange(targetGroupIds, orderedIds.Where(movedGroupIds.Contains), adjustedGroupIndex);
            InsertRange(targetLayerIds, orderedIds.Where(movedLayerIds.Contains), adjustedLayerIndex);
        }

        public string WrapLayerInNewGroup(string layerId, string preferredGroupName)
        {
            var groupId = NewId("group");
            ExecuteProjectChange("Wrap Layer in Group", project =>
            {
                var layer = RequireLayer(project, layerId);
                var previousParentId = layer.parentGroupId;
                GetLayerIds(project, previousParentId).Remove(layer.id);

                var group = new ArchitectorGroupRecord
                {
                    id = groupId,
                    name = MakeUniqueName(project, previousParentId, preferredGroupName, layer.id),
                    parentGroupId = previousParentId,
                    layerIds = new List<string> { layer.id }
                };
                project.groups.Add(group);
                GetGroupIds(project, previousParentId).Add(group.id);
                layer.parentGroupId = group.id;
            });
            return groupId;
        }

        public void RenameEntry(string id, string newName)
        {
            ExecuteProjectChange("Rename Entry", project =>
            {
                var trimmed = (newName ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(trimmed))
                    throw new ArchitectorDataException("Name cannot be empty.");
                var parentGroupId = project.groups.FirstOrDefault(candidate => candidate.id == id)?.parentGroupId
                                    ?? RequireLayer(project, id).parentGroupId;
                if (SiblingNames(project, parentGroupId, id).Contains(trimmed))
                    throw new ArchitectorDataException($"The name '{trimmed}' is already used in this group.");

                var group = project.groups.FirstOrDefault(candidate => candidate.id == id);
                if (group != null)
                {
                    group.name = trimmed;
                    return;
                }

                RequireLayer(project, id).name = trimmed;
            });
        }

        public ArchitectorDeleteSummary GetDeleteSummary(string id)
        {
            var group = FindGroup(id);
            if (group == null)
                return FindLayer(id) == null ? default : new ArchitectorDeleteSummary(0, 1);

            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            var layerIds = new HashSet<string>(StringComparer.Ordinal);
            CollectDescendants(Project, group.id, groupIds, layerIds);
            return new ArchitectorDeleteSummary(groupIds.Count, layerIds.Count);
        }

        public void DeleteEntry(string id)
        {
            DeleteEntries(new[] { id });
        }

        public void DeleteEntries(IReadOnlyCollection<string> entryIds)
        {
            if (entryIds == null || entryIds.Count == 0)
                return;
            var requestedIds = new HashSet<string>(entryIds, StringComparer.Ordinal);
            if (requestedIds.Count != entryIds.Count || requestedIds.Any(string.IsNullOrEmpty))
                throw new ArchitectorDataException("Explorer deletion contains an invalid or duplicate entry.");

            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            var layerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in requestedIds)
            {
                var group = FindGroup(id);
                if (group != null)
                {
                    CollectDescendants(Project, group.id, groupIds, layerIds);
                    continue;
                }
                var layer = FindLayer(id);
                if (layer == null)
                    throw new ArchitectorDataException($"Entry '{id}' does not exist.");
                layerIds.Add(layer.id);
            }

            ExecuteRepositoryChange(requestedIds.Count == 1 ? "Delete Entry" : "Delete Entries", (project, layers) =>
            {
                foreach (var survivingLayerId in layers.Keys.Where(candidate => !layerIds.Contains(candidate)).ToArray())
                {
                    var documents = layers[survivingLayerId];
                    var portalIds = documents.semantic.nodes
                        .Where(node => IsPortal(node) && layerIds.Contains(node.portal.targetLayerId))
                        .Select(node => node.id)
                        .ToHashSet(StringComparer.Ordinal);
                    if (portalIds.Count == 0)
                        continue;
                    RemoveNodes(documents, portalIds);
                    documents.semantic.edges.RemoveAll(edge => layerIds.Contains(edge.targetLayerId));
                    var validEdgeIds = documents.semantic.edges.Select(edge => edge.id)
                        .ToHashSet(StringComparer.Ordinal);
                    documents.layout.edges.RemoveAll(edge => !validEdgeIds.Contains(edge.edgeId));
                }

                foreach (var groupId in groupIds)
                {
                    var candidate = project.groups.First(item => item.id == groupId);
                    GetGroupIds(project, candidate.parentGroupId).Remove(candidate.id);
                }
                foreach (var layerId in layerIds)
                {
                    var candidate = project.layers.First(item => item.id == layerId);
                    GetLayerIds(project, candidate.parentGroupId).Remove(candidate.id);
                    layers.Remove(layerId);
                }

                project.groups.RemoveAll(candidate => groupIds.Contains(candidate.id));
                project.layers.RemoveAll(candidate => layerIds.Contains(candidate.id));
            });
        }

        public void Undo()
        {
            if (!CanUndo)
                return;
            EnsureDiskMatchesSession();
            var record = undo.Pop();
            ApplySnapshots(record.before);
            redo.Push(record);
            ReloadSessionFromKnownFiles();
        }

        public void Redo()
        {
            if (!CanRedo)
                return;
            EnsureDiskMatchesSession();
            var record = redo.Pop();
            ApplySnapshots(record.after);
            undo.Push(record);
            ReloadSessionFromKnownFiles();
        }

        public void UpdateSettings(string name, Action<ArchitectorSettingsDocument> mutate)
        {
            var nextSettings = ArchitectorJson.Clone(Settings);
            mutate(nextSettings);
            ArchitectorValidation.Validate(nextSettings);
            Commit(name, null, null, nextSettings, false, notifyWhenUnchanged: true);
        }

        private void ExecuteProjectChange(string name, Action<ArchitectorProjectDocument> mutate)
        {
            ExecuteRepositoryChange(name, (project, _) => mutate(project));
        }

        private void ExecuteRepositoryChange(
            string name,
            Action<ArchitectorProjectDocument, IDictionary<string, ArchitectorLayerDocuments>> mutate)
        {
            var nextProject = ArchitectorJson.Clone(Project);
            var nextLayers = CloneAllLayers();
            mutate(nextProject, nextLayers);
            Commit(name, nextProject, nextLayers, null, false, notifyWhenUnchanged: true);
        }

        private void ExecuteLayerChange(string name, ArchitectorLayerRecord layer, Action<ArchitectorLayerDocument, ArchitectorLayerLayoutDocument> mutate)
        {
            ReadLayer(layer.id);
            ExecuteRepositoryChange(name, (_, layers) =>
            {
                var documents = layers[layer.id];
                mutate(documents.semantic, documents.layout);
            });
        }

        private void ExecuteLayerSetChange(
            string name,
            IReadOnlyCollection<ArchitectorLayerRecord> layers,
            Action<IDictionary<string, ArchitectorLayerDocuments>> mutate)
        {
            foreach (var layer in layers)
                ReadLayer(layer.id);
            ExecuteRepositoryChange(name, (_, documents) => mutate(documents));
        }

        private ArchitectorCanonicalBatchResult ApplyCanonicalBatchCore(ArchitectorCanonicalBatch batch, bool dryRun)
        {
            if (batch == null)
                throw new ArgumentNullException(nameof(batch));
            if (batch.Documents.Count == 0)
                throw new ArchitectorDataException("Canonical batch contains no documents.");
            if (RefreshExternalChanges())
                throw new ArchitectorDataException(ReloadedBeforeWriteMessage);

            var recovering = layerErrors.Count > 0 || ExternalChangeError.Length > 0;
            var baseDisk = recovering ? ReadDiskFiles(out _) : diskFiles;
            var files = new Dictionary<string, string>(baseDisk, StringComparer.Ordinal);
            var requestedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var settingsChanged = false;
            foreach (var pair in batch.Documents)
            {
                var path = NormalizeCanonicalPath(pair.Key);
                if (!requestedPaths.Add(path))
                    throw new ArchitectorDataException($"Canonical batch contains duplicate path '{path}'.");
                if (!ArchitectorStorageCodec.IsCanonicalPath(path))
                    throw new ArchitectorDataException($"Canonical batch path '{path}' is not an Architector document path.");
                if (string.Equals(path, ArchitectorPaths.SettingsFileName, StringComparison.Ordinal))
                {
                    if (pair.Value == null)
                        throw new ArchitectorDataException($"Candidate project still requires deleted document '{path}'.");
                    settingsChanged = true;
                }
                foreach (var alias in ArchitectorStorageCodec.EntryAliases(files.Keys, path).ToArray())
                    files.Remove(alias);
                if (pair.Value == null)
                    files.Remove(path);
                else
                    files[path] = pair.Value;
            }

            var decoded = DecodeComplete(files);
            ValidateCandidateApprovalMetadata(decoded.Project);
            return Commit(
                batch.ActionName,
                decoded.Project,
                decoded.Layers,
                settingsChanged ? ReadSettings(files) : null,
                dryRun,
                recovering ? baseDisk : null,
                notifyWhenUnchanged: false);
        }

        private ArchitectorCanonicalBatchResult Commit(
            string name,
            ArchitectorProjectDocument nextProject,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> nextLayers,
            ArchitectorSettingsDocument nextSettings,
            bool dryRun,
            IReadOnlyDictionary<string, string> recoveryDiskFiles = null,
            bool notifyWhenUnchanged = false)
        {
            if (recoveryDiskFiles == null)
                EnsureDiskMatchesSession();
            var baseDisk = recoveryDiskFiles ?? diskFiles;
            var changes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var validatedFiles = 0;
            var layerCount = Project.layers.Count;
            var portalPairs = 0;
            IReadOnlyList<ArchitectorApprovalImpact> approvalImpacts = Array.Empty<ArchitectorApprovalImpact>();

            if (nextProject != null)
            {
                RequireCompleteLayers(nextProject, nextLayers);
                RemoveLonelyPortals(nextLayers, dryRun);
                portalPairs = ArchitectorValidation.ValidateRepository(nextProject, nextSettings ?? Settings, nextLayers);
                var nextFiles = ArchitectorStorageCodec.Encode(nextProject, nextLayers);
                IReadOnlyDictionary<string, string> previousFiles = recoveryDiskFiles == null && layerErrors.Count == 0
                    ? ArchitectorStorageCodec.Encode(Project, layerDocuments)
                    : baseDisk
                        .Where(pair => !string.Equals(pair.Key, ArchitectorPaths.SettingsFileName, StringComparison.Ordinal))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                foreach (var pair in nextFiles)
                {
                    if (!previousFiles.TryGetValue(pair.Key, out var previous)
                        || !string.Equals(previous, pair.Value, StringComparison.Ordinal))
                        changes[pair.Key] = pair.Value;
                }
                foreach (var path in previousFiles.Keys.Where(path => !nextFiles.ContainsKey(path)))
                    changes[path] = null;
                foreach (var path in baseDisk.Keys.Where(path => IsRenamedEntry(path) && !nextFiles.ContainsKey(path)))
                    changes[path] = null;
                foreach (var pair in nextFiles.Where(pair => IsRenamedEntry(pair.Key) && !baseDisk.ContainsKey(pair.Key)))
                    changes[pair.Key] = pair.Value;
                validatedFiles = nextFiles.Count;
                layerCount = nextLayers.Count;
                approvalImpacts = BuildApprovalImpacts(nextProject, nextLayers);
                EnforceGroupLocks(nextProject, nextLayers, previousFiles, nextFiles);
            }

            if (nextSettings != null)
            {
                var settingsJson = ArchitectorJson.Serialize(nextSettings);
                validatedFiles++;
                if (!baseDisk.TryGetValue(ArchitectorPaths.SettingsFileName, out var currentSettings)
                    || !string.Equals(currentSettings, settingsJson, StringComparison.Ordinal))
                    changes[ArchitectorPaths.SettingsFileName] = settingsJson;
            }

            var result = new ArchitectorCanonicalBatchResult(
                changes.Count,
                validatedFiles,
                layerCount,
                portalPairs,
                approvalImpacts);
            if (dryRun)
                return result;
            if (changes.Count == 0)
            {
                if (notifyWhenUnchanged)
                {
                    Revision++;
                    Changed?.Invoke();
                }
                return result;
            }

            WriteCommit(name, changes, baseDisk, nextSettings);
            return result;
        }

        private static bool IsRenamedEntry(string path) =>
            ArchitectorStorageCodec.IsCanonicalPath(path)
            && !string.Equals(path, ArchitectorPaths.ProjectFileName, StringComparison.Ordinal)
            && !string.Equals(path, ArchitectorPaths.SettingsFileName, StringComparison.Ordinal);

        private void WriteCommit(
            string name,
            IReadOnlyDictionary<string, string> changes,
            IReadOnlyDictionary<string, string> baseDisk,
            ArchitectorSettingsDocument nextSettings)
        {
            var before = changes.Keys
                .Select(path => new ArchitectorFileSnapshot(path, baseDisk.TryGetValue(path, out var content) ? content : null))
                .ToList();
            var after = changes
                .Select(pair => new ArchitectorFileSnapshot(pair.Key, pair.Value))
                .ToList();
            var previousDiskFiles = diskFiles;
            var previousDiskStamps = diskStamps;
            diskFiles = new Dictionary<string, string>(baseDisk, StringComparer.Ordinal);
            diskStamps = new Dictionary<string, ArchitectorFileStamp>(diskStamps, StringComparer.Ordinal);

            try
            {
                ApplySnapshots(after);
                var decoded = DecodeComplete(diskFiles);
                Install(decoded, nextSettings ?? Settings, diskFiles, replacementStamps: null);
                Revision++;
                undo.Push(new ArchitectorUndoRecord(name, before, after));
                redo.Clear();
                Changed?.Invoke();
            }
            catch
            {
                ApplySnapshots(before);
                diskFiles = previousDiskFiles;
                diskStamps = previousDiskStamps;
                throw;
            }
        }

        private void ApplySnapshots(IEnumerable<ArchitectorFileSnapshot> snapshots)
        {
            var ordered = snapshots.OrderBy(snapshot => snapshot.relativePath, StringComparer.Ordinal).ToArray();
            foreach (var snapshot in ordered.Where(snapshot => snapshot.content != null))
            {
                var path = Paths.ToAbsoluteDataPath(snapshot.relativePath);
                ArchitectorJson.WriteTextAtomic(path, snapshot.content);
                diskFiles[snapshot.relativePath] = snapshot.content;
                diskStamps[snapshot.relativePath] = ArchitectorFileStamp.Read(path);
            }
            foreach (var snapshot in ordered.Where(snapshot => snapshot.content == null))
            {
                var path = Paths.ToAbsoluteDataPath(snapshot.relativePath);
                if (File.Exists(path))
                    File.Delete(path);
                diskFiles.Remove(snapshot.relativePath);
                diskStamps.Remove(snapshot.relativePath);
            }
        }

        private void ReloadSessionFromKnownFiles()
        {
            Install(DecodeComplete(diskFiles), ReadSettings(diskFiles), diskFiles, replacementStamps: null);
            Revision++;
            Changed?.Invoke();
        }

        private const string ReloadedBeforeWriteMessage =
            "Architector files changed on disk and were reloaded. The operation was not applied; repeat it on the current architecture.";

        private void EnsureDiskMatchesSession()
        {
            if (RefreshExternalChanges())
                throw new ArchitectorDataException(ReloadedBeforeWriteMessage);
            if (ExternalChangeError.Length > 0)
                throw new ArchitectorDataException($"{ExternalChangeError} Repair the files before editing.");
        }

        private void Install(
            ArchitectorDecodedRepository decoded,
            ArchitectorSettingsDocument settings,
            IReadOnlyDictionary<string, string> files,
            IReadOnlyDictionary<string, ArchitectorFileStamp> replacementStamps)
        {
            Project = decoded.Project;
            layerDocuments = decoded.Layers;
            layerErrors = decoded.LayerErrors;
            Settings = settings;
            diskFiles = new Dictionary<string, string>(files, StringComparer.Ordinal);
            if (replacementStamps != null)
                diskStamps = new Dictionary<string, ArchitectorFileStamp>(replacementStamps, StringComparer.Ordinal);
            rejectedDiskStamps = null;
            ExternalChangeError = string.Empty;
        }

        public bool HasLayerLoadError(string layerId) => layerErrors.ContainsKey(layerId);

        private ArchitectorLayerDocuments ReadLayer(string layerId)
        {
            if (FindLayer(layerId) == null)
                throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
            if (layerErrors.TryGetValue(layerId, out var error))
                throw new ArchitectorDataException(error);
            return layerDocuments[layerId];
        }

        private Dictionary<string, ArchitectorLayerDocuments> CloneAllLayers()
        {
            return layerDocuments.ToDictionary(
                pair => pair.Key,
                pair => CloneLayer(pair.Value),
                StringComparer.Ordinal);
        }

        private void RequireCompleteLayers(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers)
        {
            foreach (var layer in project.layers)
            {
                if (layers.ContainsKey(layer.id))
                    continue;
                throw new ArchitectorDataException(layerErrors.TryGetValue(layer.id, out var error)
                    ? error
                    : $"Layer '{layer.name}' has no documents.");
            }
        }

        private static ArchitectorDecodedRepository DecodeComplete(IReadOnlyDictionary<string, string> files)
        {
            var decoded = ArchitectorStorageCodec.Decode(files);
            if (decoded.LayerErrors.Count > 0)
                throw new ArchitectorDataException(decoded.LayerErrors
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .First()
                    .Value);
            return decoded;
        }

        private static ArchitectorSettingsDocument ReadSettings(IReadOnlyDictionary<string, string> files)
        {
            if (!files.TryGetValue(ArchitectorPaths.SettingsFileName, out var json) || json == null)
                throw new ArchitectorDataException($"Settings document '{ArchitectorPaths.SettingsFileName}' is missing.");
            var settings = ArchitectorJson.Deserialize<ArchitectorSettingsDocument>(json, ArchitectorPaths.SettingsFileName);
            ArchitectorValidation.Validate(settings);
            return settings;
        }

        private void ValidateCandidateApprovalMetadata(ArchitectorProjectDocument candidate)
        {
            foreach (var layer in candidate.layers)
            {
                var current = FindLayer(layer.id);
                if (current == null)
                {
                    if (!string.IsNullOrEmpty(layer.approval.semanticHash))
                        throw new ArchitectorDataException("New layer approval metadata is repository-owned and must be empty.");
                    continue;
                }
                if (!string.Equals(ArchitectorJson.Serialize(layer.approval), ArchitectorJson.Serialize(current.approval), StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Layer '{layer.name}' approval metadata is repository-owned.");
            }
        }

        private IReadOnlyList<ArchitectorApprovalImpact> BuildApprovalImpacts(
            ArchitectorProjectDocument candidate,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> candidateLayers)
        {
            var impacts = new List<ArchitectorApprovalImpact>();
            var currentRepositoryIsReadable = layerErrors.Count == 0;
            foreach (var current in Project.layers)
            {
                var next = candidate.layers.FirstOrDefault(layer => layer.id == current.id);
                var currentState = ArchitectorApprovalStates.Changed;
                string currentHash = null;
                if (currentRepositoryIsReadable)
                {
                    currentHash = ComputeSemanticHashForLayer(Project, layerDocuments, current.id);
                    currentState = ArchitectorApproval.State(current, currentHash);
                }
                if (next == null)
                {
                    impacts.Add(new ArchitectorApprovalImpact(
                        current.id, current.name, "delete", currentState));
                    continue;
                }
                if (!currentRepositoryIsReadable)
                {
                    impacts.Add(new ArchitectorApprovalImpact(
                        current.id, current.name, "modify", currentState));
                    continue;
                }
                var nextHash = ComputeSemanticHashForLayer(candidate, candidateLayers, next.id);
                if (!string.Equals(currentHash, nextHash, StringComparison.Ordinal))
                    impacts.Add(new ArchitectorApprovalImpact(
                        current.id, current.name, "modify", currentState));
            }
            foreach (var created in candidate.layers.Where(layer => FindLayer(layer.id) == null))
                impacts.Add(new ArchitectorApprovalImpact(
                    created.id, created.name, "create", ArchitectorApprovalStates.Draft));
            return impacts;
        }

        private string NormalizeCanonicalPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new ArchitectorDataException("Canonical document paths must be non-empty and relative.");
            var normalized = relativePath.Replace('\\', '/');
            Paths.ToAbsoluteDataPath(normalized);
            return normalized;
        }

        private Dictionary<string, string> ReadDiskFiles(out Dictionary<string, ArchitectorFileStamp> stamps)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            stamps = new Dictionary<string, ArchitectorFileStamp>(StringComparer.Ordinal);
            foreach (var (relativePath, absolutePath) in EnumerateCanonicalFiles())
            {
                stamps[relativePath] = ArchitectorFileStamp.Read(absolutePath);
                files[relativePath] = File.ReadAllText(absolutePath);
            }
            return files;
        }

        private Dictionary<string, ArchitectorFileStamp> ReadDiskStamps()
        {
            return EnumerateCanonicalFiles().ToDictionary(
                file => file.relativePath,
                file => ArchitectorFileStamp.Read(file.absolutePath),
                StringComparer.Ordinal);
        }

        private IEnumerable<(string relativePath, string absolutePath)> EnumerateCanonicalFiles()
        {
            foreach (var fileName in new[] { ArchitectorPaths.ProjectFileName, ArchitectorPaths.SettingsFileName, ArchitectorPaths.DigestFileName })
            {
                var path = Path.Combine(Paths.DataRoot, fileName);
                if (File.Exists(path))
                    yield return (fileName, path);
            }

            foreach (var (directory, suffix) in new[]
                     {
                         (ArchitectorPaths.GroupsDirectoryName, ArchitectorPaths.GroupFileSuffix),
                         (ArchitectorPaths.LayersDirectoryName, ArchitectorPaths.LayerFileSuffix),
                         (ArchitectorPaths.LayoutsDirectoryName, ArchitectorPaths.LayoutFileSuffix),
                         (ArchitectorPaths.LinksDirectoryName, ArchitectorPaths.LinkFileSuffix),
                         (ArchitectorPaths.BaselinesDirectoryName, ArchitectorPaths.BaselineFileSuffix)
                     })
            {
                var root = Path.Combine(Paths.DataRoot, directory);
                if (!Directory.Exists(root))
                    continue;
                foreach (var path in Directory.EnumerateFiles(root, "*" + suffix, SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    var relativePath = $"{directory}/{Path.GetFileName(path)}";
                    if (ArchitectorStorageCodec.IsCanonicalPath(relativePath))
                        yield return (relativePath, path);
                }
            }
        }

        private static bool StampsEqual(
            IReadOnlyDictionary<string, ArchitectorFileStamp> first,
            IReadOnlyDictionary<string, ArchitectorFileStamp> second)
        {
            return first.Count == second.Count
                   && first.All(pair => second.TryGetValue(pair.Key, out var stamp) && stamp.Equals(pair.Value));
        }

        private static bool FilesEqual(
            IReadOnlyDictionary<string, string> first,
            IReadOnlyDictionary<string, string> second)
        {
            return first.Count == second.Count
                   && first.All(pair => second.TryGetValue(pair.Key, out var content)
                                        && string.Equals(content, pair.Value, StringComparison.Ordinal));
        }

        private static string DescribeExternalChanges(
            IReadOnlyDictionary<string, string> previousFiles,
            IReadOnlyDictionary<string, string> currentFiles,
            ArchitectorProjectDocument project)
        {
            var names = previousFiles.Keys.Union(currentFiles.Keys)
                .Where(path => !previousFiles.TryGetValue(path, out var previous)
                               || !currentFiles.TryGetValue(path, out var current)
                               || !string.Equals(previous, current, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => DescribeDocument(path, project))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var shown = string.Join(", ", names.Take(5));
            return names.Length > 5
                ? $"Reloaded from disk: {shown} and {names.Length - 5} more."
                : $"Reloaded from disk: {shown}.";
        }

        private static string DescribeDocument(string relativePath, ArchitectorProjectDocument project)
        {
            if (string.Equals(relativePath, ArchitectorPaths.ProjectFileName, StringComparison.Ordinal))
                return "project";
            if (string.Equals(relativePath, ArchitectorPaths.SettingsFileName, StringComparison.Ordinal))
                return "settings";
            if (string.Equals(relativePath, ArchitectorPaths.DigestFileName, StringComparison.Ordinal))
                return "architecture digest";
            if (relativePath.StartsWith(ArchitectorPaths.LinksDirectoryName + "/", StringComparison.Ordinal))
                return "Portal links";
            var baseline = relativePath.StartsWith(ArchitectorPaths.BaselinesDirectoryName + "/", StringComparison.Ordinal);
            var fileName = relativePath.Substring(relativePath.IndexOf('/') + 1);
            var id = fileName.Substring(0, fileName.IndexOf('.'));
            var path = ArchitectorEntryPaths.Describe(project, id);
            if (string.IsNullOrEmpty(path))
                return relativePath;
            return baseline ? $"{path} (approval)" : path;
        }

        private void EnsureStorageExists()
        {
            Directory.CreateDirectory(Paths.LayersRoot);
            Directory.CreateDirectory(Paths.LayoutsRoot);
            if (!File.Exists(Paths.ProjectPath))
            {
                ArchitectorJson.WriteAtomic(
                    Paths.ProjectPath,
                    new ArchitectorStoredProjectDocument { projectId = NewId("project") });
            }
            if (!File.Exists(Paths.SettingsPath))
            {
                var settings = new ArchitectorSettingsDocument();
                settings.sourceIndex.includedRoots.Add("Assets/Scripts");
                settings.sourceIndex.excludedRoots.AddRange(new[]
                {
                    "Assets/Plugins",
                    "Assets/Plugins/Architector",
                    "Assets/Resources",
                    "Assets/ThirdParty",
                    "Assets/Vendor"
                });
                ArchitectorJson.WriteAtomic(Paths.SettingsPath, settings);
            }
        }

        private static void CollectDescendants(ArchitectorProjectDocument project, string groupId, ISet<string> groupIds, ISet<string> layerIds)
        {
            if (!groupIds.Add(groupId))
                return;
            var group = project.groups.First(candidate => candidate.id == groupId);
            foreach (var layerId in group.layerIds)
                layerIds.Add(layerId);
            foreach (var childId in group.childGroupIds)
                CollectDescendants(project, childId, groupIds, layerIds);
        }

        private void AddPortalNode(
            ArchitectorLayerDocuments documents,
            string nodeId,
            string pairId,
            string targetLayerId,
            string counterpartNodeId,
            string role,
            string title,
            float requestedX,
            float requestedY,
            ArchitectorSize size)
        {
            var position = FindAvailablePosition(documents.layout, requestedX, requestedY, size, null);
            documents.semantic.nodes.Add(new ArchitectorNodeRecord
            {
                id = nodeId,
                title = title,
                kind = ArchitectorNodeKinds.LayerPortal,
                type = new ArchitectorTypeRecord { mode = "none" },
                portal = new ArchitectorPortalRecord
                {
                    pairId = pairId,
                    targetLayerId = targetLayerId,
                    counterpartNodeId = counterpartNodeId,
                    role = role
                }
            });
            documents.layout.nodes.Add(new ArchitectorNodeLayoutRecord
            {
                nodeId = nodeId,
                x = position.x,
                y = position.y,
                width = size.width,
                height = size.height
            });
        }

        private void DeleteNodesAndPortalCounterparts(
            ArchitectorLayerRecord sourceLayer,
            IReadOnlyCollection<string> selectedIds,
            IReadOnlyCollection<ArchitectorNodeRecord> selectedPortals)
        {
            var layers = Project.layers.ToList();
            foreach (var portal in selectedPortals)
            {
                var targetLayer = FindLayer(portal.portal.targetLayerId)
                                  ?? throw new ArchitectorDataException(
                                      $"The destination layer for Portal '{portal.title}' no longer exists.");
            }

            ExecuteLayerSetChange(
                selectedIds.Count == 1 ? "Delete Node" : "Delete Nodes",
                layers,
                documentsByLayerId =>
                {
                    var deletionsByLayerId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
                    {
                        [sourceLayer.id] = new HashSet<string>(selectedIds, StringComparer.Ordinal)
                    };
                    foreach (var selectedPortal in selectedPortals)
                    {
                        var targetDocuments = documentsByLayerId[selectedPortal.portal.targetLayerId];
                        var counterpart = targetDocuments.semantic.nodes.FirstOrDefault(node =>
                            string.Equals(node.id, selectedPortal.portal.counterpartNodeId, StringComparison.Ordinal));
                        if (counterpart == null
                            || !IsPortal(counterpart)
                            || !string.Equals(counterpart.portal.pairId, selectedPortal.portal.pairId, StringComparison.Ordinal)
                            || !string.Equals(counterpart.portal.targetLayerId, sourceLayer.id, StringComparison.Ordinal)
                            || !string.Equals(counterpart.portal.counterpartNodeId, selectedPortal.id, StringComparison.Ordinal)
                            || string.Equals(counterpart.portal.role, selectedPortal.portal.role, StringComparison.Ordinal))
                            throw new ArchitectorDataException(
                                $"Layer Portal '{selectedPortal.title}' has a missing or invalid counterpart.");
                        if (!deletionsByLayerId.TryGetValue(selectedPortal.portal.targetLayerId, out var targetDeletions))
                        {
                            targetDeletions = new HashSet<string>(StringComparer.Ordinal);
                            deletionsByLayerId.Add(selectedPortal.portal.targetLayerId, targetDeletions);
                        }
                        targetDeletions.Add(counterpart.id);
                    }

                    foreach (var deletion in deletionsByLayerId)
                        RemoveNodes(documentsByLayerId[deletion.Key], deletion.Value);
                    RemoveRelationshipsForNodes(
                        documentsByLayerId,
                        sourceLayer.id,
                        new HashSet<string>(selectedIds, StringComparer.Ordinal));
                    foreach (var selectedPortal in selectedPortals)
                        RemoveCrossRelationshipsBetween(
                            documentsByLayerId,
                            sourceLayer.id,
                            selectedPortal.portal.targetLayerId);
                });
        }

        private void RemoveLonelyPortals(IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers, bool dryRun)
        {
            var connected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in layers.Values.SelectMany(documents => documents.semantic.edges))
                if (!string.Equals(edge.sourceLayerId, edge.targetLayerId, StringComparison.Ordinal))
                    connected.Add(LayerPairKey(edge.sourceLayerId, edge.targetLayerId));
            foreach (var documents in layers.Values)
            {
                var lonely = documents.semantic.nodes
                    .Where(node => IsPortal(node) && !connected.Contains(LayerPairKey(documents.semantic.layerId, node.portal.targetLayerId)))
                    .ToArray();
                if (!dryRun)
                    foreach (var portal in documents.semantic.nodes.Where(IsPortal).Except(lonely))
                        unconnectedPortalPairIds.Remove(portal.portal.pairId);
                var removed = lonely
                    .Where(node => !unconnectedPortalPairIds.Contains(node.portal.pairId))
                    .Select(node => node.id)
                    .ToHashSet(StringComparer.Ordinal);
                if (removed.Count > 0)
                    RemoveNodes(documents, removed);
            }
        }

        private static string LayerPairKey(string first, string second)
        {
            return string.CompareOrdinal(first, second) <= 0 ? first + "|" + second : second + "|" + first;
        }

        private static void RemoveNodes(ArchitectorLayerDocuments documents, ISet<string> nodeIds)
        {
            documents.semantic.nodes.RemoveAll(node => nodeIds.Contains(node.id));
            documents.semantic.edges.RemoveAll(edge =>
                nodeIds.Contains(edge.sourceNodeId)
                || edge.targetLayerId == documents.semantic.layerId && nodeIds.Contains(edge.targetNodeId));
            documents.layout.nodes.RemoveAll(node => nodeIds.Contains(node.nodeId));
            var validEdgeIds = new HashSet<string>(documents.semantic.edges.Select(edge => edge.id), StringComparer.Ordinal);
            documents.layout.edges.RemoveAll(edge => !validEdgeIds.Contains(edge.edgeId));
        }

        private static void RemoveRelationshipsForNodes(
            IDictionary<string, ArchitectorLayerDocuments> documentsByLayerId,
            string layerId,
            ISet<string> nodeIds)
        {
            foreach (var documents in documentsByLayerId.Values)
            {
                var removed = documents.semantic.edges.Where(edge =>
                        edge.sourceLayerId == layerId && nodeIds.Contains(edge.sourceNodeId)
                        || edge.targetLayerId == layerId && nodeIds.Contains(edge.targetNodeId))
                    .Select(edge => edge.id)
                    .ToHashSet(StringComparer.Ordinal);
                if (removed.Count == 0)
                    continue;
                documents.semantic.edges.RemoveAll(edge => removed.Contains(edge.id));
                documents.layout.edges.RemoveAll(layout => removed.Contains(layout.edgeId));
            }
        }

        private static void RemoveCrossRelationshipsBetween(
            IDictionary<string, ArchitectorLayerDocuments> documentsByLayerId,
            string firstLayerId,
            string secondLayerId)
        {
            foreach (var documents in documentsByLayerId.Values)
            {
                var removed = documents.semantic.edges.Where(edge =>
                        edge.sourceLayerId == firstLayerId && edge.targetLayerId == secondLayerId
                        || edge.sourceLayerId == secondLayerId && edge.targetLayerId == firstLayerId)
                    .Select(edge => edge.id)
                    .ToHashSet(StringComparer.Ordinal);
                documents.semantic.edges.RemoveAll(edge => removed.Contains(edge.id));
                documents.layout.edges.RemoveAll(layout => removed.Contains(layout.edgeId));
            }
        }

        private static bool IsPortal(ArchitectorNodeRecord node)
        {
            return node != null && string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal);
        }

        private static bool IsPortalTo(ArchitectorNodeRecord node, string targetLayerId)
        {
            return IsPortal(node)
                   && string.Equals(node.portal?.targetLayerId, targetLayerId, StringComparison.Ordinal);
        }

        private static bool IsSameOrDescendantGroup(ArchitectorProjectDocument project, string candidateGroupId, string ancestorGroupId)
        {
            var cursor = candidateGroupId;
            while (!string.IsNullOrEmpty(cursor))
            {
                if (string.Equals(cursor, ancestorGroupId, StringComparison.Ordinal))
                    return true;
                cursor = project.groups.FirstOrDefault(group => group.id == cursor)?.parentGroupId ?? string.Empty;
            }
            return false;
        }

        private static int? AdjustInsertionIndex(IReadOnlyList<string> targetIds, ISet<string> movedIds, int? requestedIndex)
        {
            if (!requestedIndex.HasValue)
                return null;
            var clamped = Math.Max(0, Math.Min(requestedIndex.Value, targetIds.Count));
            var removedBefore = targetIds.Take(clamped).Count(movedIds.Contains);
            return clamped - removedBefore;
        }

        private static void InsertRange(List<string> target, IEnumerable<string> ids, int? insertionIndex)
        {
            var index = insertionIndex.HasValue
                ? Math.Max(0, Math.Min(insertionIndex.Value, target.Count))
                : target.Count;
            foreach (var id in ids)
                target.Insert(index++, id);
        }

        private static List<string> GetGroupIds(ArchitectorProjectDocument project, string parentGroupId)
        {
            return string.IsNullOrEmpty(parentGroupId)
                ? project.rootGroupIds
                : project.groups.First(group => group.id == parentGroupId).childGroupIds;
        }

        private static List<string> GetLayerIds(ArchitectorProjectDocument project, string parentGroupId)
        {
            return string.IsNullOrEmpty(parentGroupId)
                ? project.rootLayerIds
                : project.groups.First(group => group.id == parentGroupId).layerIds;
        }

        private static ArchitectorLayerRecord RequireLayer(ArchitectorProjectDocument project, string id)
        {
            return project.layers.FirstOrDefault(layer => layer.id == id)
                   ?? throw new ArchitectorDataException($"Layer '{id}' does not exist.");
        }

        private static HashSet<string> SiblingNames(
            ArchitectorProjectDocument project,
            string parentGroupId,
            string ignoredId = null)
        {
            var parent = parentGroupId ?? string.Empty;
            return new HashSet<string>(
                project.groups
                    .Where(group => string.Equals(group.parentGroupId ?? string.Empty, parent, StringComparison.Ordinal)
                                    && !string.Equals(group.id, ignoredId, StringComparison.Ordinal))
                    .Select(group => group.name)
                    .Concat(project.layers
                        .Where(layer => string.Equals(layer.parentGroupId ?? string.Empty, parent, StringComparison.Ordinal)
                                        && !string.Equals(layer.id, ignoredId, StringComparison.Ordinal))
                        .Select(layer => layer.name)),
                StringComparer.OrdinalIgnoreCase);
        }

        private static string MakeUniqueName(
            ArchitectorProjectDocument project,
            string parentGroupId,
            string preferredName,
            string ignoredId = null)
        {
            var baseName = string.IsNullOrWhiteSpace(preferredName) ? "Untitled" : preferredName.Trim();
            var used = SiblingNames(project, parentGroupId, ignoredId);
            if (!used.Contains(baseName))
                return baseName;
            for (var suffix = 2; ; suffix++)
            {
                var candidate = $"{baseName} {suffix}";
                if (!used.Contains(candidate))
                    return candidate;
            }
        }

        private string MakeUniqueNodeTitle(ArchitectorLayerDocument layer, string preferredName)
        {
            var used = new HashSet<string>(layer.nodes.Select(node => node.title), StringComparer.OrdinalIgnoreCase);
            if (!used.Contains(preferredName))
                return preferredName;
            for (var suffix = 2; ; suffix++)
            {
                var candidate = $"{preferredName} {suffix}";
                if (!used.Contains(candidate))
                    return candidate;
            }
        }

        private static string MakeUniqueManualTypeName(ArchitectorLayerDocument layer, string preferredName)
        {
            return MakeUniqueManualTypeName(layer, string.Empty, preferredName);
        }

        private static string MakeUniqueManualTypeName(ArchitectorLayerDocument layer, string typeNamespace, string preferredName)
        {
            var used = new HashSet<string>(
                layer.nodes
                    .Where(node => string.Equals(node.type?.mode, "declared", StringComparison.Ordinal)
                                   && string.Equals(node.type?.typeNamespace ?? string.Empty, typeNamespace ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    .Select(node => node.type?.name),
                StringComparer.OrdinalIgnoreCase);
            if (!used.Contains(preferredName))
                return preferredName;
            for (var suffix = 2; ; suffix++)
            {
                var candidate = $"{preferredName}{suffix}";
                if (!used.Contains(candidate))
                    return candidate;
            }
        }

        private static void EnsureLinkedTypeIsNotRepresented(ArchitectorLayerDocument layer, ArchitectorTypeRecord type)
        {
            var duplicate = layer.nodes.Any(node =>
                string.Equals(node.type?.mode, "linked", StringComparison.Ordinal)
                && string.Equals(node.type.assemblyName, type.assemblyName, StringComparison.Ordinal)
                && string.Equals(node.type.metadataName, type.metadataName, StringComparison.Ordinal));
            if (duplicate)
                throw new ArchitectorDataException($"Linked type '{type.metadataName}' is already represented in the target layer.");
        }

        private static string NormalizeEdgeSide(string side)
        {
            return side == "left" || side == "right" || side == "top" || side == "bottom" ? side : "auto";
        }

        private static string NormalizeEdgeKind(string kind)
        {
            if (ArchitectorEdgeKinds.All.Contains(kind, StringComparer.Ordinal))
                return kind;
            throw new ArchitectorDataException($"Unsupported relationship kind '{kind}'.");
        }

        private static string MakeTypeName(string title)
        {
            var characters = (title ?? string.Empty).Where(char.IsLetterOrDigit).ToArray();
            var candidate = new string(characters);
            if (candidate.Length == 0 || (!char.IsLetter(candidate[0]) && candidate[0] != '_'))
                return "NewType";
            return candidate;
        }

        private ArchitectorSize AlignNodeSizeToGrid(float minimumWidth, float minimumHeight)
        {
            if (!Settings.geometry.snapToGrid)
                return new ArchitectorSize(minimumWidth, minimumHeight);

            var grid = Settings.geometry.gridSize;
            return new ArchitectorSize(
                Math.Max(grid, (float)Math.Ceiling(minimumWidth / grid) * grid),
                Math.Max(grid, (float)Math.Ceiling(minimumHeight / grid) * grid));
        }

        private ArchitectorPoint FindAvailablePosition(ArchitectorLayerLayoutDocument layout, float requestedX, float requestedY, ArchitectorSize size, string ignoredNodeId)
        {
            var grid = Settings.geometry.snapToGrid ? Settings.geometry.gridSize : 1f;
            var originX = Snap(requestedX, grid);
            var originY = Snap(requestedY, grid);
            const int maxRings = 256;

            for (var ring = 0; ring <= maxRings; ring++)
            {
                for (var y = -ring; y <= ring; y++)
                {
                    for (var x = -ring; x <= ring; x++)
                    {
                        if (ring > 0 && Math.Abs(x) != ring && Math.Abs(y) != ring)
                            continue;
                        var candidate = new ArchitectorPoint(originX + x * grid, originY + y * grid);
                        if (IsPositionAvailable(layout, candidate, size, ignoredNodeId))
                            return candidate;
                    }
                }
            }

            throw new ArchitectorDataException("No valid node position was found near the requested location.");
        }

        private Dictionary<string, ArchitectorPoint> FindAvailableGroupPositions(
            ArchitectorLayerLayoutDocument layout,
            IReadOnlyDictionary<string, ArchitectorPoint> requestedPositions)
        {
            var grid = Settings.geometry.snapToGrid ? Settings.geometry.gridSize : 1f;
            var anchor = requestedPositions.OrderBy(pair => pair.Key, StringComparer.Ordinal).First().Value;
            var snapOffsetX = Snap(anchor.x, grid) - anchor.x;
            var snapOffsetY = Snap(anchor.y, grid) - anchor.y;
            const int maxRings = 256;

            for (var ring = 0; ring <= maxRings; ring++)
            {
                for (var y = -ring; y <= ring; y++)
                {
                    for (var x = -ring; x <= ring; x++)
                    {
                        if (ring > 0 && Math.Abs(x) != ring && Math.Abs(y) != ring)
                            continue;
                        var offsetX = snapOffsetX + x * grid;
                        var offsetY = snapOffsetY + y * grid;
                        var candidates = requestedPositions.ToDictionary(
                            pair => pair.Key,
                            pair => new ArchitectorPoint(pair.Value.x + offsetX, pair.Value.y + offsetY),
                            StringComparer.Ordinal);
                        if (IsGroupPositionAvailable(layout, candidates))
                            return candidates;
                    }
                }
            }

            throw new ArchitectorDataException("No valid group position was found near the requested location.");
        }

        private bool IsGroupPositionAvailable(ArchitectorLayerLayoutDocument layout, IReadOnlyDictionary<string, ArchitectorPoint> candidates)
        {
            var movedIds = new HashSet<string>(candidates.Keys, StringComparer.Ordinal);
            var gap = Settings.geometry.minimumNodeGap;
            foreach (var moved in layout.nodes.Where(node => movedIds.Contains(node.nodeId)))
            {
                var candidate = candidates[moved.nodeId];
                var left = candidate.x - gap;
                var top = candidate.y - gap;
                var right = candidate.x + moved.width + gap;
                var bottom = candidate.y + moved.height + gap;
                foreach (var existing in layout.nodes)
                {
                    if (movedIds.Contains(existing.nodeId))
                        continue;
                    if (right <= existing.x || left >= existing.x + existing.width || bottom <= existing.y || top >= existing.y + existing.height)
                        continue;
                    return false;
                }
            }
            return true;
        }

        private bool IsPositionAvailable(ArchitectorLayerLayoutDocument layout, ArchitectorPoint candidate, ArchitectorSize size, string ignoredNodeId)
        {
            var gap = Settings.geometry.minimumNodeGap;
            var left = candidate.x - gap;
            var top = candidate.y - gap;
            var right = candidate.x + size.width + gap;
            var bottom = candidate.y + size.height + gap;
            foreach (var existing in layout.nodes)
            {
                if (existing.nodeId == ignoredNodeId)
                    continue;
                if (right <= existing.x || left >= existing.x + existing.width || bottom <= existing.y || top >= existing.y + existing.height)
                    continue;
                return false;
            }
            return true;
        }

        private static float Snap(float value, float grid)
        {
            return (float)Math.Round(value / grid) * grid;
        }

        private static void Insert(List<string> values, string id, int? index)
        {
            if (index.HasValue)
                values.Insert(Math.Max(0, Math.Min(index.Value, values.Count)), id);
            else
                values.Add(id);
        }

        private static string NewId(string prefix)
        {
            return $"{prefix}_{Guid.NewGuid():N}";
        }

        private readonly struct ArchitectorFileSnapshot
        {
            public ArchitectorFileSnapshot(string relativePath, string content)
            {
                this.relativePath = relativePath;
                this.content = content;
            }

            public readonly string relativePath;
            public readonly string content;
        }

        private sealed class ArchitectorUndoRecord
        {
            public ArchitectorUndoRecord(string name, List<ArchitectorFileSnapshot> before, List<ArchitectorFileSnapshot> after)
            {
                this.name = name;
                this.before = before;
                this.after = after;
            }

            public readonly string name;
            public readonly List<ArchitectorFileSnapshot> before;
            public readonly List<ArchitectorFileSnapshot> after;
        }
    }

    public readonly struct ArchitectorDeleteSummary
    {
        public ArchitectorDeleteSummary(int groupCount, int layerCount)
        {
            GroupCount = groupCount;
            LayerCount = layerCount;
        }

        public int GroupCount { get; }
        public int LayerCount { get; }
    }

    public readonly struct ArchitectorLayerDocuments
    {
        public ArchitectorLayerDocuments(ArchitectorLayerDocument semantic, ArchitectorLayerLayoutDocument layout)
        {
            this.semantic = semantic;
            this.layout = layout;
        }

        public readonly ArchitectorLayerDocument semantic;
        public readonly ArchitectorLayerLayoutDocument layout;
    }

    public readonly struct ArchitectorPoint
    {
        public ArchitectorPoint(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public readonly float x;
        public readonly float y;
    }

    public readonly struct ArchitectorPortalPair
    {
        public ArchitectorPortalPair(string pairId, string sourceNodeId, string targetNodeId)
        {
            PairId = pairId;
            SourceNodeId = sourceNodeId;
            TargetNodeId = targetNodeId;
        }

        public string PairId { get; }
        public string SourceNodeId { get; }
        public string TargetNodeId { get; }
    }

    internal readonly struct ArchitectorSize
    {
        public ArchitectorSize(float width, float height)
        {
            this.width = width;
            this.height = height;
        }

        public readonly float width;
        public readonly float height;
    }

    internal readonly struct ArchitectorFileStamp : IEquatable<ArchitectorFileStamp>
    {
        private ArchitectorFileStamp(long length, long lastWriteTicks)
        {
            Length = length;
            LastWriteTicks = lastWriteTicks;
        }

        public long Length { get; }
        public long LastWriteTicks { get; }

        public static ArchitectorFileStamp Read(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new ArchitectorFileStamp(info.Length, info.LastWriteTimeUtc.Ticks)
                : new ArchitectorFileStamp(-1, 0);
        }

        public bool Equals(ArchitectorFileStamp other)
        {
            return Length == other.Length && LastWriteTicks == other.LastWriteTicks;
        }

        public override bool Equals(object obj)
        {
            return obj is ArchitectorFileStamp other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Length, LastWriteTicks);
        }
    }
}
