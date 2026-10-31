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
        public void Draw(string layerId, string edgeId, ArchitectorRepository repository, Action<Action> execute)
        {
            var edge = repository.FindRelationship(edgeId);
            if (edge == null)
                return;
            var sourceLayer = repository.FindLayer(edge.sourceLayerId);
            var targetLayer = repository.FindLayer(edge.targetLayerId);
            var source = repository.LoadLayer(edge.sourceLayerId).semantic.nodes.FirstOrDefault(node => node.id == edge.sourceNodeId);
            var target = repository.LoadLayer(edge.targetLayerId).semantic.nodes.FirstOrDefault(node => node.id == edge.targetNodeId);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(ArchitectorNodeInspector.Width), GUILayout.ExpandHeight(true)))
            {
                GUILayout.Label("Relationship Inspector", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("Source", $"{sourceLayer?.name} / {source?.title ?? edge.sourceNodeId}");
                    EditorGUILayout.TextField("Target", $"{targetLayer?.name} / {target?.title ?? edge.targetNodeId}");
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
    }
}
