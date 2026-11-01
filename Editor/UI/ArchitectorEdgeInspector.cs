using System;
using System.Linq;
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

        public void Draw(string layerId, string edgeId, ArchitectorRepository repository, Action<Action> execute)
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

                EditorGUILayout.Space(8f);
                EditorGUILayout.HelpBox(
                    edge.kind == ArchitectorEdgeKinds.Dependency
                        ? "Dependency relationships must remain acyclic."
                        : "Runtime relationships may be reversed or cyclic.",
                    MessageType.None);
            }
        }

        private void LoadEdge(string edgeId, ArchitectorRepository repository)
        {
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
