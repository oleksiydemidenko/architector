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
        private const float RowHeight = 20f;
        private const float CheckboxWidth = 22f;
        private const float OpenWidth = 50f;
        private const float BottomBarHeight = 34f;
        private const float DragScrollMargin = 24f;
        private const float DragScrollStep = 12f;
        private static readonly int RowControlHint = "ArchitectorReviewRows".GetHashCode();

        private ArchitectorRepository repository;
        private ArchitectorTypeIndex typeIndex;
        private string loadError = string.Empty;
        private Vector2 scroll;
        private bool changedExpanded = true;
        private bool byAgentExpanded = true;
        private bool draftExpanded = true;
        private int errorsRevision = -1;
        private int errorsGeneration = -1;
        private string anchorLayerId = string.Empty;
        private int dragRow = -1;
        private readonly HashSet<string> strokeRows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> errorCounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> selected = new(StringComparer.Ordinal);
        private HashSet<string> visibleLayers = new(StringComparer.Ordinal);
        private readonly List<string> rows = new();
        private readonly List<Rect> rowRects = new();

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

        public static int PendingCount(ArchitectorRepository repository)
        {
            var uncheckedLayers = repository.UncheckedLayerIds();
            return repository.Project.layers.Count(layer => !uncheckedLayers.Contains(layer.id)
                                                            && NeedsReview(layer, repository.GetLayerApprovalStatus(layer.id).State));
        }

        private static bool NeedsReview(ArchitectorLayerRecord layer, string state)
        {
            return state == ArchitectorApprovalStates.Changed
                   || state == ArchitectorApprovalStates.Draft
                   || state == ArchitectorApprovalStates.Approved && layer.approval?.approvedByAgent == true;
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
            var statuses = repository.GetLayerApprovalStatuses().ToDictionary(status => status.LayerId, StringComparer.Ordinal);
            var uncheckedLayers = repository.UncheckedLayerIds();
            var layers = LayersInExplorerOrder().Where(layer => statuses.ContainsKey(layer.id) && !uncheckedLayers.Contains(layer.id)).ToArray();
            var changed = layers.Where(layer => statuses[layer.id].State == ArchitectorApprovalStates.Changed).ToArray();
            var byAgent = layers.Where(layer => statuses[layer.id].State == ArchitectorApprovalStates.Approved
                                                && layer.approval?.approvedByAgent == true).ToArray();
            var drafts = layers.Where(layer => statuses[layer.id].State == ArchitectorApprovalStates.Draft).ToArray();
            var visible = changed.Concat(byAgent).Concat(drafts).Select(layer => layer.id).ToHashSet(StringComparer.Ordinal);
            selected.IntersectWith(visible);

            rows.Clear();
            rowRects.Clear();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUILayout.VerticalScope(ArchitectorFoldoutBox.ContentStyle))
            {
                DrawSection("Changed", changed, ref changedExpanded);
                DrawSection("Approved By Agent", byAgent, ref byAgentExpanded);
                DrawSection("Draft", drafts, ref draftExpanded);
                if (visible.Count == 0)
                    EditorGUILayout.HelpBox("Every layer is approved.", MessageType.Info);
            }
            HandleRowInput();
            EditorGUILayout.EndScrollView();

            visibleLayers = visible;
            DrawBottomBar(visible);
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                ClearSelection();
                Event.current.Use();
            }
        }

        private IEnumerable<ArchitectorLayerRecord> LayersInExplorerOrder()
        {
            var project = repository.Project;
            foreach (var groupId in project.rootGroupIds)
            {
                foreach (var layer in LayersInGroup(groupId))
                    yield return layer;
            }
            foreach (var layerId in project.rootLayerIds)
                yield return repository.FindLayer(layerId);
        }

        private IEnumerable<ArchitectorLayerRecord> LayersInGroup(string groupId)
        {
            var group = repository.FindGroup(groupId);
            foreach (var childId in group.childGroupIds)
            {
                foreach (var layer in LayersInGroup(childId))
                    yield return layer;
            }
            foreach (var layerId in group.layerIds)
                yield return repository.FindLayer(layerId);
        }

        private void DrawSection(string title, IReadOnlyList<ArchitectorLayerRecord> layers, ref bool expanded)
        {
            if (layers.Count == 0)
                return;
            using var box = new ArchitectorFoldoutBox($"{title} ({layers.Count})", ref expanded);
            if (!box.Expanded)
                return;
            foreach (var group in layers.GroupBy(layer => layer.parentGroupId ?? string.Empty))
            {
                DrawGroupHeader(group.Key, group.Select(layer => layer.id).ToArray());
                foreach (var layer in group)
                    DrawRow(layer);
                EditorGUILayout.Space(4f);
            }
        }

        private void DrawGroupHeader(string groupId, IReadOnlyList<string> layerIds)
        {
            var path = string.IsNullOrEmpty(groupId) ? "Root" : repository.GetEntryPath(groupId);
            var count = layerIds.Count(selected.Contains);
            var label = count > 0 ? $"{path}  ({count} of {layerIds.Count})" : path;
            if (!GUILayout.Button(new GUIContent(label, "Select or clear the layers of this group."), EditorStyles.miniBoldLabel))
                return;
            if (count == layerIds.Count)
                selected.ExceptWith(layerIds);
            else
                selected.UnionWith(layerIds);
        }

        private void DrawRow(ArchitectorLayerRecord layer)
        {
            var rect = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));
            rows.Add(layer.id);
            rowRects.Add(rect);
            var isSelected = selected.Contains(layer.id);
            var openRect = new Rect(rect.xMax - OpenWidth, rect.y + 1f, OpenWidth, rect.height - 2f);
            if (Event.current.type == EventType.Repaint)
            {
                if (isSelected)
                    EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0.24f, 0.37f, 0.59f, 0.55f) : new Color(0.24f, 0.48f, 0.90f, 0.30f));
                EditorStyles.toggle.Draw(new Rect(rect.x + 4f, rect.y + 2f, 16f, 16f), false, false, isSelected, false);
            }
            var labelRight = openRect.x - 4f;
            if (errorCounts.TryGetValue(layer.id, out var errors) && errors > 0)
            {
                var errorContent = new GUIContent($"{errors} {(errors == 1 ? "error" : "errors")}", EditorGUIUtility.IconContent("console.erroricon.sml").image);
                var width = EditorStyles.miniLabel.CalcSize(errorContent).x + 4f;
                GUI.Label(new Rect(labelRight - width, rect.y, width, rect.height), errorContent, EditorStyles.miniLabel);
                labelRight -= width + 4f;
            }
            GUI.Label(new Rect(rect.x + CheckboxWidth, rect.y, Mathf.Max(0f, labelRight - rect.x - CheckboxWidth), rect.height), layer.name);
            if (GUI.Button(openRect, "Open", EditorStyles.miniButton))
                ArchitectorMasterWindow.OpenLayerInMaster(layer.id);
        }

        private void HandleRowInput()
        {
            var controlId = GUIUtility.GetControlID(RowControlHint, FocusType.Passive);
            var current = Event.current;
            switch (current.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when current.button == 0:
                {
                    var row = RowAt(current.mousePosition);
                    if (row < 0)
                        return;
                    strokeRows.Clear();
                    var anchor = rows.IndexOf(anchorLayerId);
                    if (current.shift && anchor >= 0)
                    {
                        for (var index = Mathf.Min(anchor, row); index <= Mathf.Max(anchor, row); index++)
                        {
                            selected.Add(rows[index]);
                            strokeRows.Add(rows[index]);
                        }
                    }
                    else
                    {
                        ToggleRange(row, row);
                        anchorLayerId = rows[row];
                    }
                    dragRow = row;
                    GUIUtility.hotControl = controlId;
                    current.Use();
                    break;
                }
                case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                {
                    var relativeY = current.mousePosition.y - scroll.y;
                    var viewHeight = position.height - BottomBarHeight;
                    if (relativeY < DragScrollMargin)
                        scroll.y = Mathf.Max(0f, scroll.y - DragScrollStep);
                    else if (relativeY > viewHeight - DragScrollMargin)
                        scroll.y += DragScrollStep;
                    var row = NearestRow(current.mousePosition.y);
                    if (row >= 0 && row != dragRow)
                    {
                        ToggleRange(dragRow, row);
                        dragRow = row;
                    }
                    current.Use();
                    break;
                }
                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                    GUIUtility.hotControl = 0;
                    current.Use();
                    break;
            }
        }

        private int RowAt(Vector2 point)
        {
            return rowRects.FindIndex(rect => rect.Contains(point));
        }

        private int NearestRow(float y)
        {
            if (rowRects.Count == 0)
                return -1;
            var row = rowRects.FindIndex(rect => y < rect.yMax);
            return row >= 0 ? row : rowRects.Count - 1;
        }

        private void ToggleRange(int from, int to)
        {
            for (var index = Mathf.Min(from, to); index <= Mathf.Max(from, to); index++)
            {
                if (!strokeRows.Add(rows[index]))
                    continue;
                if (!selected.Remove(rows[index]))
                    selected.Add(rows[index]);
            }
        }

        private void DrawBottomBar(IReadOnlyCollection<string> visible)
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(BottomBarHeight)))
            {
                GUILayout.Space(8f);
                using (new EditorGUI.DisabledScope(visible.Count == 0))
                {
                    if (GUILayout.Button("Select All", GUILayout.Height(26f), GUILayout.Width(90f)))
                        selected.UnionWith(visible);
                }
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                {
                    if (GUILayout.Button("Clear", GUILayout.Height(26f), GUILayout.Width(70f)))
                        selected.Clear();
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                {
                    if (GUILayout.Button($"Approve Selected ({selected.Count})", GUILayout.Height(26f), GUILayout.Width(180f)))
                        ApproveSelected();
                }
                GUILayout.Space(8f);
            }
        }

        public void SelectAll()
        {
            selected.UnionWith(visibleLayers);
            Repaint();
        }

        public void ClearSelection()
        {
            selected.Clear();
            Repaint();
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
