using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    [Serializable]
    public sealed class ArchitectorSemanticPlan
    {
        public List<ArchitectorPlanGroup> groups = new();
        public List<ArchitectorPlanLayer> layers = new();
        public List<ArchitectorPlanNode> nodes = new();
        public List<ArchitectorPlanNodeContract> nodeContracts = new();
        public List<ArchitectorPlanPortal> portals = new();
        public List<ArchitectorPlanEdge> edges = new();
        public List<ArchitectorPlanRename> renames = new();
        public List<ArchitectorPlanEntryMove> entryMoves = new();
        public List<ArchitectorPlanMove> moves = new();
        public List<ArchitectorPlanEdgeChange> edgeChanges = new();
        public List<ArchitectorPlanLayerPurpose> layerPurposes = new();
        public List<ArchitectorPlanLayerScope> layerScopes = new();
        public List<ArchitectorPlanDeletion> deletions = new();
    }

    [Serializable]
    public sealed class ArchitectorPlanGroup
    {
        public string reference = string.Empty;
        public string name = string.Empty;
        public string parent = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorPlanLayer
    {
        public string reference = string.Empty;
        public string name = string.Empty;
        public string parent = string.Empty;
        public string purpose = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorPlanNode
    {
        public string reference = string.Empty;
        public string layer = string.Empty;
        public string title = string.Empty;
        public string responsibility;
        public string mode = "none";
        public string typeKind = "class";
        public string typeNamespace = string.Empty;
        public string typeName = string.Empty;
        public string typeKey = string.Empty;
        public ArchitectorPlanTypeReference baseType;
        public List<ArchitectorPlanTypeReference> interfaces = new();
        public List<ArchitectorPlanMember> members = new();
        public float x;
        public float y;
    }

    [Serializable]
    public sealed class ArchitectorPlanMember
    {
        public string memberId = string.Empty;
        public string kind = "method";
        public string declaration = string.Empty;
        public string symbolKey = string.Empty;
        // A linked member may be named instead of keyed: name and, for overloads, the short parameter type names the
        // code facts list (member_ref). Without parameters any overload with the name matches.
        public string name = string.Empty;
        public List<string> parameters;
    }

    [Serializable]
    public sealed class ArchitectorPlanTypeReference
    {
        public string typeName = string.Empty;
        public string typeKey = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorPlanNodeContract
    {
        public string layer = string.Empty;
        public string node = string.Empty;
        public string title = string.Empty;
        public string responsibility;
        public string mode = string.Empty;
        public string typeKind = "class";
        public string typeNamespace = string.Empty;
        public string typeName = string.Empty;
        public string typeKey = string.Empty;
        // On a linked node an omitted (null) baseType, interfaces or members keeps what the node has;
        // an explicit empty list clears it.
        public ArchitectorPlanTypeReference baseType;
        public List<ArchitectorPlanTypeReference> interfaces;
        public List<ArchitectorPlanMember> members;
    }

    [Serializable]
    public sealed class ArchitectorPlanPortal
    {
        public string exitReference = string.Empty;
        public string entryReference = string.Empty;
        public string fromLayer = string.Empty;
        public string toLayer = string.Empty;
        public float exitX;
        public float exitY;
        public float entryX;
        public float entryY;
    }

    [Serializable]
    public sealed class ArchitectorPlanEdge
    {
        public string reference = string.Empty;
        public string sourceLayer = string.Empty;
        public string source = string.Empty;
        public string targetLayer = string.Empty;
        public string target = string.Empty;
        public string kind = ArchitectorEdgeKinds.Dependency;
        public string label = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorPlanRename
    {
        public string kind = "entry";
        public string target = string.Empty;
        public string layer = string.Empty;
        public string name = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorPlanMove
    {
        public string layer = string.Empty;
        public string node = string.Empty;
        public float x;
        public float y;
    }

    [Serializable]
    public sealed class ArchitectorPlanEntryMove
    {
        public List<string> entries = new();
        public string parent = string.Empty;
        public int groupIndex = -1;
        public int layerIndex = -1;
    }

    [Serializable]
    public sealed class ArchitectorPlanEdgeChange
    {
        public string edge = string.Empty;
        public string kind = ArchitectorEdgeKinds.Dependency;
        public string label = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorPlanLayerPurpose
    {
        public string layer = string.Empty;
        public string purpose = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorPlanLayerScope
    {
        public string layer = string.Empty;
        public List<string> folders = new();
        public List<string> namespaces = new();
    }

    [Serializable]
    public sealed class ArchitectorPlanDeletion
    {
        public string kind = string.Empty;
        public string target = string.Empty;
        public string layer = string.Empty;
        // For an edge named by its nodes ("Layer::Source -> Layer::Target"): the relationship kind and label, when the
        // node pair alone does not select one relationship.
        public string relationship = string.Empty;
        public string label = string.Empty;
    }

    // A layer pair whose last dependency relationship a plan removes: code between the layers would then be undeclared.
    public sealed class ArchitectorLostPermission
    {
        public ArchitectorLostPermission(string sourceLayerId, string sourcePath, string targetLayerId, string targetPath, int relationshipsRemoved)
        {
            SourceLayerId = sourceLayerId;
            SourcePath = sourcePath;
            TargetLayerId = targetLayerId;
            TargetPath = targetPath;
            RelationshipsRemoved = relationshipsRemoved;
        }

        public string SourceLayerId { get; }
        public string SourcePath { get; }
        public string TargetLayerId { get; }
        public string TargetPath { get; }
        public int RelationshipsRemoved { get; }
    }

    public sealed class ArchitectorSemanticPlanResult
    {
        public ArchitectorSemanticPlanResult(
            ArchitectorCanonicalBatchResult batch,
            IReadOnlyDictionary<string, string> references,
            IReadOnlyList<string> warnings = null,
            IReadOnlyList<ArchitectorLostPermission> permissionsLost = null)
        {
            Batch = batch;
            References = references;
            Warnings = warnings ?? Array.Empty<string>();
            PermissionsLost = permissionsLost ?? Array.Empty<ArchitectorLostPermission>();
        }

        public IReadOnlyList<ArchitectorLostPermission> PermissionsLost { get; }

        public ArchitectorCanonicalBatchResult Batch { get; }
        public IReadOnlyDictionary<string, string> References { get; }

        // What the plan removes without saying so, for example members of a linked node a nodeContracts entry dropped.
        public IReadOnlyList<string> Warnings { get; }
    }

    public sealed partial class ArchitectorRepository
    {
        public const string PlanOperationKey = "plan_operation";
        private string planOperation = string.Empty;
        private readonly List<string> planWarnings = new();

        public ArchitectorSemanticPlanResult DryRunSemanticPlan(
            ArchitectorSemanticPlan plan,
            string actionName = "Apply Semantic Plan",
            ArchitectorTypeIndex typeIndex = null)
        {
            var candidate = BuildLocatedSemanticPlan(plan, typeIndex);
            var lost = PermissionsLostBy(candidate);
            return new ArchitectorSemanticPlanResult(CommitSemanticPlan(candidate, actionName, true), candidate.references, planWarnings.ToArray(), lost);
        }

        // Layer pairs allowed by at least one cross-layer dependency before the plan and by none after it.
        private IReadOnlyList<ArchitectorLostPermission> PermissionsLostBy(SemanticPlanCandidate candidate)
        {
            static Dictionary<(string, string), HashSet<string>> Allowed(IEnumerable<ArchitectorEdgeRecord> edges) => edges
                .Where(edge => edge.kind == ArchitectorEdgeKinds.Dependency && edge.sourceLayerId != edge.targetLayerId)
                .GroupBy(edge => (edge.sourceLayerId, edge.targetLayerId))
                .ToDictionary(group => group.Key, group => group.Select(edge => edge.id).ToHashSet(StringComparer.Ordinal));
            if (Project.layers.Any(layer => HasLayerLoadError(layer.id)))
                return Array.Empty<ArchitectorLostPermission>();
            var before = Allowed(Project.layers.SelectMany(layer => PeekLayer(layer.id).semantic.edges));
            var after = Allowed(candidate.project.layers.SelectMany(layer =>
                (candidate.documents.TryGetValue(layer.id, out var changed) ? changed : PeekLayer(layer.id)).semantic.edges));
            return before
                .Where(pair => !after.ContainsKey(pair.Key)
                               && candidate.project.layers.Any(layer => layer.id == pair.Key.Item1)
                               && candidate.project.layers.Any(layer => layer.id == pair.Key.Item2))
                .Select(pair => new ArchitectorLostPermission(
                    pair.Key.Item1, ArchitectorEntryPaths.Describe(Project, pair.Key.Item1),
                    pair.Key.Item2, ArchitectorEntryPaths.Describe(Project, pair.Key.Item2),
                    pair.Value.Count))
                .OrderBy(item => item.SourcePath, StringComparer.Ordinal)
                .ThenBy(item => item.TargetPath, StringComparer.Ordinal)
                .ToArray();
        }

        public ArchitectorSemanticPlanResult ApplySemanticPlan(
            ArchitectorSemanticPlan plan,
            string actionName = "Apply Semantic Plan",
            ArchitectorTypeIndex typeIndex = null)
        {
            var candidate = BuildLocatedSemanticPlan(plan, typeIndex);
            return new ArchitectorSemanticPlanResult(CommitSemanticPlan(candidate, actionName, false), candidate.references, planWarnings.ToArray());
        }

        private ArchitectorCanonicalBatchResult CommitSemanticPlan(
            SemanticPlanCandidate candidate,
            string actionName,
            bool dryRun)
        {
            var layers = candidate.project.layers.ToDictionary(
                layer => layer.id,
                layer => candidate.documents.TryGetValue(layer.id, out var changed) ? changed : LoadLayer(layer.id),
                StringComparer.Ordinal);
            return Commit(
                string.IsNullOrWhiteSpace(actionName) ? "Apply Semantic Plan" : actionName.Trim(),
                candidate.project,
                layers,
                null,
                dryRun);
        }

        private SemanticPlanCandidate BuildLocatedSemanticPlan(ArchitectorSemanticPlan plan, ArchitectorTypeIndex typeIndex)
        {
            planOperation = string.Empty;
            planWarnings.Clear();
            try
            {
                return BuildSemanticPlan(plan, typeIndex);
            }
            catch (ArchitectorDataException exception)
            {
                exception.Data[PlanOperationKey] = planOperation;
                throw;
            }
        }

        private IEnumerable<T> Track<T>(IEnumerable<T> items, string section)
        {
            var index = 0;
            foreach (var item in items ?? Array.Empty<T>())
            {
                planOperation = $"{section}[{index++}]";
                yield return item;
            }
        }

        private SemanticPlanCandidate BuildSemanticPlan(
            ArchitectorSemanticPlan plan,
            ArchitectorTypeIndex typeIndex)
        {
            if (plan == null)
                throw new ArchitectorDataException("Semantic plan cannot be null.");

            var project = ArchitectorJson.Clone(Project);
            var references = new Dictionary<string, string>(StringComparer.Ordinal);
            var documents = new Dictionary<string, ArchitectorLayerDocuments>(StringComparer.Ordinal);

            foreach (var item in Track(plan.groups, "groups"))
            {
                var reference = AddReference(references, item?.reference, "group");
                var parentId = ResolveGroupReference(project, references, item?.parent);
                var id = NewId("group");
                project.groups.Add(new ArchitectorGroupRecord
                {
                    id = id,
                    name = MakeUniqueName(project, parentId, item?.name),
                    parentGroupId = parentId
                });
                GetGroupIds(project, parentId).Add(id);
                references[reference] = id;
            }

            foreach (var item in Track(plan.layers, "layers"))
            {
                var reference = AddReference(references, item?.reference, "layer");
                var parentId = ResolveGroupReference(project, references, item?.parent);
                var id = NewId("layer");
                project.layers.Add(new ArchitectorLayerRecord
                {
                    id = id,
                    name = MakeUniqueName(project, parentId, item?.name),
                    purpose = item?.purpose?.Trim() ?? string.Empty,
                    parentGroupId = parentId,
                    semanticPath = ArchitectorPaths.LayerPath(id),
                    layoutPath = ArchitectorPaths.LayoutPath(id)
                });
                GetLayerIds(project, parentId).Add(id);
                documents[id] = new ArchitectorLayerDocuments(
                    new ArchitectorLayerDocument { layerId = id },
                    new ArchitectorLayerLayoutDocument { layerId = id });
                references[reference] = id;
            }

            foreach (var item in Track(plan.nodes, "nodes"))
            {
                var reference = AddReference(references, item?.reference, "node");
                var layerId = ResolveLayerReference(project, references, item?.layer);
                var layerDocuments = GetPlanDocuments(layerId, documents);
                var nodeId = NewId("node");
                var size = AlignNodeSizeToGrid(280f,
                    string.Equals(item?.mode, "none", StringComparison.OrdinalIgnoreCase) ? 120f : 160f);
                var position = FindAvailablePosition(layerDocuments.layout, item?.x ?? 0f, item?.y ?? 0f, size, null);
                var title = MakeUniqueNodeTitle(layerDocuments.semantic,
                    string.IsNullOrWhiteSpace(item?.title) ? "New Node" : item.title.Trim());
                layerDocuments.semantic.nodes.Add(CreatePlannedNode(nodeId, title, item, layerDocuments.semantic, typeIndex));
                layerDocuments.layout.nodes.Add(new ArchitectorNodeLayoutRecord
                {
                    nodeId = nodeId,
                    x = position.x,
                    y = position.y,
                    width = size.width,
                    height = size.height
                });
                references[reference] = nodeId;
            }

            foreach (var item in Track(plan.layerPurposes, "layerPurposes"))
            {
                var layerId = ResolveLayerReference(project, references, item?.layer);
                project.layers.First(layer => layer.id == layerId).purpose = item?.purpose?.Trim() ?? string.Empty;
            }

            foreach (var item in Track(plan.layerScopes, "layerScopes"))
            {
                var layerId = ResolveLayerReference(project, references, item?.layer);
                project.layers.First(layer => layer.id == layerId).scope = new ArchitectorLayerScopeRecord
                {
                    folders = NormalizeScopeEntries(item?.folders, true),
                    namespaces = NormalizeScopeEntries(item?.namespaces, false)
                };
            }

            foreach (var item in Track(plan.nodeContracts, "nodeContracts"))
                ApplyPlannedNodeContract(project, references, documents, item, typeIndex);

            foreach (var item in Track(plan.portals, "portals"))
            {
                var exitReference = AddReference(references, item?.exitReference, "portal exit node");
                var entryReference = AddReference(references, item?.entryReference, "portal entry node");
                var fromLayerId = ResolveLayerReference(project, references, item?.fromLayer);
                var toLayerId = ResolveLayerReference(project, references, item?.toLayer);
                if (fromLayerId == toLayerId)
                    throw new ArchitectorDataException("A layer cannot contain a Portal to itself.");
                var fromLayer = project.layers.First(layer => layer.id == fromLayerId);
                var toLayer = project.layers.First(layer => layer.id == toLayerId);
                var source = GetPlanDocuments(fromLayerId, documents);
                var target = GetPlanDocuments(toLayerId, documents);
                EnsureLayerPortalPair(source, target, fromLayer, toLayer);
                var exitNodeId = source.semantic.nodes.First(node => IsPortalTo(node, toLayerId)).id;
                var entryNodeId = target.semantic.nodes.First(node => IsPortalTo(node, fromLayerId)).id;
                PlacePlanPortal(source, exitNodeId, item?.exitX ?? 0f, item?.exitY ?? 0f);
                PlacePlanPortal(target, entryNodeId, item?.entryX ?? 0f, item?.entryY ?? 0f);
                references[exitReference] = exitNodeId;
                references[entryReference] = entryNodeId;
            }

            foreach (var item in Track(plan.edges, "edges"))
            {
                var edgeReference = string.IsNullOrWhiteSpace(item?.reference)
                    ? string.Empty
                    : AddReference(references, item.reference, "relationship");
                var sourceLayerId = ResolveLayerReference(project, references, item?.sourceLayer);
                var targetLayerId = ResolveLayerReference(project, references, item?.targetLayer);
                var sourceDocuments = GetPlanDocuments(sourceLayerId, documents);
                var targetDocuments = GetPlanDocuments(targetLayerId, documents);
                var sourceId = ResolveNodeReference(sourceDocuments.semantic, references, item?.source);
                var targetId = ResolveNodeReference(targetDocuments.semantic, references, item?.target);
                var kind = NormalizeEdgeKind(item?.kind);
                var edgeId = NewId("edge");
                if (sourceLayerId != targetLayerId)
                    EnsureLayerPortalPair(
                        sourceDocuments,
                        targetDocuments,
                        project.layers.First(layer => layer.id == sourceLayerId),
                        project.layers.First(layer => layer.id == targetLayerId));
                sourceDocuments.semantic.edges.Add(new ArchitectorEdgeRecord
                {
                    id = edgeId,
                    sourceLayerId = sourceLayerId,
                    sourceNodeId = sourceId,
                    targetLayerId = targetLayerId,
                    targetNodeId = targetId,
                    kind = kind,
                    label = item?.label?.Trim() ?? string.Empty
                });
                sourceDocuments.layout.edges.Add(new ArchitectorEdgeLayoutRecord
                {
                    edgeId = edgeId,
                    preferredSourceSide = "auto",
                    preferredTargetSide = "auto"
                });
                sourceDocuments.layout.visibleRelationshipKinds |= ArchitectorRelationshipVisibility.Flag(kind);
                targetDocuments.layout.visibleRelationshipKinds |= ArchitectorRelationshipVisibility.Flag(kind);
                if (edgeReference.Length > 0)
                    references[edgeReference] = edgeId;
            }

            ApplyPlanRenames(project, documents, references, Track(plan.renames, "renames"));
            ApplyPlanEntryMoves(project, references, Track(plan.entryMoves, "entryMoves"));
            ApplyPlanMoves(project, documents, references, Track(plan.moves, "moves"));
            ApplyPlanEdgeChanges(project, documents, references, Track(plan.edgeChanges, "edgeChanges"));
            planOperation = "deletions";
            ApplyPlanDeletions(project, documents, references, plan.deletions);
            planOperation = string.Empty;
            return new SemanticPlanCandidate(project, documents, references);
        }

        private void PlacePlanPortal(ArchitectorLayerDocuments documents, string nodeId, float x, float y)
        {
            var layout = documents.layout.nodes.First(node => node.nodeId == nodeId);
            var position = FindAvailablePosition(documents.layout, x, y, new ArchitectorSize(layout.width, layout.height), nodeId);
            layout.x = position.x;
            layout.y = position.y;
        }

        private void ApplyPlanRenames(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            Dictionary<string, string> references,
            IEnumerable<ArchitectorPlanRename> renames)
        {
            foreach (var item in renames ?? Array.Empty<ArchitectorPlanRename>())
            {
                if (item == null)
                    throw new ArchitectorDataException("Semantic plan rename cannot be null.");
                var name = item.name?.Trim() ?? string.Empty;
                if (name.Length == 0)
                    throw new ArchitectorDataException("Renamed entities require a non-empty name.");
                switch (item.kind?.Trim().ToLowerInvariant())
                {
                    case "entry":
                    {
                        var id = ResolveEntryReference(project, references, item.target);
                        var group = project.groups.FirstOrDefault(candidate => candidate.id == id);
                        if (group != null)
                        {
                            group.name = name;
                            break;
                        }
                        var layer = project.layers.FirstOrDefault(candidate => candidate.id == id)
                                    ?? throw new ArchitectorDataException($"Explorer entry reference '{item.target}' does not exist.");
                        layer.name = name;
                        RefreshPortalTitles(
                            project,
                            layerId => documents.TryGetValue(layerId, out var known) ? known : LoadLayer(layerId),
                            layerId => GetPlanDocuments(layerId, documents),
                            layer.id);
                        break;
                    }
                    case "node":
                    {
                        var layerId = ResolveLayerReference(project, references, item.layer);
                        var layerDocuments = GetPlanDocuments(layerId, documents);
                        var nodeId = ResolveNodeReference(layerDocuments.semantic, references, item.target);
                        var node = layerDocuments.semantic.nodes.First(candidate => candidate.id == nodeId);
                        if (IsPortal(node))
                            throw new ArchitectorDataException("Layer Portal titles are derived from their destination layer and cannot be renamed directly.");
                        node.title = name;
                        break;
                    }
                    default:
                        throw new ArchitectorDataException($"Unsupported rename kind '{item.kind}'. Valid kinds: entry, node.");
                }
            }
        }

        private void ApplyPlanMoves(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            Dictionary<string, string> references,
            IEnumerable<ArchitectorPlanMove> moves)
        {
            var requestsByLayer = new Dictionary<string, Dictionary<string, ArchitectorPoint>>(StringComparer.Ordinal);
            foreach (var item in moves ?? Array.Empty<ArchitectorPlanMove>())
            {
                if (item == null)
                    throw new ArchitectorDataException("Semantic plan move cannot be null.");
                var layerId = ResolveLayerReference(project, references, item.layer);
                var layerDocuments = GetPlanDocuments(layerId, documents);
                var nodeId = ResolveNodeReference(layerDocuments.semantic, references, item.node);
                if (!requestsByLayer.TryGetValue(layerId, out var requests))
                {
                    requests = new Dictionary<string, ArchitectorPoint>(StringComparer.Ordinal);
                    requestsByLayer.Add(layerId, requests);
                }
                if (!requests.TryAdd(nodeId, new ArchitectorPoint(item.x, item.y)))
                    throw new ArchitectorDataException($"Node reference '{item.node}' is moved more than once in one semantic plan.");
            }

            foreach (var layerRequests in requestsByLayer)
            {
                var layerDocuments = GetPlanDocuments(layerRequests.Key, documents);
                var positions = FindAvailableGroupPositions(layerDocuments.layout, layerRequests.Value);
                foreach (var nodeLayout in layerDocuments.layout.nodes)
                {
                    if (!positions.TryGetValue(nodeLayout.nodeId, out var position))
                        continue;
                    nodeLayout.x = position.x;
                    nodeLayout.y = position.y;
                }
            }
        }

        private static void ApplyPlanEntryMoves(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, string> references,
            IEnumerable<ArchitectorPlanEntryMove> moves)
        {
            var movedEntries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in moves ?? Array.Empty<ArchitectorPlanEntryMove>())
            {
                if (item == null)
                    throw new ArchitectorDataException("Semantic plan Explorer entry move cannot be null.");
                if (item.entries == null || item.entries.Count == 0)
                    throw new ArchitectorDataException("Semantic plan Explorer entry move requires at least one entry.");

                var entryIds = item.entries.Select(entry => ResolveEntryReference(project, references, entry)).ToArray();
                foreach (var entryId in entryIds)
                {
                    if (!movedEntries.Add(entryId))
                        throw new ArchitectorDataException(
                            $"Explorer entry reference '{entryId}' is moved more than once in one semantic plan.");
                }

                var parentId = ResolveGroupReference(project, references, item.parent);
                MoveEntriesInProject(
                    project,
                    entryIds,
                    parentId,
                    item.groupIndex >= 0 ? item.groupIndex : null,
                    item.layerIndex >= 0 ? item.layerIndex : null);
            }
        }

        private void ApplyPlanEdgeChanges(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            Dictionary<string, string> references,
            IEnumerable<ArchitectorPlanEdgeChange> changes)
        {
            var changedEdges = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in changes ?? Array.Empty<ArchitectorPlanEdgeChange>())
            {
                if (item == null)
                    throw new ArchitectorDataException("Semantic plan relationship change cannot be null.");
                var edgeId = ResolveReference(references, item.edge);
                if (!changedEdges.Add(edgeId))
                    throw new ArchitectorDataException($"Relationship reference '{item.edge}' is changed more than once in one semantic plan.");
                var edge = FindPlanEdge(project, documents, edgeId);
                edge.kind = NormalizeEdgeKind(item.kind);
                edge.label = item.label?.Trim() ?? string.Empty;
                GetPlanDocuments(edge.sourceLayerId, documents).layout.visibleRelationshipKinds |=
                    ArchitectorRelationshipVisibility.Flag(edge.kind);
                GetPlanDocuments(edge.targetLayerId, documents).layout.visibleRelationshipKinds |=
                    ArchitectorRelationshipVisibility.Flag(edge.kind);
            }
        }

        private void ApplyPlanDeletions(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            Dictionary<string, string> references,
            IEnumerable<ArchitectorPlanDeletion> deletions)
        {
            var items = (deletions ?? Array.Empty<ArchitectorPlanDeletion>()).ToArray();
            if (items.Any(item => item == null))
                throw new ArchitectorDataException("Semantic plan deletion cannot be null.");
            foreach (var item in items)
            {
                var kind = item.kind?.Trim().ToLowerInvariant();
                if (kind != "entry" && kind != "node" && kind != "edge")
                    throw new ArchitectorDataException($"Unsupported deletion kind '{item.kind}'. Valid kinds: entry, node, edge.");
            }

            DeletePlanEdges(project, documents, references, items.Where(item =>
                string.Equals(item.kind?.Trim(), "edge", StringComparison.OrdinalIgnoreCase)));
            DeletePlanNodes(project, documents, references, items.Where(item =>
                string.Equals(item.kind?.Trim(), "node", StringComparison.OrdinalIgnoreCase)));
            DeletePlanEntries(project, documents, references, items.Where(item =>
                string.Equals(item.kind?.Trim(), "entry", StringComparison.OrdinalIgnoreCase)));
        }

        private void DeletePlanEdges(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            IReadOnlyDictionary<string, string> references,
            IEnumerable<ArchitectorPlanDeletion> deletions)
        {
            var deleted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in deletions)
            {
                var edgeId = ResolveReference(references, item.target);
                if (edgeId.Contains("->", StringComparison.Ordinal))
                    edgeId = ResolveEdgeByNodes(project, documents, references, item);
                if (!deleted.Add(edgeId))
                    throw new ArchitectorDataException($"Relationship reference '{item.target}' is deleted more than once in one semantic plan.");
                var owner = project.layers.FirstOrDefault(layer =>
                    GetPlanDocuments(layer.id, documents).semantic.edges.Any(edge => edge.id == edgeId))
                            ?? throw new ArchitectorDataException($"Relationship reference '{item.target}' does not exist.");
                var layerDocuments = GetPlanDocuments(owner.id, documents);
                layerDocuments.semantic.edges.RemoveAll(edge => edge.id == edgeId);
                layerDocuments.layout.edges.RemoveAll(edge => edge.edgeId == edgeId);
            }
        }

        private void DeletePlanNodes(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            IReadOnlyDictionary<string, string> references,
            IEnumerable<ArchitectorPlanDeletion> deletions)
        {
            var items = deletions.ToArray();
            if (items.Length == 0)
                return;
            var idsByLayer = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var deletedPortalLayerPairs = new List<(string first, string second)>();
            foreach (var item in items)
            {
                var (layerPart, _) = SplitNodeName(item.target);
                var layerId = ResolveLayerReference(project, references, string.IsNullOrWhiteSpace(item.layer) ? layerPart : item.layer);
                var layerDocuments = GetPlanDocuments(layerId, documents);
                var nodeId = ResolveNodeReference(layerDocuments.semantic, references, item.target);
                if (!idsByLayer.TryGetValue(layerId, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    idsByLayer.Add(layerId, ids);
                }
                if (!ids.Add(nodeId))
                    throw new ArchitectorDataException($"Node reference '{item.target}' is deleted more than once in one semantic plan.");
            }

            foreach (var sourceDeletion in idsByLayer.ToArray())
            {
                var sourceDocuments = GetPlanDocuments(sourceDeletion.Key, documents);
                foreach (var portal in sourceDocuments.semantic.nodes.Where(node =>
                             sourceDeletion.Value.Contains(node.id) && IsPortal(node)).ToArray())
                {
                    var targetLayerId = portal.portal.targetLayerId;
                    deletedPortalLayerPairs.Add((sourceDeletion.Key, targetLayerId));
                    if (project.layers.All(layer => layer.id != targetLayerId))
                        throw new ArchitectorDataException($"The destination layer for Portal '{portal.title}' no longer exists.");
                    var targetDocuments = GetPlanDocuments(targetLayerId, documents);
                    var counterpart = targetDocuments.semantic.nodes.FirstOrDefault(node =>
                        node.id == portal.portal.counterpartNodeId
                        && IsPortal(node)
                        && node.portal.pairId == portal.portal.pairId
                        && node.portal.targetLayerId == sourceDeletion.Key
                        && node.portal.counterpartNodeId == portal.id
                        && node.portal.role != portal.portal.role);
                    if (counterpart == null)
                        throw new ArchitectorDataException($"Layer Portal '{portal.title}' has a missing or invalid counterpart.");
                    if (!idsByLayer.TryGetValue(targetLayerId, out var targetIds))
                    {
                        targetIds = new HashSet<string>(StringComparer.Ordinal);
                        idsByLayer.Add(targetLayerId, targetIds);
                    }
                    targetIds.Add(counterpart.id);
                }
            }

            foreach (var deletion in idsByLayer)
                RemoveNodes(GetPlanDocuments(deletion.Key, documents), deletion.Value);
            foreach (var layer in project.layers)
                GetPlanDocuments(layer.id, documents);
            foreach (var deletion in idsByLayer)
                RemoveRelationshipsForNodes(documents, deletion.Key, deletion.Value);

            foreach (var pair in deletedPortalLayerPairs.Distinct())
                RemoveCrossRelationshipsBetween(documents, pair.first, pair.second);
        }

        private void DeletePlanEntries(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            IReadOnlyDictionary<string, string> references,
            IEnumerable<ArchitectorPlanDeletion> deletions)
        {
            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            var layerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in deletions)
            {
                var id = ResolveEntryReference(project, references, item.target);
                var group = project.groups.FirstOrDefault(candidate => candidate.id == id);
                if (group != null)
                {
                    CollectDescendants(project, group.id, groupIds, layerIds);
                    continue;
                }
                var layer = project.layers.FirstOrDefault(candidate => candidate.id == id)
                            ?? throw new ArchitectorDataException($"Explorer entry reference '{item.target}' does not exist.");
                layerIds.Add(layer.id);
            }
            if (groupIds.Count == 0 && layerIds.Count == 0)
                return;

            foreach (var survivingLayer in project.layers.Where(layer => !layerIds.Contains(layer.id)).ToArray())
            {
                var alreadyLoaded = documents.TryGetValue(survivingLayer.id, out var layerDocuments);
                if (!alreadyLoaded)
                    layerDocuments = LoadLayer(survivingLayer.id);
                var portalIds = layerDocuments.semantic.nodes
                    .Where(node => IsPortal(node) && layerIds.Contains(node.portal.targetLayerId))
                    .Select(node => node.id)
                    .ToHashSet(StringComparer.Ordinal);
                if (portalIds.Count == 0)
                    continue;
                if (!alreadyLoaded)
                    documents.Add(survivingLayer.id, layerDocuments);
                RemoveNodes(layerDocuments, portalIds);
                layerDocuments.semantic.edges.RemoveAll(edge => layerIds.Contains(edge.targetLayerId));
                var validEdgeIds = layerDocuments.semantic.edges.Select(edge => edge.id)
                    .ToHashSet(StringComparer.Ordinal);
                layerDocuments.layout.edges.RemoveAll(edge => !validEdgeIds.Contains(edge.edgeId));
            }

            foreach (var layer in project.layers.Where(layer => layerIds.Contains(layer.id)).ToArray())
            {
                GetLayerIds(project, layer.parentGroupId).Remove(layer.id);
                documents.Remove(layer.id);
            }
            foreach (var group in project.groups.Where(group => groupIds.Contains(group.id)).ToArray())
                GetGroupIds(project, group.parentGroupId).Remove(group.id);
            project.layers.RemoveAll(layer => layerIds.Contains(layer.id));
            project.groups.RemoveAll(group => groupIds.Contains(group.id));
        }

        private ArchitectorLayerDocuments GetPlanDocuments(
            string layerId,
            IDictionary<string, ArchitectorLayerDocuments> documents)
        {
            if (!documents.TryGetValue(layerId, out var result))
            {
                result = LoadLayer(layerId);
                documents.Add(layerId, result);
            }
            return result;
        }

        private ArchitectorEdgeRecord FindPlanEdge(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            string edgeId)
        {
            return project.layers.Select(layer => GetPlanDocuments(layer.id, documents).semantic.edges
                    .FirstOrDefault(edge => edge.id == edgeId))
                .FirstOrDefault(edge => edge != null)
                ?? throw new ArchitectorDataException($"Relationship '{edgeId}' does not exist.");
        }

        private ArchitectorNodeRecord CreatePlannedNode(
            string nodeId,
            string title,
            ArchitectorPlanNode item,
            ArchitectorLayerDocument layer,
            ArchitectorTypeIndex typeIndex)
        {
            var node = CreatePlannedNodeContract(nodeId, title, item, layer, typeIndex);
            node.responsibility = PlannedResponsibility(item?.responsibility, title);
            return node;
        }

        // Checked here and not only by validation, so the failure names the plan item.
        private static string PlannedResponsibility(string value, string title)
        {
            var text = ArchitectorNodeResponsibility.Normalize(value);
            if (text.Length > ArchitectorNodeResponsibility.MaxLength)
                throw new ArchitectorDataException(
                    $"Node '{title}' responsibility has {text.Length} characters; the limit is {ArchitectorNodeResponsibility.MaxLength}.");
            return text;
        }

        // A linked node names its type by typeKey, or by typeNamespace and typeName (assemblyName is not needed when
        // the pair names one indexed type).
        private static string PlannedTypeKey(string typeKey, string typeNamespace, string typeName, string title, ArchitectorTypeIndex typeIndex)
        {
            if (!string.IsNullOrWhiteSpace(typeKey))
                return typeKey.Trim();
            if (string.IsNullOrWhiteSpace(typeName))
                throw new ArchitectorDataException(
                    $"Linked node '{title}' needs typeKey, or typeNamespace and typeName of one indexed type; none is given.");
            var space = typeNamespace?.Trim() ?? string.Empty;
            var name = typeName.Trim();
            var fullName = space.Length == 0 ? name : $"{space}.{name}";
            var matches = typeIndex.Types
                .Where(type => string.Equals(type.FullDisplayName, fullName, StringComparison.Ordinal)
                               || string.Equals(type.MetadataName, fullName, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 1)
                return matches[0].Key;
            var candidates = (matches.Length > 1 ? matches : typeIndex.Types.Where(type => string.Equals(type.DisplayName, name, StringComparison.Ordinal)))
                .Take(5)
                .Select(type => type.Key)
                .ToArray();
            throw new ArchitectorDataException(matches.Length > 1
                ? $"Linked node '{title}': typeNamespace '{space}' and typeName '{name}' match {matches.Length} indexed types; give typeKey: {string.Join(", ", candidates)}."
                : $"Linked node '{title}': no indexed type '{fullName}' (typeNamespace + typeName)."
                  + (candidates.Length > 0 ? $" Types with that name: {string.Join(", ", candidates)}." : string.Empty));
        }

        private ArchitectorNodeRecord CreatePlannedNodeContract(
            string nodeId,
            string title,
            ArchitectorPlanNode item,
            ArchitectorLayerDocument layer,
            ArchitectorTypeIndex typeIndex)
        {
            var mode = item?.mode?.Trim().ToLowerInvariant() ?? "none";
            if (mode == "none")
                return new ArchitectorNodeRecord { id = nodeId, title = title, type = new ArchitectorTypeRecord { mode = "none" } };
            if (mode == "declared" || mode == "manual")
            {
                if (string.IsNullOrWhiteSpace(item.typeName))
                    throw new ArchitectorDataException($"Manual node '{title}' requires typeName.");
                var type = new ArchitectorTypeRecord
                {
                    mode = "declared",
                    kind = string.IsNullOrWhiteSpace(item.typeKind) ? "class" : item.typeKind.Trim(),
                    typeNamespace = item.typeNamespace?.Trim() ?? string.Empty,
                    name = item.typeName.Trim()
                };
                ApplyPlannedTypeFacts(type, item.baseType, item.interfaces, typeIndex, false);
                var fullName = string.IsNullOrEmpty(type.typeNamespace) ? type.name : $"{type.typeNamespace}.{type.name}";
                if (layer.nodes.Any(node => string.Equals(node.type?.mode, "declared", StringComparison.Ordinal)
                                            && string.Equals(DeclaredTypeName(node.type), fullName, StringComparison.OrdinalIgnoreCase)))
                    throw new ArchitectorDataException($"Manual type '{fullName}' is already represented in this layer.");
                return new ArchitectorNodeRecord
                {
                    id = nodeId,
                    title = title,
                    type = type,
                    members = (item.members ?? new List<ArchitectorPlanMember>()).Select(CreateDraftMember).ToList()
                };
            }
            if (mode != "linked")
                throw new ArchitectorDataException($"Unsupported planned node mode '{item?.mode}'.");
            if (typeIndex == null)
                throw new ArchitectorDataException("Linked semantic-plan nodes require the local C# type index.");
            var typeKey = PlannedTypeKey(item.typeKey, item.typeNamespace, item.typeName, title, typeIndex);
            var indexedType = typeIndex.Types.SingleOrDefault(type => string.Equals(type.Key, typeKey, StringComparison.Ordinal))
                              ?? throw new ArchitectorDataException($"Indexed type '{typeKey}' (typeKey) was not found.");
            EnsureLinkedTypeIsUnique(layer, indexedType, null);
            var result = CreateLinkedNodeRecord(nodeId, title, indexedType);
            ApplyPlannedTypeFacts(result.type, item.baseType, item.interfaces, typeIndex, true);
            var available = typeIndex.GetMembers(result.type).ToDictionary(member => member.SymbolKey, StringComparer.Ordinal);
            foreach (var planned in item.members ?? new List<ArchitectorPlanMember>())
            {
                var member = ResolvePlannedMember(planned, available, indexedType.FullDisplayName);
                result.members.Add(new ArchitectorMemberRecord
                {
                    id = NewId("member"),
                    kind = member.Kind,
                    declaration = member.Declaration,
                    symbolKey = member.SymbolKey
                });
            }
            result.members = ArchitectorMemberOrder.SortStable(result.members);
            return result;
        }

        // A linked member by symbolKey, or by name with an optional kind and parameter list (member_ref).
        private static ArchitectorIndexedMember ResolvePlannedMember(
            ArchitectorPlanMember planned,
            IReadOnlyDictionary<string, ArchitectorIndexedMember> available,
            string owner)
        {
            if (planned == null)
                throw new ArchitectorDataException($"A planned member of '{owner}' is empty.");
            if (!string.IsNullOrWhiteSpace(planned.symbolKey))
                return available.TryGetValue(planned.symbolKey, out var keyed)
                    ? keyed
                    : throw new ArchitectorDataException($"Linked member '{planned.symbolKey}' is not available on '{owner}'.");
            if (string.IsNullOrWhiteSpace(planned.name))
                throw new ArchitectorDataException($"A linked member of '{owner}' needs a symbolKey or a name (member_ref from layer_facts).");
            var matches = available.Values.Where(member => ArchitectorMemberReferences.Matches(member, planned.kind, planned.name, planned.parameters)).ToArray();
            // kind defaults to "method", so a name given without a kind also finds a property, field or event.
            if (matches.Length == 0)
                matches = available.Values.Where(member => ArchitectorMemberReferences.Matches(member, null, planned.name, planned.parameters)).ToArray();
            if (matches.Length == 1)
                return matches[0];
            var named = available.Values.Where(member => ArchitectorMemberReferences.Matches(member, null, planned.name, null)).Take(8).ToArray();
            throw new ArchitectorDataException(matches.Length == 0
                ? $"'{owner}' has no member '{planned.name}'{(planned.parameters == null ? string.Empty : $"({string.Join(", ", planned.parameters)})")}."
                  + (named.Length == 0 ? string.Empty : $" Members with that name: {string.Join("; ", named.Select(ArchitectorMemberReferences.Describe))}.")
                : $"Member '{planned.name}' of '{owner}' is ambiguous; add parameters: {string.Join("; ", matches.Take(8).Select(ArchitectorMemberReferences.Describe))}.");
        }

        private ArchitectorMemberRecord CreateDraftMember(ArchitectorPlanMember member)
        {
            if (member == null || string.IsNullOrWhiteSpace(member.declaration))
                throw new ArchitectorDataException("Every planned manual member requires a declaration.");
            return new ArchitectorMemberRecord
            {
                id = string.IsNullOrWhiteSpace(member.memberId) ? NewId("member") : member.memberId.Trim(),
                kind = string.IsNullOrWhiteSpace(member.kind) ? "method" : member.kind.Trim(),
                declaration = member.declaration.Trim()
            };
        }

        private void ApplyPlannedNodeContract(
            ArchitectorProjectDocument project,
            Dictionary<string, string> references,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            ArchitectorPlanNodeContract item,
            ArchitectorTypeIndex typeIndex)
        {
            var layerId = ResolveMutableReference(references, item?.layer);
            if (project.layers.All(layer => layer.id != layerId))
                layerId = ArchitectorEntryPaths.Resolve(project, item?.layer);
            if (project.layers.All(layer => layer.id != layerId))
                throw new ArchitectorDataException($"Layer reference '{item?.layer}' does not exist.");
            var layer = GetPlanDocuments(layerId, documents);
            var nodeId = ResolveMutableReference(references, item?.node);
            if (layer.semantic.nodes.All(node => node.id != nodeId))
                nodeId = ResolveNodeTitle(layer.semantic, item?.node)
                         ?? throw new ArchitectorDataException($"Node reference '{item?.node}' does not exist in the selected layer.");
            var index = layer.semantic.nodes.FindIndex(node => node.id == nodeId);
            var existing = layer.semantic.nodes[index];
            if (IsPortal(existing))
                throw new ArchitectorDataException("Portal nodes cannot receive type contracts.");
            var mode = string.IsNullOrWhiteSpace(item?.mode) ? existing.type?.mode ?? "none" : item.mode.Trim().ToLowerInvariant();
            var planned = new ArchitectorPlanNode
            {
                title = string.IsNullOrWhiteSpace(item?.title) ? existing.title : item.title.Trim(),
                responsibility = item?.responsibility ?? existing.responsibility,
                mode = mode,
                typeKind = item?.typeKind,
                typeNamespace = item?.typeNamespace,
                typeName = item?.typeName,
                typeKey = item?.typeKey,
                baseType = item?.baseType,
                interfaces = item?.interfaces ?? new List<ArchitectorPlanTypeReference>(),
                members = item?.members ?? new List<ArchitectorPlanMember>()
            };
            if (string.Equals(existing.type?.mode, "none", StringComparison.Ordinal) && mode != "none")
            {
                var replacementId = NewId("node");
                var replacement = CreatePlannedNode(replacementId, planned.title, planned, layer.semantic, typeIndex);
                layer.semantic.nodes[index] = replacement;
                layer.layout.nodes.First(layout => layout.nodeId == nodeId).nodeId = replacementId;
                foreach (var projectLayer in project.layers)
                    GetPlanDocuments(projectLayer.id, documents);
                RemoveRelationshipsForNodes(
                    documents,
                    layerId,
                    new HashSet<string>(new[] { nodeId }, StringComparer.Ordinal));
                foreach (var key in references.Where(pair => pair.Value == nodeId).Select(pair => pair.Key).ToArray())
                    references[key] = replacementId;
                return;
            }
            if (string.Equals(existing.type?.mode, "declared", StringComparison.Ordinal) && (mode == "declared" || mode == "manual"))
            {
                var replacement = CreatePlannedNode(nodeId, planned.title, planned, new ArchitectorLayerDocument
                {
                    layerId = layer.semantic.layerId,
                    nodes = layer.semantic.nodes.Where(node => node.id != nodeId).ToList()
                }, typeIndex);
                layer.semantic.nodes[index] = replacement;
                return;
            }
            if (string.Equals(existing.type?.mode, "declared", StringComparison.Ordinal) && mode == "linked")
            {
                if (existing.members.Count > 0 && planned.members.Count == 0)
                    throw new ArchitectorDataException(
                        $"Manual node '{existing.title}' has draft members; a linked transition must provide their explicit linked-member selection.");
                var replacement = CreatePlannedNode(nodeId, planned.title, planned, new ArchitectorLayerDocument
                {
                    layerId = layer.semantic.layerId,
                    nodes = layer.semantic.nodes.Where(node => node.id != nodeId).ToList()
                }, typeIndex);
                if (item?.baseType == null && (item?.interfaces == null || item.interfaces.Count == 0))
                {
                    replacement.type.baseType = existing.type.baseType == null ? null : ArchitectorJson.Clone(existing.type.baseType);
                    replacement.type.interfaces = (existing.type.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                        .Select(ArchitectorJson.Clone)
                        .ToList();
                }
                layer.semantic.nodes[index] = replacement;
                return;
            }
            if (string.Equals(existing.type?.mode, "linked", StringComparison.Ordinal) && mode == "linked")
            {
                var existingKey = ArchitectorTypeIdentity.Key(existing.type.assemblyName, existing.type.metadataName);
                if (!string.IsNullOrWhiteSpace(item.typeKey) && !string.Equals(existingKey, item.typeKey, StringComparison.Ordinal))
                    throw new ArchitectorDataException("Changing one Linked Type identity into another is not supported; clear or replace the node deliberately.");
                var available = typeIndex?.GetMembers(existing.type).ToDictionary(member => member.SymbolKey, StringComparer.Ordinal)
                                ?? throw new ArchitectorDataException("Linked member selection requires the local C# type index.");
                var existingByKey = existing.members.ToDictionary(member => member.symbolKey, StringComparer.Ordinal);
                if (item.members != null)
                {
                    var before = existing.members;
                    existing.members = item.members.Select(member =>
                    {
                        var indexed = ResolvePlannedMember(member, available, existing.title);
                        return existingByKey.TryGetValue(indexed.SymbolKey, out var saved)
                            ? saved
                            : new ArchitectorMemberRecord
                            {
                                id = NewId("member"), kind = indexed.Kind, declaration = indexed.Declaration, symbolKey = indexed.SymbolKey
                            };
                    }).ToList();
                    var kept = existing.members.Select(member => member.symbolKey).ToHashSet(StringComparer.Ordinal);
                    foreach (var removed in before.Where(member => !kept.Contains(member.symbolKey)))
                        planWarnings.Add($"{planOperation}: removes member '{removed.declaration}' from node '{existing.title}'.");
                    existing.members = ArchitectorMemberOrder.SortStable(existing.members);
                }
                var keptBase = existing.type.baseType;
                var keptInterfaces = existing.type.interfaces;
                if (item.baseType != null || item.interfaces != null)
                    ApplyPlannedTypeFacts(existing.type, item.baseType, item.interfaces ?? new List<ArchitectorPlanTypeReference>(), typeIndex, true);
                if (item.baseType == null)
                    existing.type.baseType = keptBase;
                if (item.interfaces == null)
                    existing.type.interfaces = keptInterfaces;
                else
                    foreach (var removed in (keptInterfaces ?? new List<ArchitectorTypeReferenceRecord>())
                                 .Where(before => existing.type.interfaces.All(after => ArchitectorTypeReference.Key(after) != ArchitectorTypeReference.Key(before))))
                        planWarnings.Add($"{planOperation}: removes interface '{removed.name}' from node '{existing.title}'.");
                existing.title = planned.title;
                existing.responsibility = PlannedResponsibility(planned.responsibility, planned.title);
                return;
            }
            if (mode == "none" && string.Equals(existing.type?.mode, "none", StringComparison.Ordinal))
            {
                existing.title = planned.title;
                existing.responsibility = PlannedResponsibility(planned.responsibility, planned.title);
                return;
            }
            throw new ArchitectorDataException($"Unsupported node contract transition '{existing.type?.mode}' -> '{mode}'.");
        }

        private static void ApplyPlannedTypeFacts(
            ArchitectorTypeRecord owner,
            ArchitectorPlanTypeReference baseType,
            IReadOnlyList<ArchitectorPlanTypeReference> interfaces,
            ArchitectorTypeIndex typeIndex,
            bool linkedOwner)
        {
            interfaces ??= Array.Empty<ArchitectorPlanTypeReference>();
            if (!linkedOwner)
            {
                owner.baseType = PlannedReference(baseType, "class", typeIndex);
                owner.interfaces = interfaces
                    .Select(item => PlannedReference(item, "interface", typeIndex))
                    .Where(item => item != null)
                    .OrderBy(ArchitectorTypeReference.Key, StringComparer.Ordinal)
                    .ToList();
                return;
            }

            if (typeIndex == null)
                throw new ArchitectorDataException("Linked type facts require the local C# type index.");
            var actualBase = typeIndex.GetDirectBase(owner);
            if (baseType == null || string.IsNullOrWhiteSpace(baseType.typeKey))
                owner.baseType = null;
            else if (actualBase == null || !string.Equals(actualBase.Key, baseType.typeKey, StringComparison.Ordinal))
                throw new ArchitectorDataException($"Direct base type '{baseType.typeKey}' is not available on '{owner.metadataName}'.");
            else
                owner.baseType = actualBase.ToRecord();

            var available = typeIndex.GetInterfaces(owner).ToDictionary(item => item.Key, StringComparer.Ordinal);
            owner.interfaces = interfaces.Select(item =>
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.typeKey) || !available.TryGetValue(item.typeKey, out var found))
                        throw new ArchitectorDataException($"Interface '{item?.typeKey}' is not available on '{owner.metadataName}'.");
                    return found.ToRecord();
                })
                .OrderBy(ArchitectorTypeReference.Key, StringComparer.Ordinal)
                .ToList();
        }

        private static ArchitectorTypeReferenceRecord PlannedReference(ArchitectorPlanTypeReference reference, string kind, ArchitectorTypeIndex typeIndex)
        {
            if (!string.IsNullOrWhiteSpace(reference?.typeKey))
            {
                var indexed = typeIndex?.Types.SingleOrDefault(type => string.Equals(type.Key, reference.typeKey, StringComparison.Ordinal))
                              ?? throw new ArchitectorDataException($"Indexed type '{reference.typeKey}' was not found.");
                return new ArchitectorTypeReferenceRecord
                {
                    mode = "linked",
                    kind = indexed.Kind,
                    typeNamespace = indexed.Namespace,
                    name = indexed.DisplayName,
                    assemblyName = indexed.AssemblyName,
                    metadataName = indexed.MetadataName
                };
            }

            return string.IsNullOrWhiteSpace(reference?.typeName) ? null : ArchitectorTypeReference.Declared(reference.typeName, kind);
        }

        private static string DeclaredTypeName(ArchitectorTypeRecord type)
        {
            return string.IsNullOrEmpty(type?.typeNamespace) ? type?.name ?? string.Empty : $"{type.typeNamespace}.{type.name}";
        }

        private static string ResolveMutableReference(IDictionary<string, string> references, string value)
        {
            var reference = value?.Trim() ?? string.Empty;
            if (reference.Length == 0)
                throw new ArchitectorDataException("Semantic plan references cannot be empty.");
            return references.TryGetValue(reference, out var id) && id.Length > 0 ? id : reference;
        }

        private static string AddReference(IDictionary<string, string> references, string value, string category)
        {
            var reference = value?.Trim() ?? string.Empty;
            if (reference.Length == 0)
                throw new ArchitectorDataException($"Every planned {category} requires a non-empty reference.");
            if (references.ContainsKey(reference))
                throw new ArchitectorDataException($"Semantic plan reference '{reference}' is duplicated.");
            references.Add(reference, string.Empty);
            return reference;
        }

        private static string ResolveGroupReference(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, string> references,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            var id = ResolveReference(references, value);
            if (project.groups.All(group => group.id != id))
                id = ArchitectorEntryPaths.Resolve(project, value);
            if (project.groups.All(group => group.id != id))
                throw new ArchitectorDataException($"Group reference '{value}' does not exist or is declared after its child.");
            return id;
        }

        private static string ResolveEntryReference(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, string> references,
            string value)
        {
            var id = ResolveReference(references, value);
            if (project.groups.Any(group => group.id == id) || project.layers.Any(layer => layer.id == id))
                return id;
            var resolved = ArchitectorEntryPaths.Resolve(project, value);
            return resolved.Length > 0 ? resolved : id;
        }

        private static string ResolveLayerReference(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, string> references,
            string value)
        {
            var id = ResolveReference(references, value);
            if (project.layers.All(layer => layer.id != id))
                id = ArchitectorEntryPaths.Resolve(project, value);
            if (project.layers.All(layer => layer.id != id))
            {
                var matches = ArchitectorEntryPaths.MatchLayers(project, value);
                if (matches.Count > 1)
                    throw new ArchitectorDataException(
                        $"Layer reference '{value}' is ambiguous: {string.Join(", ", matches.Select(match => ArchitectorEntryPaths.Describe(project, match)))}. Use a longer path.");
                id = matches.Count == 1 ? matches[0] : id;
            }
            if (project.layers.All(layer => layer.id != id))
                throw new ArchitectorDataException($"Layer reference '{value}' does not exist.");
            return id;
        }

        private static string ResolveNodeReference(
            ArchitectorLayerDocument layer,
            IReadOnlyDictionary<string, string> references,
            string value)
        {
            var id = ResolveReference(references, value);
            if (layer.nodes.Any(node => node.id == id))
                return id;
            return ResolveNodeTitle(layer, value)
                   ?? throw new ArchitectorDataException($"Node reference '{value}' does not exist in the selected layer.");
        }

        // A node may be named instead of identified: "Node Title" or "Layer/Path::Node Title" (the layer part is
        // informative; the plan item already selects the layer). Portals are named after layers and are not matched.
        private static string ResolveNodeTitle(ArchitectorLayerDocument layer, string value)
        {
            var reference = value?.Trim() ?? string.Empty;
            var separator = reference.LastIndexOf("::", StringComparison.Ordinal);
            var title = separator < 0 ? reference : reference.Substring(separator + 2).Trim();
            if (title.Length == 0)
                return null;
            var matches = layer.nodes
                .Where(node => !IsPortal(node) && string.Equals(node.title, title, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1)
                throw new ArchitectorDataException(
                    $"Node name '{title}' is ambiguous in the selected layer: {string.Join(", ", matches.Select(node => node.id))}. Use the node id.");
            return matches.Length == 1 ? matches[0].id : null;
        }

        // "Layer/Path::Node Title" splits into its layer part (empty without "::") and the title.
        private static (string Layer, string Title) SplitNodeName(string value)
        {
            var reference = value?.Trim() ?? string.Empty;
            var separator = reference.LastIndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? (string.Empty, reference) : (reference.Substring(0, separator).Trim(), reference.Substring(separator + 2).Trim());
        }

        // An edge named by its two nodes: "Layer::Source -> Layer::Target". A side without a layer uses the deletion's
        // layer. The kind and label narrow the match; several matches fail with their ids.
        private string ResolveEdgeByNodes(
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> documents,
            IReadOnlyDictionary<string, string> references,
            ArchitectorPlanDeletion item)
        {
            var sides = item.target.Split(new[] { "->" }, StringSplitOptions.None);
            if (sides.Length != 2)
                throw new ArchitectorDataException($"Relationship reference '{item.target}' must be one id or 'Layer::Source -> Layer::Target'.");
            (string layerId, string nodeId) Side(string side)
            {
                var (layerPart, _) = SplitNodeName(side);
                var layer = string.IsNullOrWhiteSpace(layerPart) ? item.layer : layerPart;
                if (string.IsNullOrWhiteSpace(layer))
                    throw new ArchitectorDataException($"Relationship reference '{item.target}' needs a layer: 'Layer::Node' on each side, or the deletion's layer.");
                var layerId = ResolveLayerReference(project, references, layer);
                return (layerId, ResolveNodeReference(GetPlanDocuments(layerId, documents).semantic, references, side.Trim()));
            }
            var source = Side(sides[0]);
            var target = Side(sides[1]);
            var matches = project.layers
                .SelectMany(layer => GetPlanDocuments(layer.id, documents).semantic.edges)
                .Where(edge => edge.sourceLayerId == source.layerId && edge.sourceNodeId == source.nodeId
                               && edge.targetLayerId == target.layerId && edge.targetNodeId == target.nodeId)
                .Where(edge => string.IsNullOrWhiteSpace(item.relationship) || string.Equals(edge.kind, item.relationship.Trim(), StringComparison.OrdinalIgnoreCase))
                .Where(edge => string.IsNullOrWhiteSpace(item.label) || string.Equals(edge.label, item.label.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(edge => edge.id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (matches.Length == 0)
                throw new ArchitectorDataException($"No relationship matches '{item.target}'{(string.IsNullOrWhiteSpace(item.relationship) ? string.Empty : $" of kind '{item.relationship}'")}.");
            if (matches.Length > 1)
                throw new ArchitectorDataException(
                    $"Relationship reference '{item.target}' matches {matches.Length} relationships: {string.Join(", ", matches)}. Add 'relationship' (kind) or 'label', or use the id.");
            return matches[0];
        }

        private static string ResolveEdgeReference(
            ArchitectorLayerDocument layer,
            IReadOnlyDictionary<string, string> references,
            string value)
        {
            var id = ResolveReference(references, value);
            if (layer.edges.All(edge => edge.id != id))
                throw new ArchitectorDataException($"Relationship reference '{value}' does not exist in the selected layer.");
            return id;
        }

        private static string ResolveReference(IReadOnlyDictionary<string, string> references, string value)
        {
            var reference = value?.Trim() ?? string.Empty;
            if (reference.Length == 0)
                throw new ArchitectorDataException("Semantic plan references cannot be empty.");
            return references.TryGetValue(reference, out var id) && id.Length > 0 ? id : reference;
        }

        private sealed class SemanticPlanCandidate
        {
            public SemanticPlanCandidate(
                ArchitectorProjectDocument project,
                IReadOnlyDictionary<string, ArchitectorLayerDocuments> documents,
                IReadOnlyDictionary<string, string> references)
            {
                this.project = project;
                this.documents = documents;
                this.references = references;
            }

            public readonly ArchitectorProjectDocument project;
            public readonly IReadOnlyDictionary<string, ArchitectorLayerDocuments> documents;
            public readonly IReadOnlyDictionary<string, string> references;
        }
    }
}
