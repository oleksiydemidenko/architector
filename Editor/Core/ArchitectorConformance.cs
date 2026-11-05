using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Architector.Editor
{
    public static class ArchitectorConformanceStates
    {
        public const string Conformant = "Conformant";
        public const string Incomplete = "Incomplete";
        public const string Conflicts = "Conflicts";
    }

    public static class ArchitectorConformanceSeverities
    {
        public const string Warning = "warning";
        public const string Error = "error";
    }

    public static class ArchitectorConformanceCodes
    {
        public const string NoLinkedContracts = "no_linked_contracts";
        public const string LayerNotApproved = "layer_not_approved";
        public const string TypeOutOfScope = "type_out_of_scope";
        public const string TypeUnresolved = "type_unresolved";
        public const string TypeKindChanged = "type_kind_changed";
        public const string BaseTypeMismatch = "base_type_mismatch";
        public const string InterfaceMissing = "interface_missing";
        public const string MemberMissing = "member_missing";
        public const string MemberSignatureChanged = "member_signature_changed";
        public const string UnanchoredNode = "unanchored_node";
        public const string DependencyCycle = "dependency_cycle";
        public const string LayersUnchecked = "layers_unchecked";
        public const string LinkedToUncheckedLayer = "linked_to_unchecked_layer";
    }

    public sealed class ArchitectorConformanceDiagnostic
    {
        public string Severity { get; internal set; } = string.Empty;
        public string Code { get; internal set; } = string.Empty;
        public string LayerId { get; internal set; } = string.Empty;
        public string LayerName { get; internal set; } = string.Empty;
        public string NodeId { get; internal set; } = string.Empty;
        public string NodeTitle { get; internal set; } = string.Empty;
        public string MemberId { get; internal set; } = string.Empty;
        public string Expected { get; internal set; } = string.Empty;
        public string Observed { get; internal set; } = string.Empty;
        public string Message { get; internal set; } = string.Empty;
    }

    public sealed class ArchitectorConformanceReport
    {
        internal readonly List<ArchitectorConformanceDiagnostic> diagnostics = new();

        public string State => ErrorCount > 0
            ? ArchitectorConformanceStates.Conflicts
            : WarningCount > 0
                ? ArchitectorConformanceStates.Incomplete
                : ArchitectorConformanceStates.Conformant;
        public int LayerCount { get; internal set; }
        public int UncheckedLayerCount { get; internal set; }
        public int ApprovedLayerCount { get; internal set; }
        public int UnapprovedLayerCount { get; internal set; }
        public int LinkedTypeCount { get; internal set; }
        public int SelectedMemberCount { get; internal set; }
        public int SelectedTypeFactCount { get; internal set; }
        public int PassedCount { get; internal set; }
        public int WarningCount { get; internal set; }
        public int ErrorCount { get; internal set; }
        public int UnverifiedMemberCount { get; internal set; }
        public int UnverifiedTypeFactCount { get; internal set; }
        public int TotalDiagnosticCount { get; internal set; }
        public IReadOnlyList<ArchitectorConformanceDiagnostic> Diagnostics => diagnostics;
        public bool DiagnosticsTruncated => diagnostics.Count < TotalDiagnosticCount;
    }

    public readonly struct ArchitectorUnanchoredNode
    {
        public ArchitectorUnanchoredNode(ArchitectorLayerRecord layer, ArchitectorNodeRecord node)
        {
            Layer = layer;
            Node = node;
        }

        public ArchitectorLayerRecord Layer { get; }
        public ArchitectorNodeRecord Node { get; }
    }

    public static class ArchitectorConformance
    {
        public static ArchitectorConformanceReport Evaluate(
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            IEnumerable<string> layerIds,
            int diagnosticLimit = 100)
        {
            if (repository == null)
                throw new ArgumentNullException(nameof(repository));
            if (typeIndex == null)
                throw new ArgumentNullException(nameof(typeIndex));
            if (layerIds == null)
                throw new ArgumentNullException(nameof(layerIds));

            var requested = layerIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
            var uncheckedLayers = repository.UncheckedLayerIds();
            var ids = requested.Where(id => !uncheckedLayers.Contains(id)).ToArray();
            var skipped = requested.Where(uncheckedLayers.Contains).ToArray();
            var report = new ArchitectorConformanceReport { LayerCount = ids.Length, UncheckedLayerCount = skipped.Length };
            var limit = Math.Max(0, diagnosticLimit);
            if (skipped.Length > 0)
            {
                var names = skipped.Select(id => repository.FindLayer(id)?.name ?? id).ToArray();
                report.WarningCount++;
                AddDiagnostic(report, limit, new ArchitectorConformanceDiagnostic
                {
                    Severity = ArchitectorConformanceSeverities.Warning,
                    Code = ArchitectorConformanceCodes.LayersUnchecked,
                    Expected = "Checked layers",
                    Observed = $"{skipped.Length} skipped",
                    Message = $"{skipped.Length} layer(s) in groups with checks off were skipped: {string.Join(", ", names.Take(5))}{(names.Length > 5 ? ", …" : string.Empty)}."
                });
            }
            var documents = repository.Project.layers.ToDictionary(
                layer => layer.id,
                layer => repository.PeekLayer(layer.id),
                StringComparer.Ordinal);
            var unanchoredByLayer = FindUnanchoredLinkedNodes(repository.Project, documents, ids)
                .GroupBy(item => item.Layer.id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var cycles = repository.FindDependencyCycles();
            foreach (var layerId in ids)
            {
                var layer = repository.FindLayer(layerId)
                            ?? throw new ArchitectorDataException($"Layer '{layerId}' does not exist.");
                var approval = repository.GetLayerApprovalStatus(layer.id);
                if (string.Equals(approval.State, ArchitectorApprovalStates.Approved, StringComparison.Ordinal))
                    report.ApprovedLayerCount++;
                else
                {
                    report.UnapprovedLayerCount++;
                    report.WarningCount++;
                    AddDiagnostic(report, limit, new ArchitectorConformanceDiagnostic
                    {
                        Severity = ArchitectorConformanceSeverities.Warning,
                        Code = ArchitectorConformanceCodes.LayerNotApproved,
                        LayerId = layer.id,
                        LayerName = layer.name,
                        Expected = ArchitectorApprovalStates.Approved,
                        Observed = approval.State,
                        Message = $"Layer '{layer.name}' is {approval.State}; conformance cannot be claimed until it is approved."
                    });
                }

                var semantic = documents[layer.id].semantic;
                if (unanchoredByLayer.TryGetValue(layer.id, out var unanchoredNodes))
                {
                    foreach (var unanchored in unanchoredNodes)
                    {
                        report.ErrorCount++;
                        AddDiagnostic(report, limit, Diagnostic(
                            ArchitectorConformanceSeverities.Error,
                            ArchitectorConformanceCodes.UnanchoredNode,
                            layer,
                            unanchored.Node,
                            null,
                            "At least one relationship",
                            "No relationship",
                            $"Linked node '{unanchored.Node.title}' has no relationship."));
                    }
                }
                foreach (var cycle in cycles.Where(cycle => cycle.LayerIds.Contains(layer.id)))
                {
                    var member = cycle.Members.First(endpoint => string.Equals(endpoint.LayerId, layer.id, StringComparison.Ordinal));
                    report.WarningCount++;
                    AddDiagnostic(report, limit, Diagnostic(
                        ArchitectorConformanceSeverities.Warning,
                        ArchitectorConformanceCodes.DependencyCycle,
                        layer,
                        semantic.nodes.First(node => string.Equals(node.id, member.NodeId, StringComparison.Ordinal)),
                        null,
                        "Acyclic dependencies",
                        $"Cycle of {cycle.Members.Count} nodes in {cycle.LayerIds.Count} layers",
                        $"Dependency cycle: {repository.DescribeDependencyCycle(cycle)}."));
                }
                foreach (var other in ArchitectorUncheckedGroups.LinksToUnchecked(layer.id, documents.Values, uncheckedLayers))
                {
                    var otherName = repository.FindLayer(other.Key)?.name ?? other.Key;
                    report.ErrorCount++;
                    AddDiagnostic(report, limit, new ArchitectorConformanceDiagnostic
                    {
                        Severity = ArchitectorConformanceSeverities.Error,
                        Code = ArchitectorConformanceCodes.LinkedToUncheckedLayer,
                        LayerId = layer.id,
                        LayerName = layer.name,
                        Expected = "No relationships with layers whose checks are off",
                        Observed = $"{other.Count()} with '{otherName}'",
                        Message = $"Layer '{layer.name}' has {other.Count()} relationship(s) with '{otherName}', where checks are off. Remove them, or move the layer out of that group and turn its checks on."
                    });
                }
                MergeLinked(report, limit, LinkedResult(layer, semantic, typeIndex));
            }
            if (report.LinkedTypeCount == 0 && ids.Length > 0)
            {
                report.WarningCount++;
                AddDiagnostic(report, limit, new ArchitectorConformanceDiagnostic
                {
                    Severity = ArchitectorConformanceSeverities.Warning,
                    Code = ArchitectorConformanceCodes.NoLinkedContracts,
                    Expected = "At least one Linked Type contract",
                    Observed = "None",
                    Message = "The selected scope has no Linked Type contracts to verify."
                });
            }
            return report;
        }

        public static IReadOnlyList<ArchitectorUnanchoredNode> FindUnanchoredLinkedNodes(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            IEnumerable<string> layerIds)
        {
            var selectedLayerIds = new HashSet<string>(layerIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var connectedEndpoints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in layers.Values.SelectMany(documents => documents.semantic.edges))
            {
                connectedEndpoints.Add(EndpointKey(edge.sourceLayerId, edge.sourceNodeId));
                connectedEndpoints.Add(EndpointKey(edge.targetLayerId, edge.targetNodeId));
            }

            var result = new List<ArchitectorUnanchoredNode>();
            foreach (var layer in project.layers.Where(candidate => selectedLayerIds.Contains(candidate.id)))
            {
                foreach (var node in layers[layer.id].semantic.nodes)
                {
                    if (!string.Equals(node.kind, ArchitectorNodeKinds.Standard, StringComparison.Ordinal)
                        || !string.Equals(node.type?.mode, "linked", StringComparison.Ordinal)
                        || connectedEndpoints.Contains(EndpointKey(layer.id, node.id)))
                        continue;
                    result.Add(new ArchitectorUnanchoredNode(layer, node));
                }
            }
            return result;
        }

        private static string EndpointKey(string layerId, string nodeId) => $"{layerId}\u001f{nodeId}";

        private static void EvaluateLinkedNode(
            ArchitectorConformanceReport report,
            int diagnosticLimit,
            ArchitectorLayerRecord layer,
            ArchitectorNodeRecord node,
            ArchitectorTypeIndex typeIndex)
        {
            report.LinkedTypeCount++;
            report.SelectedMemberCount += node.members?.Count ?? 0;
            var selectedFactCount = (node.type?.baseType == null ? 0 : 1) + (node.type?.interfaces?.Count ?? 0);
            report.SelectedTypeFactCount += selectedFactCount;
            var resolution = typeIndex.Resolve(node.type);
            if (resolution == ArchitectorTypeResolution.OutOfScope)
            {
                report.WarningCount++;
                report.UnverifiedMemberCount += node.members?.Count ?? 0;
                report.UnverifiedTypeFactCount += selectedFactCount;
                AddDiagnostic(report, diagnosticLimit, Diagnostic(
                    ArchitectorConformanceSeverities.Warning,
                    ArchitectorConformanceCodes.TypeOutOfScope,
                    layer,
                    node,
                    null,
                    ArchitectorTypeIdentity.Key(node.type.assemblyName, node.type.metadataName),
                    "OutOfScope",
                    $"Linked type '{node.title}' is outside the configured source index."));
                return;
            }
            if (resolution != ArchitectorTypeResolution.Resolved)
            {
                report.ErrorCount++;
                report.UnverifiedMemberCount += node.members?.Count ?? 0;
                report.UnverifiedTypeFactCount += selectedFactCount;
                AddDiagnostic(report, diagnosticLimit, Diagnostic(
                    ArchitectorConformanceSeverities.Error,
                    ArchitectorConformanceCodes.TypeUnresolved,
                    layer,
                    node,
                    null,
                    ArchitectorTypeIdentity.Key(node.type.assemblyName, node.type.metadataName),
                    "Missing",
                    $"Linked type '{node.title}' could not be resolved."));
                return;
            }

            var indexedType = typeIndex.Find(node.type);
            if (indexedType == null || !string.Equals(indexedType.Kind, node.type.kind, StringComparison.Ordinal))
            {
                report.ErrorCount++;
                AddDiagnostic(report, diagnosticLimit, Diagnostic(
                    ArchitectorConformanceSeverities.Error,
                    ArchitectorConformanceCodes.TypeKindChanged,
                    layer,
                    node,
                    null,
                    node.type.kind,
                    indexedType?.Kind ?? "Missing",
                    $"Linked type '{node.title}' no longer has the expected kind."));
            }
            else
            {
                report.PassedCount++;
            }

            if (node.type.baseType != null)
            {
                if (typeIndex.HasDirectBase(node.type, node.type.baseType))
                {
                    report.PassedCount++;
                }
                else
                {
                    report.ErrorCount++;
                    var actualBase = typeIndex.GetDirectBase(node.type);
                    AddDiagnostic(report, diagnosticLimit, Diagnostic(
                        ArchitectorConformanceSeverities.Error,
                        ArchitectorConformanceCodes.BaseTypeMismatch,
                        layer,
                        node,
                        null,
                        ArchitectorTypeReference.Key(node.type.baseType),
                        actualBase?.Key ?? "None",
                        $"Linked type '{node.title}' no longer has the expected direct base type."));
                }
            }

            foreach (var expectedInterface in node.type.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
            {
                if (typeIndex.ImplementsInterface(node.type, expectedInterface))
                {
                    report.PassedCount++;
                    continue;
                }
                report.ErrorCount++;
                AddDiagnostic(report, diagnosticLimit, Diagnostic(
                    ArchitectorConformanceSeverities.Error,
                    ArchitectorConformanceCodes.InterfaceMissing,
                    layer,
                    node,
                    null,
                    ArchitectorTypeReference.Key(expectedInterface),
                    "Missing",
                    $"Linked type '{node.title}' no longer implements expected interface '{ArchitectorTypeReference.FullName(expectedInterface)}'."));
            }

            foreach (var member in node.members)
            {
                var memberResolution = typeIndex.ResolveMember(node.type, member);
                if (memberResolution == ArchitectorMemberResolution.Resolved)
                {
                    report.PassedCount++;
                    continue;
                }
                report.ErrorCount++;
                var signatureChanged = memberResolution == ArchitectorMemberResolution.SignatureChanged;
                AddDiagnostic(report, diagnosticLimit, Diagnostic(
                    ArchitectorConformanceSeverities.Error,
                    signatureChanged
                        ? ArchitectorConformanceCodes.MemberSignatureChanged
                        : ArchitectorConformanceCodes.MemberMissing,
                    layer,
                    node,
                    member,
                    member.declaration,
                    signatureChanged ? "SignatureChanged" : "Missing",
                    signatureChanged
                        ? $"Selected member on '{node.title}' has a different signature."
                        : $"Selected member on '{node.title}' could not be resolved."));
            }
        }

        private static ArchitectorConformanceDiagnostic Diagnostic(
            string severity,
            string code,
            ArchitectorLayerRecord layer,
            ArchitectorNodeRecord node,
            ArchitectorMemberRecord member,
            string expected,
            string observed,
            string message)
        {
            return new ArchitectorConformanceDiagnostic
            {
                Severity = severity,
                Code = code,
                LayerId = layer.id,
                LayerName = layer.name,
                NodeId = node.id,
                NodeTitle = node.title,
                MemberId = member?.id ?? string.Empty,
                Expected = expected ?? string.Empty,
                Observed = observed ?? string.Empty,
                Message = message
            };
        }

        private sealed class LinkedCacheEntry
        {
            public ArchitectorTypeIndex TypeIndex;
            public int Generation;
            public string LayerName;
            public ArchitectorConformanceReport Report;
        }

        private static readonly ConditionalWeakTable<ArchitectorLayerDocument, LinkedCacheEntry> LinkedCache = new();

        private static ArchitectorConformanceReport LinkedResult(ArchitectorLayerRecord layer, ArchitectorLayerDocument semantic, ArchitectorTypeIndex typeIndex)
        {
            if (LinkedCache.TryGetValue(semantic, out var cached)
                && ReferenceEquals(cached.TypeIndex, typeIndex)
                && cached.Generation == typeIndex.Generation
                && cached.LayerName == layer.name)
                return cached.Report;
            var partial = new ArchitectorConformanceReport();
            foreach (var node in semantic.nodes.Where(node => string.Equals(node.type?.mode, "linked", StringComparison.Ordinal)))
                EvaluateLinkedNode(partial, int.MaxValue, layer, node, typeIndex);
            LinkedCache.Remove(semantic);
            LinkedCache.Add(semantic, new LinkedCacheEntry { TypeIndex = typeIndex, Generation = typeIndex.Generation, LayerName = layer.name, Report = partial });
            return partial;
        }

        private static void MergeLinked(ArchitectorConformanceReport report, int limit, ArchitectorConformanceReport partial)
        {
            report.LinkedTypeCount += partial.LinkedTypeCount;
            report.SelectedMemberCount += partial.SelectedMemberCount;
            report.SelectedTypeFactCount += partial.SelectedTypeFactCount;
            report.PassedCount += partial.PassedCount;
            report.WarningCount += partial.WarningCount;
            report.ErrorCount += partial.ErrorCount;
            report.UnverifiedMemberCount += partial.UnverifiedMemberCount;
            report.UnverifiedTypeFactCount += partial.UnverifiedTypeFactCount;
            foreach (var diagnostic in partial.Diagnostics)
                AddDiagnostic(report, limit, diagnostic);
        }

        private static void AddDiagnostic(
            ArchitectorConformanceReport report,
            int limit,
            ArchitectorConformanceDiagnostic diagnostic)
        {
            report.TotalDiagnosticCount++;
            if (report.diagnostics.Count < limit)
                report.diagnostics.Add(diagnostic);
        }
    }
}
