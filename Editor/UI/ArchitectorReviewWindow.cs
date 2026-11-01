using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorReviewWindow : EditorWindow
    {
        private const string Title = "Architector Review";

        private ArchitectorRepository repository;
        private ArchitectorTypeIndex typeIndex;
        private string loadError = string.Empty;
        private Vector2 scroll;
        private bool changedExpanded = true;
        private bool byAgentExpanded = true;
        private bool draftExpanded = true;
        private int errorsRevision = -1;
        private int errorsGeneration = -1;
        private readonly Dictionary<string, int> errorCounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> selected = new(StringComparer.Ordinal);

        [MenuItem("Window/Architector/Review")]
        public static void Open()
        {
            var master = Resources.FindObjectsOfTypeAll<ArchitectorMasterWindow>()
                .FirstOrDefault(candidate => candidate.IsMasterWindow);
            if (master != null)
                master.Focus();
            var window = master != null
                ? GetWindow<ArchitectorReviewWindow>(Title, true, typeof(ArchitectorMasterWindow))
                : GetWindow<ArchitectorReviewWindow>();
            window.titleContent = new GUIContent(Title);
            window.minSize = new Vector2(560f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(Title);
            if (!ArchitectorSession.TryGet(out var session, out loadError))
                return;
            repository = session.Repository;
            typeIndex = session.TypeIndex;
            repository.Changed += Repaint;
        }

        private void OnDisable()
        {
            if (repository != null)
                repository.Changed -= Repaint;
        }

        private void OnGUI()
        {
            if (repository == null)
            {
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
                if (GUILayout.Button("Retry"))
                    OnEnable();
                return;
            }

            RefreshErrors();
            var statuses = repository.GetLayerApprovalStatuses();
            var changed = statuses.Where(status => status.State == ArchitectorApprovalStates.Changed).ToArray();
            var byAgent = statuses.Where(status => status.State == ArchitectorApprovalStates.Approved
                                                   && repository.FindLayer(status.LayerId).approval?.approvedByAgent == true).ToArray();
            var drafts = statuses.Where(status => status.State == ArchitectorApprovalStates.Draft).ToArray();
            var visible = changed.Concat(byAgent).Concat(drafts).Select(status => status.LayerId).ToHashSet(StringComparer.Ordinal);
            selected.IntersectWith(visible);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUILayout.VerticalScope(ArchitectorFoldoutBox.ContentStyle))
            {
                DrawSection("Changed", changed, ref changedExpanded);
                DrawSection("Approved By Agent", byAgent, ref byAgentExpanded);
                DrawSection("Draft", drafts, ref draftExpanded);
                if (visible.Count == 0)
                    EditorGUILayout.HelpBox("Every layer is approved.", MessageType.Info);
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                {
                    if (GUILayout.Button($"Approve Selected ({selected.Count})", GUILayout.Height(26f), GUILayout.Width(180f)))
                        ApproveSelected();
                }
            }
        }

        private void DrawSection(string title, IReadOnlyList<ArchitectorLayerApprovalStatus> statuses, ref bool expanded)
        {
            if (statuses.Count == 0)
                return;
            using var box = new ArchitectorFoldoutBox($"{title} ({statuses.Count})", ref expanded);
            if (!box.Expanded)
                return;
            foreach (var status in statuses)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var isSelected = selected.Contains(status.LayerId);
                    if (EditorGUILayout.Toggle(isSelected, GUILayout.Width(18f)) != isSelected)
                    {
                        if (isSelected)
                            selected.Remove(status.LayerId);
                        else
                            selected.Add(status.LayerId);
                    }
                    GUILayout.Label(repository.GetEntryPath(status.LayerId));
                    GUILayout.FlexibleSpace();
                    if (errorCounts.TryGetValue(status.LayerId, out var errors) && errors > 0)
                        GUILayout.Label(new GUIContent($"{errors} {(errors == 1 ? "error" : "errors")}", EditorGUIUtility.IconContent("console.erroricon.sml").image), EditorStyles.miniLabel);
                    if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(50f)))
                        ArchitectorMasterWindow.OpenLayerInMaster(status.LayerId);
                }
            }
        }

        private void RefreshErrors()
        {
            if (errorsRevision == repository.SemanticRevision && errorsGeneration == typeIndex.Generation)
                return;
            errorsRevision = repository.SemanticRevision;
            errorsGeneration = typeIndex.Generation;
            errorCounts.Clear();
            var report = ArchitectorConformance.Evaluate(repository, typeIndex, repository.Project.layers.Select(layer => layer.id), int.MaxValue);
            foreach (var diagnostic in report.Diagnostics.Where(item => item.Severity == ArchitectorConformanceSeverities.Error))
            {
                errorCounts.TryGetValue(diagnostic.LayerId, out var count);
                errorCounts[diagnostic.LayerId] = count + 1;
            }
        }

        private void ApproveSelected()
        {
            try
            {
                repository.ApproveLayers(selected.ToArray());
                selected.Clear();
            }
            catch (ArchitectorDataException exception)
            {
                ShowNotification(new GUIContent(exception.Message));
            }
        }
    }
}
