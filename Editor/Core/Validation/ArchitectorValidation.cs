using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public static class ArchitectorValidation
    {
        public const int CurrentSchemaVersion = 1;
        private static readonly HashSet<string> SupportedTypeKinds = new(StringComparer.Ordinal)
        {
            "class", "interface", "struct", "enum", "delegate", "record class", "record struct"
        };
        private static readonly HashSet<string> SupportedMemberKinds = new(StringComparer.Ordinal)
        {
            "field", "constant", "property", "indexer", "event", "constructor", "method", "operator"
        };
        private static readonly HashSet<string> SupportedEdgeKinds = new(ArchitectorEdgeKinds.All, StringComparer.Ordinal);

        public static void Validate(ArchitectorProjectDocument project)
        {
            if (project == null)
                throw new ArchitectorDataException("Project document is null.");
            if (project.schemaVersion != CurrentSchemaVersion)
                throw new ArchitectorDataException($"Unsupported project schema version {project.schemaVersion}.");
            if (string.IsNullOrWhiteSpace(project.projectId))
                throw new ArchitectorDataException("Project ID is required.");

            EnsureCollections(project);
            EnsureUniqueIds(project);
            EnsureUniqueNames(project);
            EnsureHierarchy(project);
        }

        public static void Validate(ArchitectorSettingsDocument settings)
        {
            if (settings == null)
                throw new ArchitectorDataException("Settings document is null.");
            if (settings.schemaVersion != CurrentSchemaVersion)
                throw new ArchitectorDataException($"Unsupported settings schema version {settings.schemaVersion}.");
            if (settings.sourceIndex == null || settings.geometry == null)
                throw new ArchitectorDataException("Settings sections are required.");
            settings.optimization ??= new ArchitectorOptimizationSettings();
            settings.palette ??= new ArchitectorPaletteSettings();
            settings.agent ??= new ArchitectorAgentSettings();
            settings.agent.integrations ??= new List<ArchitectorAgentIntegrationRecord>();
            settings.palette.dark ??= ArchitectorThemePaletteSettings.CreateDark();
            settings.palette.light ??= ArchitectorThemePaletteSettings.CreateLight();
            EnsureNodeHeader(settings.palette.dark, true);
            EnsureNodeHeader(settings.palette.light, false);
            EnsurePortalColors(settings.palette.dark, true);
            EnsurePortalColors(settings.palette.light, false);
            EnsureRelationshipColors(settings.palette.dark, true);
            EnsureRelationshipColors(settings.palette.light, false);
            if (settings.geometry.minimumNodeGap < 0f)
                throw new ArchitectorDataException("Minimum node gap cannot be negative.");
            if (settings.geometry.gridSize <= 0f)
                throw new ArchitectorDataException("Grid size must be positive.");
            if (settings.optimization.memberParentDepth is < -1 or > 2)
                throw new ArchitectorDataException("Member parent depth must be 0, 1, 2 or -1 for all parents.");
            var integrationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var integration in settings.agent.integrations)
            {
                if (integration == null || string.IsNullOrWhiteSpace(integration.id))
                    throw new ArchitectorDataException("Agent integration settings require an ID.");
                if (!integrationIds.Add(integration.id))
                    throw new ArchitectorDataException($"Agent integration '{integration.id}' is configured more than once.");
            }

            ValidateRepositoryPaths(settings.sourceIndex.includedRoots, "included source root");
            ValidateRepositoryPaths(settings.sourceIndex.excludedRoots, "excluded source root");
        }

        private static void EnsureNodeHeader(ArchitectorThemePaletteSettings palette, bool dark)
        {
            if (palette.nodeHeader != null && palette.nodeHeader.a >= 0f)
                return;
            palette.nodeHeader = dark
                ? new ArchitectorColorRecord(0.16f, 0.16f, 0.16f, 0.98f)
                : new ArchitectorColorRecord(0.74f, 0.74f, 0.74f, 0.98f);
        }

        private static void EnsureRelationshipColors(ArchitectorThemePaletteSettings palette, bool dark)
        {
            var defaults = dark
                ? ArchitectorThemePaletteSettings.CreateDark()
                : ArchitectorThemePaletteSettings.CreateLight();
            if (palette.dataFlowEdge == null || palette.dataFlowEdge.a < 0f)
                palette.dataFlowEdge = defaults.dataFlowEdge;
            if (palette.eventEdge == null || palette.eventEdge.a < 0f)
                palette.eventEdge = defaults.eventEdge;
            if (palette.callbackEdge == null || palette.callbackEdge.a < 0f)
                palette.callbackEdge = defaults.callbackEdge;
        }

        private static void EnsurePortalColors(ArchitectorThemePaletteSettings palette, bool dark)
        {
            var defaults = dark
                ? ArchitectorThemePaletteSettings.CreateDark()
                : ArchitectorThemePaletteSettings.CreateLight();
            if (palette.entryPortalOutline == null || palette.entryPortalOutline.a < 0f)
                palette.entryPortalOutline = defaults.entryPortalOutline;
            if (palette.exitPortalOutline == null || palette.exitPortalOutline.a < 0f)
                palette.exitPortalOutline = defaults.exitPortalOutline;
        }

        public static void Validate(ArchitectorLayerDocument layer, ArchitectorLayerLayoutDocument layout)
        {
            if (layer == null || layout == null)
                throw new ArchitectorDataException("Layer semantic and layout documents are required.");
            if (layer.schemaVersion != CurrentSchemaVersion || layout.schemaVersion != CurrentSchemaVersion)
                throw new ArchitectorDataException("Unsupported layer document schema version.");
            if (string.IsNullOrWhiteSpace(layer.layerId) || !string.Equals(layer.layerId, layout.layerId, StringComparison.Ordinal))
                throw new ArchitectorDataException("Layer semantic and layout IDs do not match.");
            if (layer.nodes == null || layer.edges == null || layout.nodes == null || layout.edges == null)
                throw new ArchitectorDataException("Layer collections cannot be null.");
            if ((layout.visibleRelationshipKinds & ~ArchitectorRelationshipVisibility.All) != 0)
                throw new ArchitectorDataException("Layer layout contains unsupported relationship visibility flags.");

            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            var nodeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manualTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var linkedTypeKeys = new HashSet<string>(StringComparer.Ordinal);
            var memberIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in layer.nodes)
            {
                if (node == null)
                    throw new ArchitectorDataException("Layer contains a null node.");
                AddUnique(nodeIds, node.id, "node ID");
                if (string.IsNullOrWhiteSpace(node.title))
                    throw new ArchitectorDataException($"Node '{node.id}' requires a title.");
                AddUnique(nodeTitles, node.title, "node title");
                node.responsibility ??= string.Empty;
                if (node.responsibility.IndexOfAny(new[] { '\r', '\n' }) >= 0
                    || node.responsibility.Length > ArchitectorNodeResponsibility.MaxLength)
                    throw new ArchitectorDataException(
                        $"Node '{node.title}' responsibility must be one line of at most {ArchitectorNodeResponsibility.MaxLength} characters.");
                if (node.responsibility.Length > 0
                    && string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Layer Portal '{node.title}' cannot have a responsibility.");
                node.members ??= new List<ArchitectorMemberRecord>();
                node.type ??= new ArchitectorTypeRecord();
                ValidateNode(node, manualTypeNames, linkedTypeKeys);
                var linkedMemberKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in node.members)
                {
                    if (member == null)
                        throw new ArchitectorDataException($"Node '{node.title}' contains a null contract member.");
                    AddUnique(memberIds, member.id, "contract member ID");
                    if (!SupportedMemberKinds.Contains(member.kind))
                        throw new ArchitectorDataException($"Unsupported contract member kind '{member.kind}'.");
                    if (string.IsNullOrWhiteSpace(member.declaration))
                        throw new ArchitectorDataException($"Contract member '{member.id}' requires a declaration.");
                    if (string.Equals(node.type.mode, "linked", StringComparison.Ordinal))
                    {
                        if (string.IsNullOrWhiteSpace(member.symbolKey))
                            throw new ArchitectorDataException($"Linked contract member '{member.id}' requires a symbol key.");
                        AddUnique(linkedMemberKeys, member.symbolKey, "linked contract member symbol key");
                    }
                }
            }

            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var connections = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in layer.edges)
            {
                if (edge == null)
                    throw new ArchitectorDataException("Layer contains a null edge.");
                AddUnique(edgeIds, edge.id, "edge ID");
                if (!string.Equals(edge.sourceLayerId, layer.layerId, StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Relationship '{edge.id}' must be stored in its source layer.");
                var source = layer.nodes.FirstOrDefault(node => node.id == edge.sourceNodeId);
                if (source == null)
                    throw new ArchitectorDataException($"Relationship '{edge.id}' references a missing source node.");
                if (string.Equals(source.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Relationship '{edge.id}' cannot use a Portal as its source.");
                if (string.IsNullOrWhiteSpace(edge.targetLayerId))
                    throw new ArchitectorDataException($"Relationship '{edge.id}' requires a target layer.");
                if (string.Equals(edge.targetLayerId, layer.layerId, StringComparison.Ordinal))
                {
                    var target = layer.nodes.FirstOrDefault(node => node.id == edge.targetNodeId);
                    if (target == null)
                        throw new ArchitectorDataException($"Relationship '{edge.id}' references a missing target node.");
                    if (string.Equals(target.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal))
                        throw new ArchitectorDataException($"Relationship '{edge.id}' cannot use a Portal as its target.");
                }
                if (edge.sourceLayerId == edge.targetLayerId && edge.sourceNodeId == edge.targetNodeId)
                    throw new ArchitectorDataException($"Edge '{edge.id}' cannot connect a node to itself.");
                if (!SupportedEdgeKinds.Contains(edge.kind))
                    throw new ArchitectorDataException($"Unsupported relationship kind '{edge.kind}'.");
                AddUnique(connections,
                    $"{edge.kind}:{edge.sourceLayerId}/{edge.sourceNodeId}->{edge.targetLayerId}/{edge.targetNodeId}",
                    "directed relationship");
            }

            var layoutNodeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nodeLayout in layout.nodes)
            {
                if (nodeLayout == null)
                    throw new ArchitectorDataException("Layer layout contains a null node.");
                AddUnique(layoutNodeIds, nodeLayout.nodeId, "node layout ID");
                if (!nodeIds.Contains(nodeLayout.nodeId))
                    throw new ArchitectorDataException($"Layout references missing node '{nodeLayout.nodeId}'.");
                if (nodeLayout.width <= 0f || nodeLayout.height <= 0f)
                    throw new ArchitectorDataException($"Node layout '{nodeLayout.nodeId}' requires positive size.");
            }

            if (!nodeIds.SetEquals(layoutNodeIds))
                throw new ArchitectorDataException("Every semantic node must have exactly one layout record.");

            var layoutEdgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edgeLayout in layout.edges)
            {
                if (edgeLayout == null)
                    throw new ArchitectorDataException("Layer layout contains a null edge.");
                AddUnique(layoutEdgeIds, edgeLayout.edgeId, "edge layout ID");
                if (!edgeIds.Contains(edgeLayout.edgeId))
                    throw new ArchitectorDataException($"Layout references missing edge '{edgeLayout.edgeId}'.");
                ValidateEdgeSide(edgeLayout.preferredSourceSide);
                ValidateEdgeSide(edgeLayout.preferredTargetSide);
            }
            if (!edgeIds.SetEquals(layoutEdgeIds))
                throw new ArchitectorDataException("Every semantic edge must have exactly one layout record.");
        }

        public static int ValidateRepository(
            ArchitectorProjectDocument project,
            ArchitectorSettingsDocument settings,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers)
        {
            Validate(project);
            Validate(settings);
            if (layers == null)
                throw new ArchitectorDataException("Repository layer documents are required.");

            var layerRecords = project.layers.ToDictionary(layer => layer.id, StringComparer.Ordinal);
            if (!layerRecords.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(layers.Keys))
                throw new ArchitectorDataException("Repository documents must contain exactly one document pair for every layer.");

            var documentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var portalsByPair = new Dictionary<string, List<(string layerId, ArchitectorNodeRecord node)>>(StringComparer.Ordinal);
            var nodesByEndpoint = new Dictionary<string, ArchitectorNodeRecord>(StringComparer.Ordinal);
            var relationships = new List<ArchitectorEdgeRecord>();
            foreach (var layerRecord in project.layers)
            {
                if (!documentPaths.Add(layerRecord.semanticPath) || !documentPaths.Add(layerRecord.layoutPath))
                    throw new ArchitectorDataException($"Layer '{layerRecord.name}' reuses another layer document path.");

                var documents = layers[layerRecord.id];
                Validate(documents.semantic, documents.layout);
                if (!string.Equals(documents.semantic.layerId, layerRecord.id, StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Layer '{layerRecord.name}' references documents owned by another layer.");

                foreach (var node in documents.semantic.nodes.Where(node =>
                             !string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal)))
                    nodesByEndpoint.Add(EndpointKey(layerRecord.id, node.id), node);
                relationships.AddRange(documents.semantic.edges);

                foreach (var node in documents.semantic.nodes.Where(node =>
                             string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal)))
                {
                    if (!portalsByPair.TryGetValue(node.portal.pairId, out var endpoints))
                    {
                        endpoints = new List<(string layerId, ArchitectorNodeRecord node)>();
                        portalsByPair.Add(node.portal.pairId, endpoints);
                    }
                    endpoints.Add((layerRecord.id, node));
                }
            }

            var portalLayerPairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in portalsByPair)
            {
                if (pair.Value.Count != 2)
                    throw new ArchitectorDataException($"Layer Portal pair '{pair.Key}' must contain exactly two endpoints.");

                var first = pair.Value[0];
                var second = pair.Value[1];
                if (!string.Equals(first.node.portal.targetLayerId, second.layerId, StringComparison.Ordinal)
                    || !string.Equals(second.node.portal.targetLayerId, first.layerId, StringComparison.Ordinal)
                    || !string.Equals(first.node.portal.counterpartNodeId, second.node.id, StringComparison.Ordinal)
                    || !string.Equals(second.node.portal.counterpartNodeId, first.node.id, StringComparison.Ordinal))
                {
                    throw new ArchitectorDataException($"Layer Portal pair '{pair.Key}' is not reciprocal.");
                }

                var rolesAreOpposite = string.Equals(first.node.portal.role, ArchitectorPortalRoles.Entry, StringComparison.Ordinal)
                                       && string.Equals(second.node.portal.role, ArchitectorPortalRoles.Exit, StringComparison.Ordinal)
                                       || string.Equals(first.node.portal.role, ArchitectorPortalRoles.Exit, StringComparison.Ordinal)
                                       && string.Equals(second.node.portal.role, ArchitectorPortalRoles.Entry, StringComparison.Ordinal);
                if (!rolesAreOpposite)
                    throw new ArchitectorDataException($"Layer Portal pair '{pair.Key}' must contain one Entry and one Exit endpoint.");
                if (!portalLayerPairs.Add(LayerPairKey(first.layerId, second.layerId)))
                    throw new ArchitectorDataException(
                        $"Layers '{first.layerId}' and '{second.layerId}' contain more than one Portal pair.");
            }

            var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
            var exactRelationships = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relationship in relationships)
            {
                AddUnique(relationshipIds, relationship.id, "relationship ID");
                if (!nodesByEndpoint.ContainsKey(EndpointKey(relationship.targetLayerId, relationship.targetNodeId)))
                    throw new ArchitectorDataException(
                        $"Relationship '{relationship.id}' references a missing target layer or node.");
                if (relationship.sourceLayerId != relationship.targetLayerId
                    && !portalLayerPairs.Contains(LayerPairKey(
                        relationship.sourceLayerId,
                        relationship.targetLayerId)))
                    throw new ArchitectorDataException(
                        $"Cross-layer relationship '{relationship.id}' requires the Portal pair between its endpoint layers.");
                AddUnique(exactRelationships,
                    $"{relationship.kind}:{EndpointKey(relationship.sourceLayerId, relationship.sourceNodeId)}->{EndpointKey(relationship.targetLayerId, relationship.targetNodeId)}",
                    "directed relationship");
            }
            EnsureRepositoryAcyclic(
                nodesByEndpoint.Keys,
                relationships.Where(relationship => string.Equals(
                    relationship.kind,
                    ArchitectorEdgeKinds.Dependency,
                    StringComparison.Ordinal)));

            return portalsByPair.Count;
        }

        private static void EnsureRepositoryAcyclic(
            IEnumerable<string> endpoints,
            IEnumerable<ArchitectorEdgeRecord> relationships)
        {
            var indegree = endpoints.ToDictionary(endpoint => endpoint, _ => 0, StringComparer.Ordinal);
            var outgoing = endpoints.ToDictionary(endpoint => endpoint, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var relationship in relationships)
            {
                var source = EndpointKey(relationship.sourceLayerId, relationship.sourceNodeId);
                var target = EndpointKey(relationship.targetLayerId, relationship.targetNodeId);
                indegree[target]++;
                outgoing[source].Add(target);
            }
            var ready = new Queue<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
            var visited = 0;
            while (ready.Count > 0)
            {
                var current = ready.Dequeue();
                visited++;
                foreach (var target in outgoing[current])
                {
                    if (--indegree[target] == 0)
                        ready.Enqueue(target);
                }
            }
            if (visited != indegree.Count)
                throw new ArchitectorDataException("Dependency relationships must form an acyclic project graph.");
        }

        private static string EndpointKey(string layerId, string nodeId) => $"{layerId}\u001f{nodeId}";

        private static string LayerPairKey(string firstLayerId, string secondLayerId)
        {
            return string.CompareOrdinal(firstLayerId, secondLayerId) <= 0
                ? $"{firstLayerId}\u001f{secondLayerId}"
                : $"{secondLayerId}\u001f{firstLayerId}";
        }

        private static void ValidateEdgeSide(string side)
        {
            if (side != "auto" && side != "left" && side != "right" && side != "top" && side != "bottom")
                throw new ArchitectorDataException($"Unsupported edge side '{side}'.");
        }

        private static void ValidateNode(
            ArchitectorNodeRecord node,
            HashSet<string> manualTypeNames,
            HashSet<string> linkedTypeKeys)
        {
            node.kind = string.IsNullOrEmpty(node.kind) ? ArchitectorNodeKinds.Standard : node.kind;
            if (string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal))
            {
                if (node.portal == null)
                    throw new ArchitectorDataException($"Layer Portal '{node.title}' requires Portal metadata.");
                if (!string.Equals(node.type.mode, "none", StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Layer Portal '{node.title}' cannot represent a type.");
                if (node.members.Count != 0)
                    throw new ArchitectorDataException($"Layer Portal '{node.title}' cannot contain contract members.");
                if (string.IsNullOrWhiteSpace(node.portal.pairId)
                    || string.IsNullOrWhiteSpace(node.portal.targetLayerId)
                    || string.IsNullOrWhiteSpace(node.portal.counterpartNodeId))
                    throw new ArchitectorDataException($"Layer Portal '{node.title}' requires a complete pair reference.");
                if (!string.Equals(node.portal.role, ArchitectorPortalRoles.Entry, StringComparison.Ordinal)
                    && !string.Equals(node.portal.role, ArchitectorPortalRoles.Exit, StringComparison.Ordinal))
                    throw new ArchitectorDataException(
                        $"Layer Portal '{node.title}' has unsupported role '{node.portal.role}'.");
                return;
            }
            if (!string.Equals(node.kind, ArchitectorNodeKinds.Standard, StringComparison.Ordinal))
                throw new ArchitectorDataException($"Node '{node.title}' has unsupported kind '{node.kind}'.");

            var mode = node.type.mode ?? string.Empty;
            if (mode == "none")
                return;
            if (mode != "declared" && mode != "linked")
                throw new ArchitectorDataException($"Node '{node.title}' has unsupported type mode '{mode}'.");
            if (!SupportedTypeKinds.Contains(node.type.kind))
                throw new ArchitectorDataException($"Node '{node.title}' has unsupported type kind '{node.type.kind}'.");
            if (string.IsNullOrWhiteSpace(node.type.name))
                throw new ArchitectorDataException($"Node '{node.title}' requires a type name.");

            node.type.genericParameters ??= new List<string>();
            node.type.modifiers ??= new List<string>();
            node.type.sourceDeclarations ??= new List<ArchitectorSourceDeclarationRecord>();
            node.type.interfaces ??= new List<ArchitectorTypeReferenceRecord>();
            if (IsImplicitEmptyTypeReference(node.type.baseType))
                node.type.baseType = null;
            ValidateTypeReference(node.type.baseType, node.title, "base type", false);
            var interfaceKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var interfaceType in node.type.interfaces)
            {
                if (interfaceType == null)
                    throw new ArchitectorDataException($"Node '{node.title}' contains a null interface fact.");
                ValidateTypeReference(interfaceType, node.title, "interface", true);
                AddUnique(interfaceKeys, ArchitectorTypeReference.Key(interfaceType), $"interface on node '{node.title}'");
            }
            var ownerKey = mode == "linked"
                ? ArchitectorTypeIdentity.Key(node.type.assemblyName, node.type.metadataName)
                : $"declared:{(string.IsNullOrEmpty(node.type.typeNamespace) ? node.type.name : $"{node.type.typeNamespace}.{node.type.name}")}";
            if (node.type.baseType != null && string.Equals(ownerKey, ArchitectorTypeReference.Key(node.type.baseType), StringComparison.Ordinal))
                throw new ArchitectorDataException($"Node '{node.title}' cannot use itself as its base type.");
            if (node.type.interfaces.Any(reference => string.Equals(ownerKey, ArchitectorTypeReference.Key(reference), StringComparison.Ordinal)))
                throw new ArchitectorDataException($"Node '{node.title}' cannot select itself as an interface fact.");
            node.type.interfaces = node.type.interfaces
                .OrderBy(ArchitectorTypeReference.Key, StringComparer.Ordinal)
                .ToList();
            if (mode == "declared")
                AddUnique(manualTypeNames, $"{node.type.typeNamespace}.{node.type.name}", "manual type full name");
            else
            {
                if (string.IsNullOrWhiteSpace(node.type.assemblyName) || string.IsNullOrWhiteSpace(node.type.metadataName))
                    throw new ArchitectorDataException($"Linked Type '{node.title}' requires assembly and metadata names.");
                AddUnique(
                    linkedTypeKeys,
                    ArchitectorTypeIdentity.Key(node.type.assemblyName, node.type.metadataName),
                    "linked type identity");
                foreach (var declaration in node.type.sourceDeclarations)
                {
                    if (declaration == null
                        || string.IsNullOrWhiteSpace(declaration.scriptGuid)
                        || string.IsNullOrWhiteSpace(declaration.repositoryPath))
                        throw new ArchitectorDataException($"Linked Type '{node.title}' contains an incomplete source declaration.");
                }
            }
        }

        private static void ValidateTypeReference(
            ArchitectorTypeReferenceRecord reference,
            string nodeTitle,
            string role,
            bool requireInterface)
        {
            if (reference == null)
                return;
            var mode = reference.mode ?? string.Empty;
            if (mode != "declared" && mode != "linked")
                throw new ArchitectorDataException($"Node '{nodeTitle}' has an unsupported {role} mode '{mode}'.");
            if (string.IsNullOrWhiteSpace(reference.name))
                throw new ArchitectorDataException($"Node '{nodeTitle}' contains an incomplete {role}.");
            if (!SupportedTypeKinds.Contains(reference.kind))
                throw new ArchitectorDataException($"Node '{nodeTitle}' {role} has unsupported kind '{reference.kind}'.");
            if (requireInterface && !string.Equals(reference.kind, "interface", StringComparison.Ordinal))
                throw new ArchitectorDataException($"Node '{nodeTitle}' {role} must have interface kind.");
            if (mode == "linked"
                && (string.IsNullOrWhiteSpace(reference.assemblyName) || string.IsNullOrWhiteSpace(reference.metadataName)))
                throw new ArchitectorDataException($"Node '{nodeTitle}' linked {role} requires assembly and metadata names.");
        }

        private static bool IsImplicitEmptyTypeReference(ArchitectorTypeReferenceRecord reference)
        {
            if (reference == null)
                return false;
            var mode = reference.mode ?? string.Empty;
            return (mode.Length == 0 || string.Equals(mode, "declared", StringComparison.Ordinal))
                   && string.IsNullOrWhiteSpace(reference.kind)
                   && string.IsNullOrWhiteSpace(reference.typeNamespace)
                   && string.IsNullOrWhiteSpace(reference.name)
                   && string.IsNullOrWhiteSpace(reference.assemblyName)
                   && string.IsNullOrWhiteSpace(reference.metadataName);
        }

        private static void EnsureCollections(ArchitectorProjectDocument project)
        {
            if (project.rootGroupIds == null || project.rootLayerIds == null || project.groups == null || project.layers == null)
                throw new ArchitectorDataException("Project collections cannot be null.");

            foreach (var group in project.groups)
            {
                if (group == null)
                    throw new ArchitectorDataException("Project contains a null group.");
                group.childGroupIds ??= new List<string>();
                group.layerIds ??= new List<string>();
                group.lockState ??= new ArchitectorGroupLockRecord();
                group.lockState.passwordHash ??= string.Empty;
                group.lockState.notice ??= string.Empty;
                if (group.lockState.locked && string.IsNullOrWhiteSpace(group.lockState.passwordHash))
                    throw new ArchitectorDataException($"Locked group '{group.name}' requires a password hash.");
            }
            foreach (var layer in project.layers)
            {
                if (layer == null)
                    throw new ArchitectorDataException("Project contains a null layer.");
                layer.approval ??= new ArchitectorLayerApprovalRecord();
                layer.approval.semanticHash ??= string.Empty;
                layer.scope ??= new ArchitectorLayerScopeRecord();
                layer.scope.folders ??= new List<string>();
                layer.scope.namespaces ??= new List<string>();
                ValidateRepositoryPaths(layer.scope.folders, $"code scope folder of layer '{layer.name}'");
                var namespaces = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in layer.scope.namespaces)
                {
                    if (string.IsNullOrWhiteSpace(name)
                        || name.Split('.').Any(part => part.Length == 0 || !(char.IsLetter(part[0]) || part[0] == '_') || !part.All(character => char.IsLetterOrDigit(character) || character == '_')))
                        throw new ArchitectorDataException($"Layer '{layer.name}' has invalid code scope namespace '{name}'.");
                    AddUnique(namespaces, name, $"code scope namespace of layer '{layer.name}'");
                }
            }
        }

        private static void EnsureUniqueIds(ArchitectorProjectDocument project)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in project.groups)
                AddUnique(ids, group.id, "group ID");
            foreach (var layer in project.layers)
                AddUnique(ids, layer.id, "layer ID");
        }

        private static void EnsureUniqueNames(ArchitectorProjectDocument project)
        {
            var groupNames = project.groups.ToDictionary(group => group.id, group => group.name, StringComparer.Ordinal);
            var namesByParent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var entries = project.groups.Select(group => (group.parentGroupId, group.name))
                .Concat(project.layers.Select(layer => (layer.parentGroupId, layer.name)));
            foreach (var (parentGroupId, name) in entries)
            {
                var parent = parentGroupId ?? string.Empty;
                if (!namesByParent.TryGetValue(parent, out var names))
                {
                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    namesByParent.Add(parent, names);
                }
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArchitectorDataException("Empty group or layer name is not allowed.");
                if (!names.Add(name))
                {
                    var location = parent.Length == 0
                        ? "the project root"
                        : $"group '{(groupNames.TryGetValue(parent, out var parentName) ? parentName : parent)}'";
                    throw new ArchitectorDataException($"Duplicate group or layer name '{name}' in {location}.");
                }
            }
        }

        private static void EnsureHierarchy(ArchitectorProjectDocument project)
        {
            var groups = project.groups.ToDictionary(group => group.id, StringComparer.Ordinal);
            var layers = project.layers.ToDictionary(layer => layer.id, StringComparer.Ordinal);
            var placedGroups = new HashSet<string>(StringComparer.Ordinal);
            var placedLayers = new HashSet<string>(StringComparer.Ordinal);

            foreach (var groupId in project.rootGroupIds)
            {
                var group = GetRequired(groups, groupId, "root group");
                if (!string.IsNullOrEmpty(group.parentGroupId))
                    throw new ArchitectorDataException($"Root group '{group.name}' has a parent.");
                AddUnique(placedGroups, groupId, "group placement");
            }

            foreach (var layerId in project.rootLayerIds)
            {
                var layer = GetRequired(layers, layerId, "root layer");
                if (!string.IsNullOrEmpty(layer.parentGroupId))
                    throw new ArchitectorDataException($"Root layer '{layer.name}' has a parent.");
                AddUnique(placedLayers, layerId, "layer placement");
            }

            foreach (var parent in project.groups)
            {
                foreach (var childId in parent.childGroupIds)
                {
                    var child = GetRequired(groups, childId, $"child group of '{parent.name}'");
                    if (!string.Equals(child.parentGroupId, parent.id, StringComparison.Ordinal))
                        throw new ArchitectorDataException($"Group '{child.name}' has inconsistent parent data.");
                    AddUnique(placedGroups, childId, "group placement");
                }

                foreach (var layerId in parent.layerIds)
                {
                    var layer = GetRequired(layers, layerId, $"layer of '{parent.name}'");
                    if (!string.Equals(layer.parentGroupId, parent.id, StringComparison.Ordinal))
                        throw new ArchitectorDataException($"Layer '{layer.name}' has inconsistent parent data.");
                    AddUnique(placedLayers, layerId, "layer placement");
                }
            }

            if (placedGroups.Count != groups.Count)
                throw new ArchitectorDataException("One or more groups are not placed in the hierarchy.");
            if (placedLayers.Count != layers.Count)
                throw new ArchitectorDataException("One or more layers are not placed in the hierarchy.");

            foreach (var group in project.groups)
                EnsureNoParentCycle(group, groups);
        }

        private static void EnsureNoParentCycle(ArchitectorGroupRecord start, IReadOnlyDictionary<string, ArchitectorGroupRecord> groups)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = start;
            while (!string.IsNullOrEmpty(current.parentGroupId))
            {
                if (!visited.Add(current.id))
                    throw new ArchitectorDataException($"Group hierarchy cycle contains '{start.name}'.");
                current = GetRequired(groups, current.parentGroupId, $"parent of '{current.name}'");
            }
        }

        private static void ValidateRepositoryPaths(IEnumerable<string> paths, string description)
        {
            if (paths == null)
                throw new ArchitectorDataException($"The {description} list cannot be null.");

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path) || path.Contains(".."))
                    throw new ArchitectorDataException($"Invalid {description} '{path}'.");
                AddUnique(unique, path.Replace('\\', '/'), description);
            }
        }

        private static T GetRequired<T>(IReadOnlyDictionary<string, T> records, string id, string description)
        {
            if (string.IsNullOrWhiteSpace(id) || !records.TryGetValue(id, out var record))
                throw new ArchitectorDataException($"Missing {description} '{id}'.");
            return record;
        }

        private static void AddUnique(HashSet<string> values, string value, string description)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArchitectorDataException($"Empty {description} is not allowed.");
            if (!values.Add(value))
                throw new ArchitectorDataException($"Duplicate {description} '{value}'.");
        }
    }

    public sealed class ArchitectorDataException : Exception
    {
        public ArchitectorDataException(string message) : base(message)
        {
        }
    }
}
