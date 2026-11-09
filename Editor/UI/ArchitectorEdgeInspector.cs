using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorEdgeInspector
    {
        private static readonly string[] Kinds = ArchitectorEdgeKinds.All.ToArray();
        private static readonly string[] KindNames = Kinds.Select(ArchitectorEdgeKinds.DisplayName).ToArray();
        private string cachedEdgeId = string.Empty;
        private int cachedRevision = -1;
        private ArchitectorEdgeRecord edge;
        private string sourceCaption = string.Empty;
        private string targetCaption = string.Empty;
        private string[] cachedTrunkIds = Array.Empty<string>();
        private string[] trunkCaptions = Array.Empty<string>();
        private Vector2 trunkScroll;

        public void Draw(string layerId, string edgeId, ArchitectorRepository repository, Action<Action> execute, IReadOnlyList<string> trunkEdges = null)
        {
            if (cachedEdgeId != edgeId || cachedRevision != repository.Revision)
                LoadEdge(edgeId, repository);
            if (edge == null)
                return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(ArchitectorNodeInspector.Width), GUILayout.ExpandHeight(true)))
            {
                GUILayout.Label("Relationship Inspector", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("Source", sourceCaption);
                    EditorGUILayout.TextField("Target", targetCaption);
                }
                var currentIndex = Mathf.Max(0, Array.IndexOf(Kinds, edge.kind));
                EditorGUI.BeginChangeCheck();
                var nextIndex = EditorGUILayout.Popup("Relationship Type", currentIndex, KindNames);
                var nextLabel = EditorGUILayout.DelayedTextField("Label", edge.label ?? string.Empty);
                if (EditorGUI.EndChangeCheck())
                    execute(() => repository.UpdateEdgeKind(layerId, edgeId, Kinds[nextIndex], nextLabel));

                if (trunkEdges != null && trunkEdges.Count > 1)
                {
                    EditorGUILayout.Space(8f);
                    GUILayout.Label($"Shared Trunk ({trunkEdges.Count})", EditorStyles.boldLabel);
                    if (!trunkEdges.SequenceEqual(cachedTrunkIds, StringComparer.Ordinal))
                    {
                        cachedTrunkIds = trunkEdges.ToArray();
                        trunkCaptions = trunkEdges.Select(repository.FindRelationship).Where(member => member != null)
                            .Select(member => $"{Caption(member.sourceLayerId, member.sourceNodeId, repository)} → {Caption(member.targetLayerId, member.targetNodeId, repository)}{(string.IsNullOrEmpty(member.label) ? "" : " · " + member.label)}")
                            .ToArray();
                    }
                    using (var scroll = new EditorGUILayout.ScrollViewScope(trunkScroll, GUILayout.MaxHeight(320f)))
                    {
                        trunkScroll = scroll.scrollPosition;
                        foreach (var caption in trunkCaptions)
                            EditorGUILayout.LabelField(caption, EditorStyles.wordWrappedMiniLabel);
                    }
                }
                EditorGUILayout.Space(8f);
                EditorGUILayout.HelpBox(
                    edge.kind != ArchitectorEdgeKinds.Dependency
                        ? "Runtime relationships may be reversed or cyclic."
                        : repository.Settings.rules.dependencyCycles == ArchitectorDependencyCyclePolicy.Reject
                            ? "Dependency relationships between layers must remain acyclic; a cycle inside one layer is reported, not rejected."
                            : repository.FindDependencyCycles().Any(cycle => cycle.Contains(edge))
                                ? "This dependency is part of a cycle. The project reports cycles instead of rejecting them."
                                : "The project reports dependency cycles instead of rejecting them.",
                    MessageType.None);
            }
        }

        private void LoadEdge(string edgeId, ArchitectorRepository repository)
        {
            cachedTrunkIds = Array.Empty<string>();
            trunkCaptions = Array.Empty<string>();
            cachedEdgeId = edgeId;
            cachedRevision = repository.Revision;
            edge = repository.FindRelationship(edgeId);
            if (edge == null)
                return;
            sourceCaption = Caption(edge.sourceLayerId, edge.sourceNodeId, repository);
            targetCaption = Caption(edge.targetLayerId, edge.targetNodeId, repository);
        }

        private static string Caption(string layerId, string nodeId, ArchitectorRepository repository)
        {
            var layer = repository.FindLayer(layerId);
            var node = repository.LoadLayer(layerId).semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeId);
            return $"{layer?.name} / {node?.title ?? nodeId}";
        }
    }
}
