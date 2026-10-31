using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    [Serializable]
    public sealed class ArchitectorStoredProjectDocument
    {
        public int schemaVersion = ArchitectorStorageCodec.SchemaVersion;
        public string projectId = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorStoredGroupDocument
    {
        public int schemaVersion = ArchitectorStorageCodec.SchemaVersion;
        public string id = string.Empty;
        public string name = string.Empty;
        public string parentGroupId = string.Empty;
        public string orderKey = string.Empty;
        public ArchitectorGroupLockRecord lockState = new();
    }

    [Serializable]
    public sealed class ArchitectorStoredLayerDocument
    {
        public int schemaVersion = ArchitectorStorageCodec.SchemaVersion;
        public string layerId = string.Empty;
        public string name = string.Empty;
        public string purpose = string.Empty;
        public string parentGroupId = string.Empty;
        public string orderKey = string.Empty;
        public ArchitectorLayerScopeRecord scope = new();
        public List<ArchitectorNodeRecord> nodes = new();
        public List<ArchitectorEdgeRecord> edges = new();
    }

    [Serializable]
    public sealed class ArchitectorStoredLayoutDocument
    {
        public int schemaVersion = ArchitectorStorageCodec.SchemaVersion;
        public string layerId = string.Empty;
        public List<ArchitectorNodeLayoutRecord> nodes = new();
        public List<ArchitectorEdgeLayoutRecord> edges = new();
        public int visibleRelationshipKinds = ArchitectorRelationshipVisibility.All;
    }

    [Serializable]
    public sealed class ArchitectorStoredBaselineDocument
    {
        public int schemaVersion = ArchitectorStorageCodec.SchemaVersion;
        public string layerId = string.Empty;
        public string semanticHash = string.Empty;
        public ArchitectorLayerBaselineRecord baseline = new();
    }

    [Serializable]
    public sealed class ArchitectorStoredLinkDocument
    {
        public int schemaVersion = ArchitectorStorageCodec.SchemaVersion;
        public string pairId = string.Empty;
        public List<ArchitectorStoredPortalEndpoint> endpoints = new();
        public List<ArchitectorEdgeRecord> relationships = new();
        public List<ArchitectorEdgeLayoutRecord> relationshipLayouts = new();
    }

    [Serializable]
    public sealed class ArchitectorStoredPortalEndpoint
    {
        public string layerId = string.Empty;
        public string nodeId = string.Empty;
        public string title = string.Empty;
        public string role = string.Empty;
        public float x;
        public float y;
        public float width = 280f;
        public float height = 120f;
    }

    public sealed class ArchitectorDecodedRepository
    {
        public ArchitectorDecodedRepository(
            ArchitectorProjectDocument project,
            Dictionary<string, ArchitectorLayerDocuments> layers,
            Dictionary<string, string> layerErrors)
        {
            Project = project;
            Layers = layers;
            LayerErrors = layerErrors;
        }

        public ArchitectorProjectDocument Project { get; }
        public Dictionary<string, ArchitectorLayerDocuments> Layers { get; }
        public Dictionary<string, string> LayerErrors { get; }
    }

    public static class ArchitectorStorageCodec
    {
        public const int SchemaVersion = 2;

        public static bool IsCanonicalPath(string relativePath)
        {
            return string.Equals(relativePath, ArchitectorPaths.ProjectFileName, StringComparison.Ordinal)
                   || string.Equals(relativePath, ArchitectorPaths.SettingsFileName, StringComparison.Ordinal)
                   || TryGetEntryId(relativePath, ArchitectorPaths.GroupsDirectoryName, ArchitectorPaths.GroupFileSuffix, out _)
                   || TryGetEntryId(relativePath, ArchitectorPaths.LayersDirectoryName, ArchitectorPaths.LayerFileSuffix, out _)
                   || TryGetEntryId(relativePath, ArchitectorPaths.LayoutsDirectoryName, ArchitectorPaths.LayoutFileSuffix, out _)
                   || TryGetEntryId(relativePath, ArchitectorPaths.LinksDirectoryName, ArchitectorPaths.LinkFileSuffix, out _)
                   || TryGetEntryId(relativePath, ArchitectorPaths.BaselinesDirectoryName, ArchitectorPaths.BaselineFileSuffix, out _);
        }

        public static ArchitectorDecodedRepository Decode(IReadOnlyDictionary<string, string> files)
        {
            if (!files.TryGetValue(ArchitectorPaths.ProjectFileName, out var projectJson) || projectJson == null)
                throw new ArchitectorDataException($"Project document '{ArchitectorPaths.ProjectFileName}' is missing.");
            var storedProject = ArchitectorJson.Deserialize<ArchitectorStoredProjectDocument>(
                projectJson,
                ArchitectorPaths.ProjectFileName);
            RequireSchema(storedProject.schemaVersion, ArchitectorPaths.ProjectFileName);
            var project = new ArchitectorProjectDocument { projectId = storedProject.projectId ?? string.Empty };

            foreach (var path in EntryPaths(files, ArchitectorPaths.GroupsDirectoryName, ArchitectorPaths.GroupFileSuffix))
            {
                var stored = ArchitectorJson.Deserialize<ArchitectorStoredGroupDocument>(files[path.path], path.path);
                RequireSchema(stored.schemaVersion, path.path);
                if (!string.Equals(stored.id, path.id, StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Group document '{path.path}' declares ID '{stored.id}'.");
                project.groups.Add(new ArchitectorGroupRecord
                {
                    id = stored.id,
                    name = stored.name ?? string.Empty,
                    parentGroupId = stored.parentGroupId ?? string.Empty,
                    orderKey = stored.orderKey ?? string.Empty,
                    lockState = stored.lockState ?? new ArchitectorGroupLockRecord()
                });
            }

            var layers = new Dictionary<string, ArchitectorLayerDocuments>(StringComparer.Ordinal);
            var layerErrors = new Dictionary<string, string>(StringComparer.Ordinal);
            var layoutPaths = EntryPaths(files, ArchitectorPaths.LayoutsDirectoryName, ArchitectorPaths.LayoutFileSuffix)
                .ToDictionary(path => path.id, path => path.path, StringComparer.Ordinal);
            foreach (var path in EntryPaths(files, ArchitectorPaths.LayersDirectoryName, ArchitectorPaths.LayerFileSuffix))
            {
                var layoutPath = layoutPaths.TryGetValue(path.id, out var foundLayout) ? foundLayout : null;
                layoutPaths.Remove(path.id);
                DecodeLayer(files, path.id, path.path, layoutPath, project, layers, layerErrors);
            }
            foreach (var orphanLayout in layoutPaths.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                throw new ArchitectorDataException(
                    $"Layout document '{orphanLayout.Value}' has no layer document for '{orphanLayout.Key}'.");
            foreach (var path in EntryPaths(files, ArchitectorPaths.BaselinesDirectoryName, ArchitectorPaths.BaselineFileSuffix))
                DecodeBaseline(files, path.path, path.id, project, layers, layerErrors);

            var pairsByLayers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in EntryPaths(files, ArchitectorPaths.LinksDirectoryName, ArchitectorPaths.LinkFileSuffix))
            {
                var layerPair = DecodeLink(files, path.path, path.id, project, layers, layerErrors);
                if (!pairsByLayers.TryGetValue(layerPair, out var pairIds))
                {
                    pairIds = new List<string>();
                    pairsByLayers.Add(layerPair, pairIds);
                }
                pairIds.Add(path.id);
            }
            foreach (var duplicate in pairsByLayers.Where(pair => pair.Value.Count > 1))
            {
                foreach (var layerId in duplicate.Key.Split('\u001f'))
                {
                    layers.Remove(layerId);
                    layerErrors[layerId] = LayerLoadError(
                        project,
                        layerId,
                        $"Layers are connected by more than one Portal pair ({string.Join(", ", duplicate.Value.Select(ArchitectorPaths.LinkPath))}). " +
                        "This usually comes from merging two branches; merge the relationships into one link document and delete the other.");
                }
            }

            foreach (var layerId in layers.Keys.ToArray())
            {
                var documents = layers[layerId];
                try
                {
                    ArchitectorValidation.Validate(documents.semantic, documents.layout);
                }
                catch (ArchitectorDataException exception)
                {
                    layers.Remove(layerId);
                    layerErrors[layerId] = LayerLoadError(project, layerId, exception.Message);
                }
            }

            BuildHierarchy(project);
            return new ArchitectorDecodedRepository(project, layers, layerErrors);
        }

        public static SortedDictionary<string, string> Encode(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers)
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                [ArchitectorPaths.ProjectFileName] = ArchitectorJson.Serialize(new ArchitectorStoredProjectDocument
                {
                    projectId = project.projectId
                })
            };
            var orderKeys = AssignOrderKeys(project);

            foreach (var group in project.groups)
            {
                files[ArchitectorPaths.GroupPath(group.id, group.name)] = ArchitectorJson.Serialize(new ArchitectorStoredGroupDocument
                {
                    id = group.id,
                    name = group.name,
                    parentGroupId = group.parentGroupId ?? string.Empty,
                    orderKey = orderKeys[group.id],
                    lockState = group.lockState ?? new ArchitectorGroupLockRecord()
                });
            }

            var endpointsByPair = new SortedDictionary<string, List<ArchitectorStoredPortalEndpoint>>(StringComparer.Ordinal);
            var pairByLayers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var layer in project.layers)
            {
                if (!layers.TryGetValue(layer.id, out var documents))
                    throw new ArchitectorDataException($"Layer '{layer.name}' has no documents to save.");
                var portalIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var portal in documents.semantic.nodes.Where(IsPortal))
                {
                    portalIds.Add(portal.id);
                    var layout = documents.layout.nodes.First(node => node.nodeId == portal.id);
                    if (!endpointsByPair.TryGetValue(portal.portal.pairId, out var endpoints))
                    {
                        endpoints = new List<ArchitectorStoredPortalEndpoint>();
                        endpointsByPair.Add(portal.portal.pairId, endpoints);
                    }
                    endpoints.Add(new ArchitectorStoredPortalEndpoint
                    {
                        layerId = layer.id,
                        nodeId = portal.id,
                        title = portal.title,
                        role = portal.portal.role,
                        x = layout.x,
                        y = layout.y,
                        width = layout.width,
                        height = layout.height
                    });
                    pairByLayers[LayerPairKey(layer.id, portal.portal.targetLayerId)] = portal.portal.pairId;
                }

                var intraEdgeIds = documents.semantic.edges
                    .Where(edge => IsIntraLayer(edge, layer.id))
                    .Select(edge => edge.id)
                    .ToHashSet(StringComparer.Ordinal);
                files[ArchitectorPaths.LayerPath(layer.id, layer.name)] = ArchitectorJson.Serialize(new ArchitectorStoredLayerDocument
                {
                    layerId = layer.id,
                    name = layer.name,
                    purpose = layer.purpose ?? string.Empty,
                    parentGroupId = layer.parentGroupId ?? string.Empty,
                    orderKey = orderKeys[layer.id],
                    scope = layer.scope ?? new ArchitectorLayerScopeRecord(),
                    nodes = documents.semantic.nodes.Where(node => !portalIds.Contains(node.id)).ToList(),
                    edges = documents.semantic.edges.Where(edge => intraEdgeIds.Contains(edge.id)).ToList()
                });
                if (!string.IsNullOrEmpty(layer.approval?.semanticHash))
                {
                    files[ArchitectorPaths.BaselinePath(layer.id, layer.name)] = ArchitectorJson.Serialize(new ArchitectorStoredBaselineDocument
                    {
                        layerId = layer.id,
                        semanticHash = layer.approval.semanticHash,
                        baseline = layer.approval.baseline ?? new ArchitectorLayerBaselineRecord()
                    });
                }
                files[ArchitectorPaths.LayoutPath(layer.id, layer.name)] = ArchitectorJson.Serialize(new ArchitectorStoredLayoutDocument
                {
                    layerId = layer.id,
                    nodes = documents.layout.nodes.Where(node => !portalIds.Contains(node.nodeId)).ToList(),
                    edges = documents.layout.edges.Where(edge => intraEdgeIds.Contains(edge.edgeId)).ToList(),
                    visibleRelationshipKinds = documents.layout.visibleRelationshipKinds
                });
            }

            var relationshipsByPair = new Dictionary<string, List<(ArchitectorEdgeRecord edge, ArchitectorEdgeLayoutRecord layout)>>(
                StringComparer.Ordinal);
            foreach (var layer in project.layers)
            {
                var documents = layers[layer.id];
                foreach (var edge in documents.semantic.edges.Where(edge => !IsIntraLayer(edge, layer.id)))
                {
                    if (!pairByLayers.TryGetValue(LayerPairKey(edge.sourceLayerId, edge.targetLayerId), out var pairId))
                        throw new ArchitectorDataException(
                            $"Cross-layer relationship '{edge.id}' requires the Portal pair between its endpoint layers.");
                    if (!relationshipsByPair.TryGetValue(pairId, out var relationships))
                    {
                        relationships = new List<(ArchitectorEdgeRecord edge, ArchitectorEdgeLayoutRecord layout)>();
                        relationshipsByPair.Add(pairId, relationships);
                    }
                    relationships.Add((edge, documents.layout.edges.First(layout => layout.edgeId == edge.id)));
                }
            }

            var digest = ArchitectorDigest.Build(project, layers);
            if (digest != null)
                files[ArchitectorPaths.DigestFileName] = digest;

            foreach (var pair in endpointsByPair)
            {
                if (pair.Value.Count != 2)
                    throw new ArchitectorDataException($"Layer Portal pair '{pair.Key}' must contain exactly two endpoints.");
                relationshipsByPair.TryGetValue(pair.Key, out var relationships);
                var ordered = (relationships ?? new List<(ArchitectorEdgeRecord edge, ArchitectorEdgeLayoutRecord layout)>())
                    .OrderBy(item => item.edge.id, StringComparer.Ordinal)
                    .ToArray();
                var endpoints = pair.Value.OrderBy(endpoint => endpoint.layerId, StringComparer.Ordinal).ToList();
                files[ArchitectorPaths.LinkPath(pair.Key, LayerName(project, endpoints[0].layerId), LayerName(project, endpoints[1].layerId))] = ArchitectorJson.Serialize(new ArchitectorStoredLinkDocument
                {
                    pairId = pair.Key,
                    endpoints = endpoints,
                    relationships = ordered.Select(item => item.edge).ToList(),
                    relationshipLayouts = ordered.Select(item => item.layout).ToList()
                });
            }
            return files;
        }

        private static string LayerName(ArchitectorProjectDocument project, string layerId) =>
            project.layers.FirstOrDefault(layer => layer.id == layerId)?.name ?? string.Empty;

        public static void BuildHierarchy(ArchitectorProjectDocument project)
        {
            var groupsById = project.groups.ToDictionary(group => group.id, StringComparer.Ordinal);
            foreach (var group in project.groups)
            {
                group.childGroupIds = new List<string>();
                group.layerIds = new List<string>();
            }
            project.rootGroupIds = new List<string>();
            project.rootLayerIds = new List<string>();

            foreach (var group in project.groups.OrderBy(group => group, OrderComparer<ArchitectorGroupRecord>(
                         group => group.orderKey,
                         group => group.id)))
            {
                if (string.IsNullOrEmpty(group.parentGroupId))
                {
                    project.rootGroupIds.Add(group.id);
                    continue;
                }
                if (!groupsById.TryGetValue(group.parentGroupId, out var parent))
                    throw new ArchitectorDataException(
                        $"Group '{group.name}' references missing parent group '{group.parentGroupId}'.");
                parent.childGroupIds.Add(group.id);
            }

            foreach (var layer in project.layers.OrderBy(layer => layer, OrderComparer<ArchitectorLayerRecord>(
                         layer => layer.orderKey,
                         layer => layer.id)))
            {
                if (string.IsNullOrEmpty(layer.parentGroupId))
                {
                    project.rootLayerIds.Add(layer.id);
                    continue;
                }
                if (!groupsById.TryGetValue(layer.parentGroupId, out var parent))
                    throw new ArchitectorDataException(
                        $"Layer '{layer.name}' references missing parent group '{layer.parentGroupId}'.");
                parent.layerIds.Add(layer.id);
            }
        }

        private static void DecodeLayer(
            IReadOnlyDictionary<string, string> files,
            string layerId,
            string layerPath,
            string layoutPath,
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> layers,
            IDictionary<string, string> layerErrors)
        {
            ArchitectorStoredLayerDocument stored;
            try
            {
                stored = ArchitectorJson.Deserialize<ArchitectorStoredLayerDocument>(files[layerPath], layerPath);
                RequireSchema(stored.schemaVersion, layerPath);
                if (!string.Equals(stored.layerId, layerId, StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Layer document '{layerPath}' declares ID '{stored.layerId}'.");
            }
            catch (ArchitectorDataException exception)
            {
                project.layers.Add(new ArchitectorLayerRecord
                {
                    id = layerId,
                    name = layerId,
                    semanticPath = layerPath,
                    layoutPath = layoutPath ?? ArchitectorPaths.LayoutPath(layerId)
                });
                layerErrors[layerId] = LayerLoadError(project, layerId, exception.Message);
                return;
            }

            layoutPath ??= ArchitectorPaths.LayoutPath(layerId, stored.name);
            project.layers.Add(new ArchitectorLayerRecord
            {
                id = layerId,
                name = stored.name ?? string.Empty,
                purpose = stored.purpose ?? string.Empty,
                parentGroupId = stored.parentGroupId ?? string.Empty,
                orderKey = stored.orderKey ?? string.Empty,
                scope = stored.scope ?? new ArchitectorLayerScopeRecord(),
                semanticPath = layerPath,
                layoutPath = layoutPath
            });

            try
            {
                if (!files.TryGetValue(layoutPath, out var layoutJson) || layoutJson == null)
                    throw new ArchitectorDataException($"Layout document '{layoutPath}' is missing.");
                var storedLayout = ArchitectorJson.Deserialize<ArchitectorStoredLayoutDocument>(layoutJson, layoutPath);
                RequireSchema(storedLayout.schemaVersion, layoutPath);
                if (!string.Equals(storedLayout.layerId, layerId, StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Layout document '{layoutPath}' declares layer ID '{storedLayout.layerId}'.");
                var nodes = stored.nodes ?? new List<ArchitectorNodeRecord>();
                var edges = stored.edges ?? new List<ArchitectorEdgeRecord>();
                if (nodes.Any(IsPortal))
                    throw new ArchitectorDataException($"Layer document '{layerPath}' cannot contain Portal nodes; Portals are stored in link documents.");
                if (edges.Any(edge => edge == null || !IsIntraLayer(edge, layerId)))
                    throw new ArchitectorDataException(
                        $"Layer document '{layerPath}' can contain only relationships inside the layer; cross-layer relationships are stored in link documents.");
                layers[layerId] = new ArchitectorLayerDocuments(
                    new ArchitectorLayerDocument
                    {
                        layerId = layerId,
                        nodes = nodes,
                        edges = edges
                    },
                    new ArchitectorLayerLayoutDocument
                    {
                        layerId = layerId,
                        nodes = storedLayout.nodes ?? new List<ArchitectorNodeLayoutRecord>(),
                        edges = storedLayout.edges ?? new List<ArchitectorEdgeLayoutRecord>(),
                        visibleRelationshipKinds = storedLayout.visibleRelationshipKinds
                    });
            }
            catch (ArchitectorDataException exception)
            {
                layerErrors[layerId] = LayerLoadError(project, layerId, exception.Message);
            }
        }

        private static void DecodeBaseline(
            IReadOnlyDictionary<string, string> files,
            string path,
            string layerId,
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> layers,
            IDictionary<string, string> layerErrors)
        {
            var layer = project.layers.FirstOrDefault(candidate => candidate.id == layerId)
                        ?? throw new ArchitectorDataException(
                            $"Baseline document '{path}' has no layer document '{ArchitectorPaths.LayerPath(layerId)}'. Delete the baseline or restore the layer.");
            try
            {
                var stored = ArchitectorJson.Deserialize<ArchitectorStoredBaselineDocument>(files[path], path);
                RequireSchema(stored.schemaVersion, path);
                if (!string.Equals(stored.layerId, layerId, StringComparison.Ordinal))
                    throw new ArchitectorDataException($"Baseline document '{path}' declares layer ID '{stored.layerId}'.");
                if (string.IsNullOrWhiteSpace(stored.semanticHash))
                    throw new ArchitectorDataException($"Baseline document '{path}' has no approval hash.");
                layer.approval = new ArchitectorLayerApprovalRecord
                {
                    semanticHash = stored.semanticHash,
                    baseline = stored.baseline ?? new ArchitectorLayerBaselineRecord()
                };
            }
            catch (ArchitectorDataException exception)
            {
                layers.Remove(layerId);
                layerErrors[layerId] = LayerLoadError(project, layerId, exception.Message);
            }
        }

        private static string DecodeLink(
            IReadOnlyDictionary<string, string> files,
            string path,
            string pairId,
            ArchitectorProjectDocument project,
            IDictionary<string, ArchitectorLayerDocuments> layers,
            IDictionary<string, string> layerErrors)
        {
            var link = ArchitectorJson.Deserialize<ArchitectorStoredLinkDocument>(files[path], path);
            RequireSchema(link.schemaVersion, path);
            if (!string.Equals(link.pairId, pairId, StringComparison.Ordinal))
                throw new ArchitectorDataException($"Link document '{path}' declares pair ID '{link.pairId}'.");
            var endpoints = link.endpoints ?? new List<ArchitectorStoredPortalEndpoint>();
            if (endpoints.Count != 2 || endpoints.Any(endpoint => endpoint == null))
                throw new ArchitectorDataException($"Link document '{path}' must contain exactly two Portal endpoints.");
            var first = endpoints[0];
            var second = endpoints[1];
            if (string.Equals(first.layerId, second.layerId, StringComparison.Ordinal))
                throw new ArchitectorDataException($"Link document '{path}' connects layer '{first.layerId}' to itself.");
            foreach (var endpoint in endpoints)
            {
                if (project.layers.All(layer => layer.id != endpoint.layerId))
                    throw new ArchitectorDataException($"Link document '{path}' references missing layer '{endpoint.layerId}'.");
            }

            var layerPair = LayerPairKey(first.layerId, second.layerId);
            var invalidEndpoint = endpoints.FirstOrDefault(endpoint => !layers.ContainsKey(endpoint.layerId));
            if (invalidEndpoint != null)
            {
                foreach (var endpoint in endpoints.Where(endpoint => layers.ContainsKey(endpoint.layerId)))
                {
                    layers.Remove(endpoint.layerId);
                    layerErrors[endpoint.layerId] = LayerLoadError(
                        project,
                        endpoint.layerId,
                        $"Portal pair '{pairId}' connects to layer '{invalidEndpoint.layerId}', which could not be loaded.");
                }
                return layerPair;
            }

            AddPortalEndpoint(layers[first.layerId], pairId, first, second);
            AddPortalEndpoint(layers[second.layerId], pairId, second, first);

            var layouts = new Dictionary<string, ArchitectorEdgeLayoutRecord>(StringComparer.Ordinal);
            foreach (var layout in link.relationshipLayouts ?? new List<ArchitectorEdgeLayoutRecord>())
            {
                if (layout == null || string.IsNullOrEmpty(layout.edgeId) || !layouts.TryAdd(layout.edgeId, layout))
                    throw new ArchitectorDataException($"Link document '{path}' contains an empty or duplicate relationship layout.");
            }
            var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relationship in link.relationships ?? new List<ArchitectorEdgeRecord>())
            {
                if (relationship == null || !relationshipIds.Add(relationship.id ?? string.Empty))
                    throw new ArchitectorDataException($"Link document '{path}' contains an empty or duplicate relationship.");
                var connectsPair =
                    string.Equals(relationship.sourceLayerId, first.layerId, StringComparison.Ordinal)
                    && string.Equals(relationship.targetLayerId, second.layerId, StringComparison.Ordinal)
                    || string.Equals(relationship.sourceLayerId, second.layerId, StringComparison.Ordinal)
                    && string.Equals(relationship.targetLayerId, first.layerId, StringComparison.Ordinal);
                if (!connectsPair)
                    throw new ArchitectorDataException(
                        $"Link document '{path}' contains relationship '{relationship.id}' outside its two layers.");
                if (!layouts.TryGetValue(relationship.id, out var layout))
                    throw new ArchitectorDataException($"Link document '{path}' has no layout for relationship '{relationship.id}'.");
                var owner = layers[relationship.sourceLayerId];
                owner.semantic.edges.Add(relationship);
                owner.layout.edges.Add(layout);
            }
            var orphanLayout = layouts.Keys.FirstOrDefault(id => !relationshipIds.Contains(id));
            if (orphanLayout != null)
                throw new ArchitectorDataException($"Link document '{path}' has a layout for missing relationship '{orphanLayout}'.");
            return layerPair;
        }

        private static void AddPortalEndpoint(
            ArchitectorLayerDocuments documents,
            string pairId,
            ArchitectorStoredPortalEndpoint endpoint,
            ArchitectorStoredPortalEndpoint counterpart)
        {
            documents.semantic.nodes.Add(new ArchitectorNodeRecord
            {
                id = endpoint.nodeId,
                title = endpoint.title,
                kind = ArchitectorNodeKinds.LayerPortal,
                type = new ArchitectorTypeRecord { mode = "none" },
                portal = new ArchitectorPortalRecord
                {
                    pairId = pairId,
                    targetLayerId = counterpart.layerId,
                    counterpartNodeId = counterpart.nodeId,
                    role = endpoint.role
                }
            });
            documents.layout.nodes.Add(new ArchitectorNodeLayoutRecord
            {
                nodeId = endpoint.nodeId,
                x = endpoint.x,
                y = endpoint.y,
                width = endpoint.width,
                height = endpoint.height
            });
        }

        private static Dictionary<string, string> AssignOrderKeys(ArchitectorProjectDocument project)
        {
            var groupsById = project.groups.ToDictionary(group => group.id, StringComparer.Ordinal);
            var layersById = project.layers.ToDictionary(layer => layer.id, StringComparer.Ordinal);
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);

            void AssignGroups(IEnumerable<string> ids)
            {
                foreach (var pair in ArchitectorOrderKeys.Assign(ids.Select(id => (id, groupsById[id].orderKey)).ToArray()))
                    keys.Add(pair.Key, pair.Value);
            }

            void AssignLayers(IEnumerable<string> ids)
            {
                foreach (var pair in ArchitectorOrderKeys.Assign(ids.Select(id => (id, layersById[id].orderKey)).ToArray()))
                    keys.Add(pair.Key, pair.Value);
            }

            AssignGroups(project.rootGroupIds);
            AssignLayers(project.rootLayerIds);
            foreach (var group in project.groups)
            {
                AssignGroups(group.childGroupIds);
                AssignLayers(group.layerIds);
            }
            return keys;
        }

        private static IComparer<T> OrderComparer<T>(Func<T, string> key, Func<T, string> id)
        {
            return Comparer<T>.Create((first, second) => ArchitectorOrderKeys.Compare(key(first), id(first), key(second), id(second)));
        }

        private static IEnumerable<(string path, string id)> EntryPaths(
            IReadOnlyDictionary<string, string> files,
            string directory,
            string suffix)
        {
            var entries = new List<(string path, string id)>();
            foreach (var pair in files)
            {
                if (pair.Value != null && TryGetEntryId(pair.Key, directory, suffix, out var id))
                    entries.Add((pair.Key, id));
            }
            foreach (var duplicate in entries.GroupBy(entry => entry.id, StringComparer.Ordinal).Where(group => group.Count() > 1))
                throw new ArchitectorDataException(
                    $"Two files describe '{duplicate.Key}': {string.Join(", ", duplicate.Select(entry => entry.path).OrderBy(path => path, StringComparer.Ordinal))}. " +
                    "This usually comes from merging branches that renamed it differently; keep one file and delete the other.");
            return entries.OrderBy(entry => entry.path, StringComparer.Ordinal).ToArray();
        }

        public static IEnumerable<string> EntryAliases(IEnumerable<string> paths, string path)
        {
            foreach (var (directory, suffix) in EntryKinds)
            {
                if (!TryGetEntryId(path, directory, suffix, out var id))
                    continue;
                foreach (var candidate in paths)
                    if (!string.Equals(candidate, path, StringComparison.Ordinal)
                        && TryGetEntryId(candidate, directory, suffix, out var candidateId)
                        && string.Equals(candidateId, id, StringComparison.Ordinal))
                        yield return candidate;
            }
        }

        private static readonly (string directory, string suffix)[] EntryKinds =
        {
            (ArchitectorPaths.GroupsDirectoryName, ArchitectorPaths.GroupFileSuffix),
            (ArchitectorPaths.LayersDirectoryName, ArchitectorPaths.LayerFileSuffix),
            (ArchitectorPaths.LayoutsDirectoryName, ArchitectorPaths.LayoutFileSuffix),
            (ArchitectorPaths.LinksDirectoryName, ArchitectorPaths.LinkFileSuffix),
            (ArchitectorPaths.BaselinesDirectoryName, ArchitectorPaths.BaselineFileSuffix)
        };

        public static string FindEntry(IReadOnlyDictionary<string, string> files, string directory, string suffix, string id)
        {
            foreach (var pair in files)
                if (pair.Value != null && TryGetEntryId(pair.Key, directory, suffix, out var entryId) && string.Equals(entryId, id, StringComparison.Ordinal))
                    return pair.Value;
            return null;
        }

        private static bool TryGetEntryId(string relativePath, string directory, string suffix, out string id)
        {
            id = string.Empty;
            var prefix = directory + "/";
            if (relativePath == null
                || !relativePath.StartsWith(prefix, StringComparison.Ordinal)
                || !relativePath.EndsWith(suffix, StringComparison.Ordinal))
                return false;
            var name = relativePath.Substring(prefix.Length, relativePath.Length - prefix.Length - suffix.Length);
            var separator = name.IndexOf('.');
            id = separator < 0 ? name : name.Substring(0, separator);
            return id.Length > 0 && name.IndexOfAny(new[] { '/', '\\' }) < 0;
        }

        private static void RequireSchema(int schemaVersion, string path)
        {
            if (schemaVersion != SchemaVersion)
                throw new ArchitectorDataException(
                    $"Document '{path}' uses unsupported storage schema version {schemaVersion}; expected {SchemaVersion}.");
        }

        private static string LayerLoadError(ArchitectorProjectDocument project, string layerId, string message)
        {
            var record = project.layers.FirstOrDefault(layer => layer.id == layerId);
            var name = record?.name;
            var layerPath = string.IsNullOrEmpty(record?.semanticPath) ? ArchitectorPaths.LayerPath(layerId) : record.semanticPath;
            var layoutPath = string.IsNullOrEmpty(record?.layoutPath) ? ArchitectorPaths.LayoutPath(layerId) : record.layoutPath;
            return $"Layer '{(string.IsNullOrEmpty(name) ? layerId : name)}' could not be loaded from " +
                   $"'{layerPath}' and '{layoutPath}': {message}";
        }

        private static bool IsPortal(ArchitectorNodeRecord node)
        {
            return node != null && string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal);
        }

        private static bool IsIntraLayer(ArchitectorEdgeRecord edge, string layerId)
        {
            return string.Equals(edge.sourceLayerId, layerId, StringComparison.Ordinal)
                   && string.Equals(edge.targetLayerId, layerId, StringComparison.Ordinal);
        }

        private static string LayerPairKey(string firstLayerId, string secondLayerId)
        {
            return string.CompareOrdinal(firstLayerId, secondLayerId) <= 0
                ? $"{firstLayerId}\u001f{secondLayerId}"
                : $"{secondLayerId}\u001f{firstLayerId}";
        }
    }
}
