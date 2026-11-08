using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorMasterWindow : EditorWindow
    {
        private enum WindowMode
        {
            Master,
            Layer
        }

        [Flags]
        private enum RelationshipKindFilter
        {
            None = 0,
            Dependency = ArchitectorRelationshipVisibility.Dependency,
            DataFlow = ArchitectorRelationshipVisibility.DataFlow,
            Event = ArchitectorRelationshipVisibility.Event,
            Callback = ArchitectorRelationshipVisibility.Callback
        }

        private const float HeaderHeight = 30f;
        private const float ExplorerWidth = 270f;
        private const float LayerTabsHeight = 34f;
        internal const string ClearLayerTabsDialogKey = "Architector.ClearLayerTabs.Confirm";
        private const float TabBorderOverlap = 1f;
        private const string ExplorerDragDataKey = "ArchitectorExplorerDrag";
        private const string LayerTabDragDataKey = "ArchitectorLayerTabDrag";
        private const float TabDragThresholdSquared = 16f;

        [SerializeField] private string currentGroupId = string.Empty;
        [SerializeField] private string activeLayerId = string.Empty;
        [SerializeField] private string search = string.Empty;
        [SerializeField] private WindowMode windowMode;
        [SerializeField] private string workspaceViewId = string.Empty;
        [SerializeField] private List<string> requestedWorkspaceLayerIds = new();
        [SerializeField] private bool initializeWorkspaceWindow;
        [SerializeField] private List<string> openLayerIds = new();
        [SerializeField] private List<string> layerVisitHistory = new();
        [SerializeField] private int layerVisitHistoryIndex = -1;
        [SerializeField] private float layerTabsScroll;

        private ArchitectorRepository repository;
        private ArchitectorUserStateStore userState;
        private ArchitectorNodeClipboard nodeClipboard;
        private ArchitectorTypeIndex typeIndex;
        private ArchitectorAiWorkCoordinator aiWork;
        private Vector2 explorerScroll;
        private readonly HashSet<string> selectedExplorerEntryIds = new(StringComparer.Ordinal);
        private string explorerSelectionAnchorId = string.Empty;
        private string pressedExplorerEntryId = string.Empty;
        private Vector2 pressedExplorerPosition;
        private bool pressedExplorerShouldOpen;
        private bool pressedExplorerCollapseSelection;
        private Rect explorerDropIndicatorRect;
        private string explorerDropIntoGroupId = string.Empty;
        private bool explorerDropOnBack;
        private string renameEntryId = string.Empty;
        private string renameValue = string.Empty;
        private bool renameNeedsFocus;
        private string loadError = string.Empty;
        private ArchitectorLayerCanvas layerCanvas;
        private readonly ArchitectorNodeInspector nodeInspector = new();
        private readonly ArchitectorEdgeInspector edgeInspector = new();
        private string inspectedLayerId = string.Empty;
        private string inspectedNodeId = string.Empty;
        private string inspectedEdgeId = string.Empty;
        private bool deferInspectorSelection;
        private bool inspectorPointerDown;
        private bool inspectorPointerDragged;
        private Vector2 inspectorPointerStart;
        private bool explorerKeyboardFocus;
        private string stepPreviewLayerId = string.Empty;
        private bool inspectorArranging;
        private bool inspectorPointerOnCanvas;
        private string inspectorPointerSelection = string.Empty;
        private Rect lastCanvasRect;
        private GenericMenu pendingContextMenu;
        private Vector2 pendingContextMenuPosition;
        private bool pendingContextMenuNeedsRepaint;
        private GUIStyle versionStyle;
        private GUIStyle breadcrumbStyle;
        private GUIStyle layerTabsBarStyle;
        private GUIStyle layerTabStyle;
        private GUIStyle layerTabNavigationStyle;
        private GUIStyle reviewCountStyle;
        private GUIStyle tabCloseStyle;
        // Unity keeps window fields across a domain reload but brings a GUIStyle back without word wrap, so wrapped styles are rebuilt.
        [NonSerialized] private GUIStyle conformanceDiagnosticStyle;
        private Texture2D layerIcon;
        private string loadedLayerIconPath = string.Empty;
        private bool scrollActiveLayerIntoView = true;
        [NonSerialized] private float lastTabsViewportWidth;
        private string pressedLayerTabId = string.Empty;
        private Vector2 pressedLayerTabPosition;
        private float layerTabDropIndicatorX = -1f;
        private bool explorerTabDropActive;
        private string purposeLayerId = string.Empty;
        private string baselineChangesLayerId = string.Empty;
        private int baselineChangesRevision = -1;
        private IReadOnlyList<ArchitectorBaselineChange> baselineChanges = Array.Empty<ArchitectorBaselineChange>();
        private bool baselineChangesExpanded = true;
        private Vector2 baselineChangesScroll;
        [NonSerialized] private GUIStyle baselineChangeStyle;
        [NonSerialized] private GUIStyle headerTagStyle;
        private readonly Dictionary<string, ArchitectorExplorerEntryStatus> explorerStatus = new(StringComparer.Ordinal);
        private int explorerStatusRevision = -1;
        private int explorerStatusTypeGeneration = -1;
        private string purposeDraft = string.Empty;
        private bool purposeDirty;
        private ArchitectorConformanceReport layerConformance;
        private string layerConformanceLayerId = string.Empty;
        private int layerConformanceRepositoryRevision = -1;
        private int layerConformanceTypeGeneration = -1;
        private readonly Dictionary<string, Vector2> conformanceScrollByLayerId = new(StringComparer.Ordinal);
        private ArchitectorConformanceReport conformanceLayoutReport;
        private float[] conformanceRowOffsets = Array.Empty<float>();
        private float[] conformanceRowHeights = Array.Empty<float>();
        private float conformanceContentHeight;
        private float conformanceLayoutPixelsPerPoint;
        private string explorerSearchCacheKey = string.Empty;
        private IReadOnlyList<string> explorerSearchLayerIds = Array.Empty<string>();
        private string currentDrawDataError = string.Empty;

        public bool IsMasterWindow => windowMode == WindowMode.Master;
        public bool CanvasHasPointer => layerCanvas?.HasPointer == true;

        // F over the tab strip scrolls it to the tab of the layer open on the canvas.
        public bool TabsHavePointer => tabsHavePointer;
        [NonSerialized] private bool tabsHavePointer;
        [NonSerialized] private Rect lastTabsRect;

        public void RevealActiveTab()
        {
            scrollActiveLayerIntoView = true;
            Repaint();
        }
        public bool ExplorerHasKeyboardFocus => explorerKeyboardFocus && selectedExplorerEntryIds.Count == 1
                                                && repository?.FindLayer(selectedExplorerEntryIds.First()) != null;

        public void StepExplorerLayer(int direction)
        {
            if (!loaded)
                return;
            var layers = (string.IsNullOrWhiteSpace(search) ? CurrentExplorerEntryOrder() : explorerSearchLayerIds)
                .Where(id => repository.FindLayer(id) != null)
                .ToList();
            var index = layers.IndexOf(selectedExplorerEntryIds.First());
            var next = index + direction;
            if (index < 0 || next < 0 || next >= layers.Count)
                return;
            var target = layers[next];
            openLayerIds ??= new List<string>();
            var alreadyOpen = openLayerIds.Contains(target);
            var previewIndex = openLayerIds.IndexOf(stepPreviewLayerId);
            if (!alreadyOpen && previewIndex >= 0 && string.Equals(activeLayerId, stepPreviewLayerId, StringComparison.Ordinal))
                openLayerIds[previewIndex] = target;
            OpenLayer(target);
            stepPreviewLayerId = alreadyOpen ? string.Empty : target;
            Repaint();
        }

        public void SendCanvasCommand(string command)
        {
            if (!loaded)
                return;
            layerCanvas?.SendCommand(command);
            SendEvent(EditorGUIUtility.CommandEvent(ArchitectorLayerCanvas.CommandEventName));
            Repaint();
        }

        public void NavigateHistory(int direction)
        {
            if (!loaded)
                return;
            NavigateLayerHistory(direction);
            Repaint();
        }

        [MenuItem("Window/Architector/Window")]
        public static void Open()
        {
            var window = Resources.FindObjectsOfTypeAll<ArchitectorMasterWindow>()
                .FirstOrDefault(candidate => candidate.windowMode == WindowMode.Master);
            if (window == null)
            {
                window = CreateInstance<ArchitectorMasterWindow>();
                window.windowMode = WindowMode.Master;
                window.workspaceViewId = "master";
                window.RestoreNavigation();
                window.Show();
            }
            window.titleContent = new GUIContent("Architector");
            window.minSize = new Vector2(720f, 420f);
            window.Focus();
        }

        private static void OpenLayerWindow(string layerId)
        {
            OpenWorkspaceWindow(new[] { layerId });
        }

        private static void OpenEmptyWorkspaceWindow()
        {
            OpenWorkspaceWindow(Array.Empty<string>());
        }

        private static void OpenWorkspaceWindow(IReadOnlyCollection<string> layerIds)
        {
            var window = CreateInstance<ArchitectorMasterWindow>();
            window.ConfigureAsWorkspaceWindow(layerIds);
            window.minSize = new Vector2(520f, 360f);
            window.Show();
            window.Focus();
        }

        private void OnEnable()
        {
            EnsureWorkspaceCanvas();
            UpdateWindowTitle();
            wantsMouseMove = true;
            wantsMouseEnterLeaveWindow = true;
            wantsLessLayoutEvents = true;
            loaded = false;
            loading ??= new ArchitectorDeferredWork(this);
            var connected = false;
            if (ArchitectorSession.IsCreated)
                LoadGuarded(() => connected = ConnectSession());
            loading.Run(connected ? "Indexing project types" : "Reading the architecture", LoadWindow(connected), FinishLoading);
            Repaint();
        }

        // Opening shows the window frame at once; each heavy step runs on its own editor update after the window
        // was painted, and the loading bar glides between steps, so the window fills in instead of hanging white.
        [NonSerialized] private bool loaded;
        [NonSerialized] private ArchitectorDeferredWork loading;
        [NonSerialized] private string pendingLayerId = string.Empty;

        private IEnumerator<float> LoadWindow(bool connected)
        {
            if (!connected)
            {
                LoadGuarded(() => connected = ConnectSession());
                if (!connected)
                    yield break;
                yield return 0.1f;
            }
            loading.Label = "Indexing project types";
            LoadGuarded(() => _ = typeIndex.Types);
            yield return 0.3f;
            loading.Label = "Checking approvals";
            LoadGuarded(() => _ = repository.GetLayerApprovalStatuses());
            yield return 0.4f;
            if (windowMode == WindowMode.Master)
            {
                using var steps = ExplorerStatusSteps(explorerStatus, true);
                while (steps.MoveNext())
                    yield return 0.4f + 0.45f * steps.Current;
            }
            loading.Label = "Opening the layer";
            LoadGuarded(() =>
            {
                var layerId = pendingLayerId.Length > 0 ? pendingLayerId : activeLayerId;
                if (repository.FindLayer(layerId) != null)
                    GetLayerConformance(layerId);
            });
            yield return 1f;
        }

        private static void LoadGuarded(Action step)
        {
            try
            {
                step();
            }
            catch (ArchitectorDataException)
            {
            }
        }

        private void FinishLoading()
        {
            loaded = true;
            if (repository != null && pendingLayerId.Length > 0)
                OpenLayer(pendingLayerId);
            pendingLayerId = string.Empty;
            Repaint();
        }

        private void DrawLoading()
        {
            EnsureStyles();
            ArchitectorAiWorkUi.ConsumeInputWhenLocked();
            var area = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (Event.current.type != EventType.Repaint)
                return;
            var right = area;
            if (windowMode == WindowMode.Master)
            {
                var explorerWidth = ExplorerWidth + EditorStyles.helpBox.margin.horizontal;
                var strip = new Rect(area.x, area.y, explorerWidth, LayerTabsHeight);
                layerTabsBarStyle.Draw(strip, false, false, false, false);
                var margin = EditorStyles.helpBox.margin;
                EditorStyles.helpBox.Draw(new Rect(area.x + margin.left, strip.yMax + margin.top, ExplorerWidth, area.height - LayerTabsHeight - margin.vertical),
                    false, false, false, false);
                right = new Rect(area.x + explorerWidth, area.y, area.width - explorerWidth, area.height);
            }
            layerTabsBarStyle.Draw(new Rect(right.x, right.y, right.width, LayerTabsHeight), false, false, false, false);
            var barWidth = Mathf.Min(320f, right.width - 40f);
            var barRect = new Rect(right.center.x - barWidth * 0.5f, right.y + LayerTabsHeight + (right.height - LayerTabsHeight) * 0.5f - 10f, barWidth, 20f);
            loading.DrawBar(barRect);
            GUI.Label(new Rect(barRect.x, barRect.y - 22f, barRect.width, 18f), "Loading Architector", EditorStyles.centeredGreyMiniLabel);
        }

        private bool ConnectSession()
        {
            if (repository != null)
                return true;
            if (!ArchitectorSession.TryGet(out var session, out loadError))
                return false;
            repository = session.Repository;
            userState = session.UserState;
            nodeClipboard = session.NodeClipboard;
            typeIndex = session.TypeIndex;
            aiWork = session.AiWork;
            repository.Changed += OnRepositoryChanged;
            repository.ExternalChangesReloaded += OnExternalChangesReloaded;
            aiWork.Changed += OnAiWorkChanged;
            EnsureSelectionExists();
            EnsureLayerNavigationState();
            InitializeRequestedWorkspaceWindow();
            UpdateWindowTitle();
            return true;
        }

        private void OnDisable()
        {
            loading?.Cancel();
            explorerStatusWork?.Cancel();
            explorerStatusRevision = -1;
            EditorApplication.update -= StepLayerConformance;
            conformanceRequestLayerId = string.Empty;
            FinishArrange(false);
            layerCanvas?.CancelRouting();
            CommitRename();
            PersistNavigation();
            if (repository != null)
            {
                repository.Changed -= OnRepositoryChanged;
                repository.ExternalChangesReloaded -= OnExternalChangesReloaded;
            }
            if (aiWork != null)
                aiWork.Changed -= OnAiWorkChanged;
        }

        private void OnFocus()
        {
            repository?.RefreshExternalChanges();
        }

        private void OnLostFocus()
        {
            deferInspectorSelection = false;
            inspectorPointerDown = false;
            inspectorArranging = false;
            CommitRename();
        }

        private void OnExternalChangesReloaded(string summary)
        {
            layerCanvas?.CancelInteraction();
            ShowNotification(new GUIContent(summary));
            Repaint();
        }

        private void ConfigureAsWorkspaceWindow(IReadOnlyCollection<string> layerIds)
        {
            windowMode = WindowMode.Layer;
            workspaceViewId = $"layer-window-{Guid.NewGuid():N}";
            layerCanvas = new ArchitectorLayerCanvas(workspaceViewId);
            requestedWorkspaceLayerIds = layerIds?.Where(layerId => !string.IsNullOrEmpty(layerId)).Distinct(StringComparer.Ordinal).ToList()
                                        ?? new List<string>();
            initializeWorkspaceWindow = true;
            InitializeRequestedWorkspaceWindow();
            UpdateWindowTitle();
        }

        private void InitializeRequestedWorkspaceWindow()
        {
            if (windowMode != WindowMode.Layer || !initializeWorkspaceWindow || repository == null)
                return;
            OpenLayersAsTabs(requestedWorkspaceLayerIds, requestedWorkspaceLayerIds.FirstOrDefault());
            initializeWorkspaceWindow = false;
            requestedWorkspaceLayerIds.Clear();
        }

        private void EnsureWorkspaceCanvas()
        {
            if (string.IsNullOrEmpty(workspaceViewId))
                workspaceViewId = windowMode == WindowMode.Master ? "master" : $"layer-window-{Guid.NewGuid():N}";
            layerCanvas ??= new ArchitectorLayerCanvas(workspaceViewId);
        }

        private void UpdateWindowTitle()
        {
            if (windowMode == WindowMode.Master)
            {
                titleContent = new GUIContent("Architector");
                return;
            }
            var layerName = repository?.FindLayer(activeLayerId)?.name;
            titleContent = new GUIContent(string.IsNullOrEmpty(layerName) ? "Architector Workspace" : $"Architector - {layerName}");
        }

        private void OnGUI()
        {
            EnsureWorkspaceCanvas();
            UpdateInspectorPointer(Event.current);
            if (windowMode == WindowMode.Master)
                DrawHeader();
            else
                DrawWorkspaceAiHeader();
            if (!loaded)
            {
                DrawLoading();
                return;
            }
            if (repository == null)
            {
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
                if (GUILayout.Button("Retry"))
                    OnEnable();
                return;
            }

            if (Arranging)
                HandleArrangeBarInput();
            var aiLocked = aiWork?.WorkspacesLocked == true;
            if (aiLocked)
                ArchitectorAiWorkUi.ConsumeInputWhenLocked();
            else if (!Arranging)
            {
                HandleUndoRedoShortcut();
                HandleExplorerDeleteShortcut();
            }
            if (Event.current.type == EventType.MouseMove)
                Repaint();
            currentDrawDataError = string.Empty;
            try
            {
                using (new EditorGUI.DisabledScope(aiLocked || Arranging))
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (windowMode == WindowMode.Master)
                        DrawExplorer();
                    DrawWorkspace();
                    DrawNodeInspector();
                }
            }
            catch (ArchitectorDataException exception)
            {
                layerCanvas.CancelInteraction();
                currentDrawDataError = exception.Message;
            }
            if (!string.IsNullOrEmpty(repository.ExternalChangeError))
                DrawDataError(
                    "Architector files on disk cannot be loaded. The last valid architecture stays open read-only until the files are repaired.",
                    repository.ExternalChangeError);
            else if (!string.IsNullOrEmpty(currentDrawDataError))
                DrawDataError(
                    "Architector data could not be loaded. Select another layer, restore the referenced canonical document, or delete the broken layer through the Explorer.",
                    currentDrawDataError);
            else if (!aiLocked)
                SchedulePendingContextMenu();
            if (Arranging)
                PaintArrangeBar();
            if (Event.current.type == EventType.Repaint)
                PersistNavigation();
        }

        private void PersistNavigation()
        {
            if (windowMode != WindowMode.Master || userState == null)
                return;
            if (userState.MatchesMasterNavigation(
                    currentGroupId ?? string.Empty,
                    activeLayerId ?? string.Empty,
                    openLayerIds,
                    layerVisitHistory,
                    layerVisitHistoryIndex,
                    layerTabsScroll))
                return;
            userState.SaveMasterNavigation(new ArchitectorWindowNavigationRecord
            {
                currentGroupId = currentGroupId ?? string.Empty,
                activeLayerId = activeLayerId ?? string.Empty,
                openLayerIds = new List<string>(openLayerIds ?? new List<string>()),
                layerVisitHistory = new List<string>(layerVisitHistory ?? new List<string>()),
                layerVisitHistoryIndex = layerVisitHistoryIndex,
                layerTabsScroll = layerTabsScroll
            });
        }

        private void RestoreNavigation()
        {
            if (userState == null || repository == null)
                return;
            var navigation = userState.GetMasterNavigation();
            openLayerIds = navigation.openLayerIds ?? new List<string>();
            layerVisitHistory = navigation.layerVisitHistory ?? new List<string>();
            layerVisitHistoryIndex = navigation.layerVisitHistoryIndex;
            activeLayerId = navigation.activeLayerId ?? string.Empty;
            layerTabsScroll = navigation.layerTabsScroll;
            EnsureLayerNavigationState();
            if (!string.IsNullOrEmpty(activeLayerId))
                SelectActiveLayerInExplorer(activeLayerId);
            var groupId = navigation.currentGroupId ?? string.Empty;
            currentGroupId = groupId.Length == 0 || repository.FindGroup(groupId) != null ? groupId : currentGroupId;
            scrollActiveLayerIntoView = true;
            UpdateWindowTitle();
        }

        private void DrawLayerScope(ArchitectorLayerRecord layer)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Code Scope",
                    "Folders and namespaces whose types belong to this layer. The dependency evidence tool checks code dependencies between scoped layers. Not part of the approved contract."),
                EditorStyles.miniBoldLabel);
            var scope = layer.scope ?? new ArchitectorLayerScopeRecord();
            var folders = string.Join("; ", scope.folders);
            var namespaces = string.Join("; ", scope.namespaces);
            var nextFolders = EditorGUILayout.DelayedTextField(new GUIContent("Folders / Files", "Separate entries with ';'. A folder covers everything below it; a file path assigns one type file. The most specific entry wins."), folders);
            var nextNamespaces = EditorGUILayout.DelayedTextField(new GUIContent("Namespaces", "Separate entries with ';'."), namespaces);
            if (!string.Equals(nextFolders, folders, StringComparison.Ordinal)
                || !string.Equals(nextNamespaces, namespaces, StringComparison.Ordinal))
                Run(() => repository.UpdateLayerScope(layer.id, nextFolders.Split(';'), nextNamespaces.Split(';')));
        }

        private void DrawBaselineChanges(string layerId)
        {
            if (!string.Equals(baselineChangesLayerId, layerId, StringComparison.Ordinal)
                || baselineChangesRevision != repository.Revision)
            {
                baselineChanges = repository.GetBaselineChanges(layerId);
                baselineChangesLayerId = layerId;
                baselineChangesRevision = repository.Revision;
            }
            baselineChangesExpanded = EditorGUILayout.Foldout(
                baselineChangesExpanded,
                $"Changes since approval ({baselineChanges.Count})",
                true);
            if (!baselineChangesExpanded)
                return;
            if (baselineChanges.Count == 0)
            {
                EditorGUILayout.LabelField("Only names of related layers or nodes changed.", EditorStyles.wordWrappedMiniLabel);
                return;
            }
            baselineChangeStyle ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            baselineChangeStyle.richText = true;
            baselineChangeStyle.wordWrap = true;
            var palette = EditorGUIUtility.isProSkin ? repository.Settings.palette.dark : repository.Settings.palette.light;
            baselineChangesScroll = EditorGUILayout.BeginScrollView(
                baselineChangesScroll,
                false,
                false,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUIStyle.none,
                GUILayout.MaxHeight(Mathf.Max(80f, position.height * 0.35f)));
            var rowWidth = ArchitectorNodeInspector.Width - 12f - GUI.skin.verticalScrollbar.fixedWidth - 6f;
            foreach (var change in baselineChanges)
            {
                GUILayout.Label(FormatBaselineChange(change, palette), baselineChangeStyle, GUILayout.Width(rowWidth));
                // A row opens what changed; a removal or a change of the layer itself has nothing on the canvas to show.
                var revealable = !string.Equals(change.Change, ArchitectorBaselineChanges.Removed, StringComparison.Ordinal)
                                 && change.Subject is "node" or "portal" or "relationship";
                if (!revealable)
                    continue;
                var rowRect = GUILayoutUtility.GetLastRect();
                EditorGUIUtility.AddCursorRect(rowRect, MouseCursor.Link);
                if (Event.current.type == EventType.Repaint && rowRect.Contains(Event.current.mousePosition))
                {
                    var hover = GUI.skin.settings.selectionColor;
                    hover.a = Mathf.Min(hover.a, 0.2f);
                    EditorGUI.DrawRect(rowRect, hover);
                }
                if (Event.current.type == EventType.MouseUp && Event.current.button == 0 && rowRect.Contains(Event.current.mousePosition))
                {
                    if (!string.Equals(activeLayerId, layerId, StringComparison.Ordinal))
                        OpenLayer(layerId);
                    layerCanvas.RevealChange(change.Subject, change.Id);
                    Event.current.Use();
                    Repaint();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static string FormatBaselineChange(ArchitectorBaselineChange change, ArchitectorThemePaletteSettings palette)
        {
            var dark = EditorGUIUtility.isProSkin;
            var (prefix, prefixColor) = change.Change switch
            {
                ArchitectorBaselineChanges.Added => ("+", dark ? new Color(0.42f, 0.84f, 0.48f) : new Color(0.08f, 0.48f, 0.17f)),
                ArchitectorBaselineChanges.Removed => ("−", dark ? new Color(1.00f, 0.42f, 0.36f) : new Color(0.72f, 0.13f, 0.10f)),
                _ => ("~", dark ? new Color(0.36f, 0.72f, 1.00f) : new Color(0.04f, 0.36f, 0.72f))
            };
            var caption = change.Caption ?? string.Empty;
            var separator = caption.IndexOf(": ", StringComparison.Ordinal);
            var details = separator < 0 ? string.Empty : caption.Substring(separator + 2);
            var title = change.Subject switch
            {
                "node" => "Node",
                "portal" => "Portal",
                _ => separator < 0 ? caption.Replace(" changed", string.Empty) : "Name"
            };
            var titleColor = EditorStyles.label.normal.textColor;
            if (string.Equals(change.Subject, "relationship", StringComparison.Ordinal))
            {
                var open = details.LastIndexOf(" (", StringComparison.Ordinal);
                if (open >= 0 && details.EndsWith(")", StringComparison.Ordinal))
                {
                    title = details.Substring(open + 2, details.Length - open - 3);
                    titleColor = RelationshipKindColor(title, palette);
                    details = details.Substring(0, open);
                }
            }
            var text = $"<color=#{ColorUtility.ToHtmlStringRGB(prefixColor)}>{prefix}</color> <b><color=#{ColorUtility.ToHtmlStringRGB(titleColor)}>{EscapeRichText(title)}{(details.Length > 0 ? ":" : string.Empty)}</color></b>";
            if (details.Length > 0)
                text += $" <i><color=#{ColorUtility.ToHtmlStringRGB(dark ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.40f, 0.40f, 0.40f))}>{EscapeRichText(details)}</color></i>";
            return text;
        }

        private static Color RelationshipKindColor(string displayName, ArchitectorThemePaletteSettings palette)
        {
            var kind = ArchitectorEdgeKinds.All.FirstOrDefault(candidate =>
                string.Equals(ArchitectorEdgeKinds.DisplayName(candidate), displayName, StringComparison.Ordinal));
            var color = kind switch
            {
                ArchitectorEdgeKinds.DataFlow => palette.dataFlowEdge,
                ArchitectorEdgeKinds.Event => palette.eventEdge,
                ArchitectorEdgeKinds.Callback => palette.callbackEdge,
                _ => palette.edge
            };
            return color.ToColor();
        }

        private static string EscapeRichText(string value) => value.Replace("<", "<​");

        // After loading, statuses are rebuilt quietly in the background, one layer per step: the old marks stay on
        // screen until the new ones are complete, so an edit never waits for a check of the whole project.
        [NonSerialized] private ArchitectorDeferredWork explorerStatusWork;

        private ArchitectorExplorerEntryStatus GetExplorerStatus(string id)
        {
            if (explorerStatusRevision != repository.SemanticRevision || explorerStatusTypeGeneration != typeIndex.Generation)
            {
                explorerStatusRevision = repository.SemanticRevision;
                explorerStatusTypeGeneration = typeIndex.Generation;
                explorerStatusWork ??= new ArchitectorDeferredWork(this);
                explorerStatusWork.Run("Checking layers", RefreshExplorerStatus(), Repaint, true);
            }
            return explorerStatus.TryGetValue(id, out var status) ? status : ArchitectorExplorerEntryStatus.None;
        }

        private IEnumerator<float> RefreshExplorerStatus()
        {
            var next = new Dictionary<string, ArchitectorExplorerEntryStatus>(StringComparer.Ordinal);
            using (var steps = ExplorerStatusSteps(next, false))
                while (steps.MoveNext())
                    yield return steps.Current;
            explorerStatus.Clear();
            foreach (var pair in next)
                explorerStatus[pair.Key] = pair.Value;
        }

        // Builds the statuses into target one layer per step and yields the progress after each layer.
        private IEnumerator<float> ExplorerStatusSteps(Dictionary<string, ArchitectorExplorerEntryStatus> target, bool showProgress)
        {
            List<string> healthy = null;
            LoadGuarded(() => healthy = BeginExplorerStatus(target));
            if (healthy == null)
                yield break;
            for (var index = 0; index < healthy.Count; index++)
            {
                if (showProgress)
                    loading.Label = $"Checking layers {index + 1} / {healthy.Count}";
                var layerId = healthy[index];
                if (repository.FindLayer(layerId) != null)
                    LoadGuarded(() => MarkLayerErrors(target, layerId));
                yield return (index + 1f) / healthy.Count;
            }
        }

        // Marks load errors and changed layers; returns the layers whose conformance is still to check, none when
        // any layer failed to load.
        private List<string> BeginExplorerStatus(Dictionary<string, ArchitectorExplorerEntryStatus> target)
        {
            explorerStatusRevision = repository.SemanticRevision;
            explorerStatusTypeGeneration = typeIndex.Generation;
            target.Clear();
            var healthy = new List<string>();
            foreach (var layer in repository.Project.layers)
            {
                if (repository.HasLayerLoadError(layer.id))
                    MarkExplorerStatus(target, layer.id, layer.parentGroupId, ArchitectorExplorerEntryStatus.Error);
                else
                    healthy.Add(layer.id);
            }
            if (healthy.Count != repository.Project.layers.Count)
                return new List<string>();
            foreach (var layerId in healthy)
                if (string.Equals(repository.GetLayerApprovalStatus(layerId).State, ArchitectorApprovalStates.Changed, StringComparison.Ordinal))
                    MarkExplorerStatus(target, layerId, repository.FindLayer(layerId).parentGroupId, ArchitectorExplorerEntryStatus.Changed);
            return healthy;
        }

        private void MarkLayerErrors(Dictionary<string, ArchitectorExplorerEntryStatus> target, string layerId)
        {
            var conformance = ArchitectorConformance.Evaluate(repository, typeIndex, new[] { layerId }, int.MaxValue);
            foreach (var diagnostic in conformance.Diagnostics)
                if (string.Equals(diagnostic.Severity, ArchitectorConformanceSeverities.Error, StringComparison.Ordinal))
                    MarkExplorerStatus(target, diagnostic.LayerId, repository.FindLayer(diagnostic.LayerId)?.parentGroupId, ArchitectorExplorerEntryStatus.Error);
        }

        private void MarkExplorerStatus(Dictionary<string, ArchitectorExplorerEntryStatus> target, string layerId, string groupId, ArchitectorExplorerEntryStatus status)
        {
            target.TryGetValue(layerId, out var current);
            target[layerId] = current | status;
            while (!string.IsNullOrEmpty(groupId))
            {
                target.TryGetValue(groupId, out var groupStatus);
                target[groupId] = groupStatus | status;
                groupId = repository.FindGroup(groupId)?.parentGroupId;
            }
        }

        private float DrawExplorerStatus(Rect rect, float right, string id, bool isGroup)
        {
            var status = GetExplorerStatus(id);
            var scope = isGroup ? "inside this group" : "in this layer";
            if (repository.IsUnchecked(id))
            {
                right -= 18f;
                var owner = repository.FindGroup(repository.FindUncheckedGroup(id))?.name ?? string.Empty;
                GUI.Label(
                    new Rect(right, rect.center.y - 8f, 16f, 16f),
                    new GUIContent(ArchitectorIcons.Get("TestIgnored").image,
                        $"Checks are off (group '{owner}'): conformance skips these layers and they cannot be approved."));
            }
            if ((status & ArchitectorExplorerEntryStatus.Changed) != 0)
            {
                right -= 18f;
                GUI.Label(
                    new Rect(right, rect.center.y - 8f, 16f, 16f),
                    new GUIContent(ArchitectorIcons.Get("console.warnicon.sml").image, $"Changes since approval {scope}."));
            }
            if ((status & ArchitectorExplorerEntryStatus.Error) != 0)
            {
                right -= 18f;
                GUI.Label(
                    new Rect(right, rect.center.y - 8f, 16f, 16f),
                    new GUIContent(ArchitectorIcons.Get("console.erroricon.sml").image, $"Conformance errors or an invalid layer document {scope}."));
            }
            return right;
        }

        private void DrawDataError(string explanation, string message)
        {
            pendingContextMenu = null;
            pendingContextMenuNeedsRepaint = false;
            var text = $"{explanation}\n\n{message}";
            var left = windowMode == WindowMode.Master ? ExplorerWidth + 8f : 8f;
            var top = (windowMode == WindowMode.Master ? HeaderHeight : 0f) + 42f;
            var width = Mathf.Max(160f, position.width - left - 8f);
            var height = Mathf.Clamp(EditorStyles.helpBox.CalcHeight(new GUIContent(text), width), 64f, 160f);
            EditorGUI.HelpBox(new Rect(left, top, width, height), text, MessageType.Error);
        }

        private void DrawHeader()
        {
            EnsureStyles();
            var headerRect = new Rect(0f, 0f, position.width, HeaderHeight);
            var headerColor = EditorGUIUtility.isProSkin
                ? new Color(0.22f, 0.22f, 0.22f)
                : new Color(0.76f, 0.76f, 0.76f);
            var separatorColor = EditorGUIUtility.isProSkin
                ? new Color(0.12f, 0.12f, 0.12f)
                : new Color(0.56f, 0.56f, 0.56f);
            EditorGUI.DrawRect(headerRect, headerColor);
            EditorGUI.DrawRect(new Rect(headerRect.x, headerRect.yMax - 1f, headerRect.width, 1f), separatorColor);
            var versionRect = new Rect(headerRect.x + 6f, headerRect.y - 1f, 100f, headerRect.height);
            GUI.Label(versionRect, $"v{ArchitectorPackagePaths.Version}", versionStyle);
            // The bottom pixel is the separator, so the buttons sit in the 29 px above it with the same 3 px margin on
            // top, bottom and right, and the same gap between them.
            const float headerMargin = 3f;
            const float settingsSize = HeaderHeight - 1f - headerMargin * 2f;
            var settingsRect = new Rect(
                headerRect.xMax - settingsSize - headerMargin,
                headerRect.y + headerMargin,
                settingsSize,
                settingsSize);
            using (new EditorGUI.DisabledScope(aiWork?.WorkspacesLocked == true))
            {
                if (GUI.Button(settingsRect, ArchitectorIcons.Get("Settings"), GUI.skin.button))
                    ArchitectorSettingsWindow.Open();
            }
            var noticeRight = settingsRect.x;
            if (aiWork?.IsActive == true)
            {
                var aiButtonWidth = ArchitectorAiWorkUi.WorkspaceButtonWidth(aiWork);
                var aiRect = new Rect(
                    settingsRect.x - aiButtonWidth - headerMargin,
                    settingsRect.y,
                    aiButtonWidth,
                    settingsRect.height);
                ArchitectorAiWorkUi.DrawWorkspaceButton(aiRect, aiWork);
                noticeRight = aiRect.x;
            }
            var mode = ArchitectorAgentPreferences.Current.Mode;
            if (mode.ShowsNotice)
            {
                var noticeWidth = ArchitectorAiWorkUi.WorkflowNoticeWidth(mode);
                ArchitectorAiWorkUi.DrawWorkflowNotice(
                    new Rect(noticeRight - noticeWidth - headerMargin, settingsRect.y, noticeWidth, settingsRect.height),
                    mode);
            }

            GUILayout.Space(HeaderHeight);
        }

        private void DrawWorkspaceAiHeader()
        {
            var mode = ArchitectorAgentPreferences.Current.Mode;
            var aiActive = aiWork?.IsActive == true;
            if (!aiActive && !mode.ShowsNotice)
                return;
            var rect = GUILayoutUtility.GetRect(0f, ArchitectorAiWorkUi.StatusHeight, GUILayout.ExpandWidth(true));
            var headerColor = EditorGUIUtility.isProSkin
                ? new Color(0.22f, 0.22f, 0.22f)
                : new Color(0.76f, 0.76f, 0.76f);
            EditorGUI.DrawRect(rect, headerColor);
            var noticeRight = rect.xMax;
            if (aiActive)
            {
                var aiButtonWidth = ArchitectorAiWorkUi.WorkspaceButtonWidth(aiWork);
                var buttonRect = new Rect(
                    rect.xMax - aiButtonWidth - 4f,
                    rect.y + 3f,
                    aiButtonWidth,
                    rect.height - 6f);
                ArchitectorAiWorkUi.DrawWorkspaceButton(buttonRect, aiWork);
                noticeRight = buttonRect.x;
            }
            if (mode.ShowsNotice)
            {
                var noticeWidth = ArchitectorAiWorkUi.WorkflowNoticeWidth(mode);
                ArchitectorAiWorkUi.DrawWorkflowNotice(
                    new Rect(noticeRight - noticeWidth - 4f, rect.y + 3f, noticeWidth, rect.height - 6f),
                    mode);
            }
        }

        private void DrawExplorer()
        {
            if (Event.current.type == EventType.DragExited)
                ClearExplorerDropFeedback();
            else if (Event.current.type == EventType.DragUpdated)
            {
                explorerDropIndicatorRect = default;
                explorerDropIntoGroupId = string.Empty;
                explorerDropOnBack = false;
            }
            EnsureStyles();
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(ExplorerWidth + EditorStyles.helpBox.margin.horizontal), GUILayout.ExpandHeight(true)))
            {
                DrawQuickTools();
                DrawExplorerBox();
            }
        }

        private void DrawQuickTools()
        {
            var stripRect = GUILayoutUtility.GetRect(0f, LayerTabsHeight, GUILayout.ExpandWidth(true));
            GUI.Box(stripRect, GUIContent.none, layerTabsBarStyle);
            var size = stripRect.height - 6f;
            var workspaceRect = new Rect(stripRect.x + 3f, stripRect.y + 3f, size, size);
            DrawNewWorkspaceButton(workspaceRect);
            var reviewRect = new Rect(workspaceRect.xMax + 3f, workspaceRect.y, size, size);
            DrawReviewButton(reviewRect, ArchitectorReviewWindow.PendingCount(repository));
        }

        private void DrawNewWorkspaceButton(Rect rect)
        {
            if (GUI.Button(rect, new GUIContent(string.Empty, "Open an empty Architector Workspace Window"), layerTabNavigationStyle))
                OpenEmptyWorkspaceWindow();
            DrawCenteredIcon(rect, ArchitectorIcons.Get("layout@2x").image, 20f);
        }

        private void DrawReviewButton(Rect rect, int pending)
        {
            var tooltip = pending > 0
                ? $"Open Architector Review: {pending} layer(s) changed since approval, approved by an agent or in draft."
                : "Open Architector Review: every layer is approved.";
            if (GUI.Button(rect, new GUIContent(string.Empty, tooltip), layerTabNavigationStyle))
                EditorApplication.delayCall += ArchitectorReviewWindow.Open;
            DrawCenteredIcon(rect, ArchitectorIcons.Get("VisibilityOn").image, 16f, pending > 0 ? -3f : 0f);
            if (pending <= 0 || Event.current.type != EventType.Repaint)
                return;
            var text = pending > 99 ? "99+" : pending.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var countRect = new Rect(rect.x, rect.yMax - 13f, rect.width - 2f, 12f);
            var outline = EditorGUIUtility.isProSkin ? new Color(0.08f, 0.08f, 0.08f) : new Color(0.15f, 0.12f, 0.05f);
            reviewCountStyle.normal.textColor = outline;
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    if (x != 0 || y != 0)
                        reviewCountStyle.Draw(new Rect(countRect.x + x, countRect.y + y, countRect.width, countRect.height), text, false, false, false, false);
            reviewCountStyle.normal.textColor = GUI.enabled ? new Color(1f, 0.78f, 0.25f) : new Color(1f, 0.78f, 0.25f, 0.45f);
            reviewCountStyle.Draw(countRect, text, false, false, false, false);
        }

        private static void DrawCenteredIcon(Rect rect, Texture icon, float iconSize, float offsetY = 0f)
        {
            if (icon == null || Event.current.type != EventType.Repaint)
                return;
            var previous = GUI.color;
            if (!GUI.enabled)
                GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * 0.45f);
            GUI.DrawTexture(new Rect(rect.center.x - iconSize * 0.5f, rect.center.y - iconSize * 0.5f + offsetY, iconSize, iconSize), icon, ScaleMode.ScaleToFit, true);
            GUI.color = previous;
        }

        private void DrawExplorerBox()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(ExplorerWidth), GUILayout.ExpandHeight(true)))
            {
                var navigationRect = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
                var breadcrumbX = navigationRect.x;
                if (!string.IsNullOrEmpty(currentGroupId))
                {
                    var backRect = new Rect(navigationRect.x, navigationRect.y + 1f, 28f, navigationRect.height - 2f);
                    if (GUI.Button(backRect, "<", EditorStyles.miniButton))
                        NavigateBack();
                    HandleExplorerBackDrop(backRect);
                    if (explorerDropOnBack && Event.current.type == EventType.Repaint)
                        DrawDropBorder(backRect);
                    breadcrumbX = backRect.xMax + 6f;
                }
                var pathRight = navigationRect.xMax;
                var pathRect = new Rect(breadcrumbX, navigationRect.y, Mathf.Max(0f, pathRight - breadcrumbX), navigationRect.height);
                GUI.Label(pathRect, CurrentLocationName(), breadcrumbStyle);

                var searchRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                searchRect.x += 2f;
                search = EditorGUI.TextField(searchRect, search, EditorStyles.toolbarSearchField);
                explorerScroll = EditorGUILayout.BeginScrollView(explorerScroll);
                var contentStart = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true));
                if (string.IsNullOrWhiteSpace(search))
                    DrawCurrentGroupEntries();
                else
                    DrawSearchResults();
                var contentEnd = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true));
                var contentRect = new Rect(0f, contentStart.y, ExplorerWidth, Mathf.Max(position.height, contentEnd.y - contentStart.y));
                HandleEmptyExplorerDrop(contentRect, contentEnd.y);
                HandleEmptyExplorerSelection(contentRect);
                HandleEmptyExplorerContext(contentRect);
                DrawExplorerDropIndicator();
                EditorGUILayout.EndScrollView();

                DrawUndoToolbar();
            }
        }

        private void DrawCurrentGroupEntries()
        {
            var groupIds = string.IsNullOrEmpty(currentGroupId)
                ? repository.Project.rootGroupIds
                : repository.FindGroup(currentGroupId)?.childGroupIds ?? new List<string>();
            var layerIds = string.IsNullOrEmpty(currentGroupId)
                ? repository.Project.rootLayerIds
                : repository.FindGroup(currentGroupId)?.layerIds ?? new List<string>();

            foreach (var id in groupIds.ToArray())
            {
                var group = repository.FindGroup(id);
                if (group != null)
                    DrawEntry(group.id, group.name, true, Array.IndexOf(groupIds.ToArray(), id));
            }
            foreach (var id in layerIds.ToArray())
            {
                var layer = repository.FindLayer(id);
                if (layer != null)
                    DrawEntry(layer.id, layer.name, false, Array.IndexOf(layerIds.ToArray(), id));
            }
        }

        private void DrawSearchResults()
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            foreach (var group in repository.Project.groups.Where(group => group.name.IndexOf(search, comparison) >= 0))
                DrawEntry(group.id, group.name, true, -1);
            var cacheKey = $"{repository.Revision}\n{search}";
            if (!string.Equals(explorerSearchCacheKey, cacheKey, StringComparison.Ordinal))
            {
                explorerSearchCacheKey = cacheKey;
                explorerSearchLayerIds = repository.Project.layers.Where(LayerMatchesSearch).Select(layer => layer.id).ToArray();
            }
            foreach (var layerId in explorerSearchLayerIds)
            {
                var layer = repository.FindLayer(layerId);
                if (layer != null)
                    DrawEntry(layer.id, layer.name, false, -1);
            }
        }

        private bool LayerMatchesSearch(ArchitectorLayerRecord layer)
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            if (layer.name.IndexOf(search, comparison) >= 0 || (layer.purpose ?? string.Empty).IndexOf(search, comparison) >= 0)
                return true;
            try
            {
                return repository.LoadLayer(layer.id).semantic.nodes.Any(node =>
                    node.title.IndexOf(search, comparison) >= 0
                    || (node.type?.name ?? string.Empty).IndexOf(search, comparison) >= 0
                    || (node.type?.typeNamespace ?? string.Empty).IndexOf(search, comparison) >= 0
                    || FullTypeName(node.type).IndexOf(search, comparison) >= 0
                    || ArchitectorTypeReference.FullName(node.type?.baseType).IndexOf(search, comparison) >= 0
                    || (node.type?.interfaces ?? new List<ArchitectorTypeReferenceRecord>()).Any(reference =>
                        ArchitectorTypeReference.FullName(reference).IndexOf(search, comparison) >= 0));
            }
            catch (ArchitectorDataException exception)
            {
                if (string.IsNullOrEmpty(currentDrawDataError))
                    currentDrawDataError = exception.Message;
                return false;
            }
        }

        private static string FullTypeName(ArchitectorTypeRecord type)
        {
            if (type == null || string.IsNullOrEmpty(type.name))
                return string.Empty;
            return string.IsNullOrEmpty(type.typeNamespace) ? type.name : $"{type.typeNamespace}.{type.name}";
        }

        private void DrawEntry(string id, string name, bool isGroup, int siblingIndex)
        {
            if (renameEntryId == id)
            {
                var renameRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                if (HandleRenameCompletion(renameRect))
                    return;
                GUI.SetNextControlName("ArchitectorRenameField");
                renameValue = EditorGUI.TextField(renameRect, renameValue);
                if (renameNeedsFocus && Event.current.type == EventType.Repaint)
                {
                    EditorGUI.FocusTextInControl("ArchitectorRenameField");
                    renameNeedsFocus = false;
                }
                return;
            }

            var selected = selectedExplorerEntryIds.Contains(id);
            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            var hovered = rect.Contains(Event.current.mousePosition);
            if (selected)
                EditorGUI.DrawRect(rect, GUI.skin.settings.selectionColor);
            else if (hovered)
            {
                var hoverColor = GUI.skin.settings.selectionColor;
                hoverColor.a = Mathf.Min(hoverColor.a, 0.22f);
                EditorGUI.DrawRect(rect, hoverColor);
            }
            if (string.Equals(explorerDropIntoGroupId, id, StringComparison.Ordinal))
            {
                var dropColor = GUI.skin.settings.selectionColor;
                dropColor.a = 0.35f;
                EditorGUI.DrawRect(rect, dropColor);
            }

            if (isGroup)
            {
                var icon = ArchitectorIcons.Get("Folder Icon");
                GUI.Label(rect, new GUIContent(name, icon.image), selected ? EditorStyles.whiteLabel : EditorStyles.label);
                var right = rect.xMax - 2f;
                if (repository.IsGroupLocked(id))
                {
                    var unlocked = repository.IsGroupUnlockedForSession(id);
                    var lockIcon = ArchitectorIcons.Get(unlocked ? "LockIcon" : "LockIcon-On").image;
                    right -= 18f;
                    GUI.Label(
                        new Rect(right, rect.center.y - 8f, 16f, 16f),
                        new GUIContent(lockIcon, unlocked ? "Locked group, unlocked for this session" : repository.FindGroup(id).lockState.notice));
                }
                DrawExplorerStatus(rect, right, id, true);
            }
            else
            {
                var iconRect = new Rect(rect.x + 3f, rect.center.y - 8f, 16f, 16f);
                DrawLayerIconAsset(iconRect, selected ? Color.white : EditorStyles.label.normal.textColor);
                var labelRect = new Rect(rect.x + 22f, rect.y, rect.width - 22f, rect.height);
                GUI.Label(labelRect, name, selected ? EditorStyles.whiteLabel : EditorStyles.label);
                DrawExplorerStatus(rect, rect.xMax - 2f, id, false);
            }

            HandleExplorerEntryPointer(rect, id, isGroup);
            HandleExplorerEntryDrop(rect, id, isGroup, siblingIndex);

            if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
            {
                layerCanvas.SelectOnly(string.Empty);
                if (!selectedExplorerEntryIds.Contains(id))
                {
                    selectedExplorerEntryIds.Clear();
                    selectedExplorerEntryIds.Add(id);
                }
                explorerSelectionAnchorId = id;
                if (isGroup)
                    ShowGroupMenu(id);
                else
                    ShowLayerMenu(id);
                Event.current.Use();
            }
        }

        private void HandleExplorerEntryPointer(Rect rect, string id, bool isGroup)
        {
            var current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                layerCanvas.SelectOnly(string.Empty);
                explorerKeyboardFocus = true;
                stepPreviewLayerId = string.Empty;
                var additive = current.control || current.command;
                var rangeSelection = current.shift;
                pressedExplorerCollapseSelection = false;
                if (rangeSelection)
                {
                    SelectExplorerRange(id, additive);
                }
                else if (additive)
                {
                    if (!selectedExplorerEntryIds.Add(id))
                        selectedExplorerEntryIds.Remove(id);
                    explorerSelectionAnchorId = id;
                }
                else if (!selectedExplorerEntryIds.Contains(id))
                {
                    selectedExplorerEntryIds.Clear();
                    selectedExplorerEntryIds.Add(id);
                    explorerSelectionAnchorId = id;
                }
                else
                {
                    pressedExplorerCollapseSelection = selectedExplorerEntryIds.Count > 1;
                    explorerSelectionAnchorId = id;
                }

                pressedExplorerEntryId = selectedExplorerEntryIds.Contains(id) ? id : string.Empty;
                pressedExplorerPosition = current.mousePosition;
                pressedExplorerShouldOpen = !additive && !rangeSelection;
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDrag
                && current.button == 0
                && string.Equals(pressedExplorerEntryId, id, StringComparison.Ordinal)
                && (current.mousePosition - pressedExplorerPosition).sqrMagnitude >= 16f)
            {
                BeginExplorerDrag(id);
                pressedExplorerEntryId = string.Empty;
                current.Use();
                return;
            }

            if (current.type != EventType.MouseUp
                || current.button != 0
                || !string.Equals(pressedExplorerEntryId, id, StringComparison.Ordinal))
            {
                return;
            }

            pressedExplorerEntryId = string.Empty;
            if (!rect.Contains(current.mousePosition))
            {
                current.Use();
                return;
            }

            if (pressedExplorerCollapseSelection)
            {
                selectedExplorerEntryIds.Clear();
                selectedExplorerEntryIds.Add(id);
            }
            if (pressedExplorerShouldOpen)
            {
                if (isGroup)
                {
                    currentGroupId = id;
                    search = string.Empty;
                    selectedExplorerEntryIds.Clear();
                    explorerSelectionAnchorId = string.Empty;
                }
                else
                {
                    OpenLayer(id);
                }
            }
            pressedExplorerCollapseSelection = false;
            current.Use();
            Repaint();
        }

        private bool BeginExplorerDrag(string pressedId)
        {
            if (!string.IsNullOrWhiteSpace(search))
            {
                ShowNotification(new GUIContent("Explorer drag-and-drop is disabled while search results are shown."));
                return false;
            }

            if (!selectedExplorerEntryIds.Contains(pressedId))
            {
                selectedExplorerEntryIds.Clear();
                selectedExplorerEntryIds.Add(pressedId);
            }
            var orderedIds = CurrentExplorerEntryOrder().Where(selectedExplorerEntryIds.Contains).ToArray();
            if (orderedIds.Length == 0)
                return false;

            var payload = new ExplorerDragPayload(GetInstanceID(), orderedIds);
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.SetGenericData(ExplorerDragDataKey, payload);
            DragAndDrop.StartDrag(orderedIds.Length == 1 ? "Move Architector Entry" : $"Move {orderedIds.Length} Architector Entries");
            pressedExplorerEntryId = string.Empty;
            pressedExplorerCollapseSelection = false;
            return true;
        }

        private IEnumerable<string> CurrentExplorerEntryOrder()
        {
            var group = string.IsNullOrEmpty(currentGroupId) ? null : repository.FindGroup(currentGroupId);
            var groupIds = group?.childGroupIds ?? repository.Project.rootGroupIds;
            var layerIds = group?.layerIds ?? repository.Project.rootLayerIds;
            return groupIds.Concat(layerIds);
        }

        private IEnumerable<string> VisibleExplorerEntryOrder()
        {
            if (string.IsNullOrWhiteSpace(search))
                return CurrentExplorerEntryOrder();
            var comparison = StringComparison.OrdinalIgnoreCase;
            return repository.Project.groups
                .Where(group => group.name.IndexOf(search, comparison) >= 0)
                .Select(group => group.id)
                .Concat(repository.Project.layers
                    .Where(layer => layer.name.IndexOf(search, comparison) >= 0)
                    .Select(layer => layer.id));
        }

        private void SelectExplorerRange(string id, bool additive)
        {
            var visibleIds = VisibleExplorerEntryOrder().ToArray();
            var targetIndex = Array.IndexOf(visibleIds, id);
            var anchorIndex = Array.IndexOf(visibleIds, explorerSelectionAnchorId);
            if (targetIndex < 0)
                return;
            if (anchorIndex < 0)
            {
                anchorIndex = targetIndex;
                explorerSelectionAnchorId = id;
            }
            if (!additive)
                selectedExplorerEntryIds.Clear();
            var first = Mathf.Min(anchorIndex, targetIndex);
            var last = Mathf.Max(anchorIndex, targetIndex);
            for (var index = first; index <= last; index++)
                selectedExplorerEntryIds.Add(visibleIds[index]);
        }

        private void HandleExplorerEntryDrop(Rect rect, string id, bool isGroup, int siblingIndex)
        {
            var current = Event.current;
            if ((current.type != EventType.DragUpdated && current.type != EventType.DragPerform) || !rect.Contains(current.mousePosition))
                return;
            if (!TryGetExplorerDrag(out var payload))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                current.Use();
                return;
            }

            var containsGroups = payload.entryIds.Any(entryId => repository.FindGroup(entryId) != null);
            string targetParentId;
            int? groupInsertionIndex = null;
            int? layerInsertionIndex = null;
            var dropInsideGroup = false;
            var insertAfter = current.mousePosition.y >= rect.center.y;

            if (isGroup)
            {
                var normalizedY = Mathf.InverseLerp(rect.yMin, rect.yMax, current.mousePosition.y);
                dropInsideGroup = !containsGroups || normalizedY >= 0.25f && normalizedY <= 0.75f;
                if (dropInsideGroup)
                {
                    targetParentId = id;
                }
                else
                {
                    targetParentId = currentGroupId;
                    groupInsertionIndex = siblingIndex + (insertAfter ? 1 : 0);
                }
            }
            else
            {
                if (containsGroups)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                    ClearExplorerDropFeedback();
                    current.Use();
                    return;
                }
                targetParentId = currentGroupId;
                layerInsertionIndex = siblingIndex + (insertAfter ? 1 : 0);
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            if (dropInsideGroup)
            {
                explorerDropIntoGroupId = id;
                explorerDropIndicatorRect = default;
            }
            else
            {
                explorerDropIntoGroupId = string.Empty;
                explorerDropIndicatorRect = new Rect(rect.x + 2f, insertAfter ? rect.yMax - 1f : rect.yMin - 1f, rect.width - 4f, 2f);
            }
            Repaint();

            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                Run(() => repository.MoveEntries(payload.entryIds, targetParentId, groupInsertionIndex, layerInsertionIndex));
                selectedExplorerEntryIds.Clear();
                ClearExplorerDropFeedback();
            }
            current.Use();
        }

        private void HandleEmptyExplorerDrop(Rect contentRect, float contentEndY)
        {
            var current = Event.current;
            if ((current.type != EventType.DragUpdated && current.type != EventType.DragPerform) || !contentRect.Contains(current.mousePosition))
                return;
            if (!TryGetExplorerDrag(out var payload))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                current.Use();
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            explorerDropIntoGroupId = string.Empty;
            explorerDropIndicatorRect = new Rect(4f, contentEndY, ExplorerWidth - 12f, 2f);
            Repaint();
            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                Run(() => repository.MoveEntries(payload.entryIds, currentGroupId));
                selectedExplorerEntryIds.Clear();
                ClearExplorerDropFeedback();
            }
            current.Use();
        }

        private void HandleExplorerBackDrop(Rect backRect)
        {
            var current = Event.current;
            if ((current.type != EventType.DragUpdated && current.type != EventType.DragPerform) || !backRect.Contains(current.mousePosition))
                return;
            if (!TryGetExplorerDrag(out var payload))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                current.Use();
                return;
            }

            var parentGroupId = repository.FindGroup(currentGroupId)?.parentGroupId ?? string.Empty;
            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            explorerDropOnBack = true;
            explorerDropIntoGroupId = string.Empty;
            explorerDropIndicatorRect = default;
            Repaint();
            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                Run(() => repository.MoveEntries(payload.entryIds, parentGroupId));
                selectedExplorerEntryIds.Clear();
                explorerSelectionAnchorId = string.Empty;
                ClearExplorerDropFeedback();
            }
            current.Use();
        }

        private void HandleEmptyExplorerSelection(Rect contentRect)
        {
            var current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0 || !contentRect.Contains(current.mousePosition))
                return;
            selectedExplorerEntryIds.Clear();
            explorerSelectionAnchorId = string.Empty;
            pressedExplorerEntryId = string.Empty;
            current.Use();
            Repaint();
        }

        private bool TryGetExplorerDrag(out ExplorerDragPayload payload)
        {
            return TryGetExplorerReferenceDrag(out payload)
                   && payload.ownerWindowId == GetInstanceID()
                   && string.IsNullOrWhiteSpace(search);
        }

        private static bool TryGetExplorerReferenceDrag(out ExplorerDragPayload payload)
        {
            payload = DragAndDrop.GetGenericData(ExplorerDragDataKey) as ExplorerDragPayload;
            return payload != null && payload.entryIds.Length > 0;
        }

        private IReadOnlyList<string> ResolveExplorerLayerIds(ExplorerDragPayload payload)
        {
            return ResolveExplorerLayerIds(repository, payload);
        }

        private static List<string> ResolveExplorerLayerIds(ArchitectorRepository sourceRepository, ExplorerDragPayload payload)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entryId in payload.entryIds)
            {
                var layer = sourceRepository.FindLayer(entryId);
                if (layer != null)
                {
                    if (seen.Add(layer.id))
                        result.Add(layer.id);
                    continue;
                }

                var group = sourceRepository.FindGroup(entryId);
                if (group == null)
                    continue;
                foreach (var layerId in group.layerIds)
                {
                    if (sourceRepository.FindLayer(layerId) != null && seen.Add(layerId))
                        result.Add(layerId);
                }
            }
            return result;
        }

        private bool ExplorerDropNeedsConfirmation(ExplorerDragPayload payload)
        {
            return payload.entryIds.Any(entryId => repository.FindGroup(entryId) != null);
        }

        private void DrawExplorerDropIndicator()
        {
            if (explorerDropIndicatorRect.width <= 0f)
                return;
            EditorGUI.DrawRect(explorerDropIndicatorRect, GUI.skin.settings.selectionColor);
        }

        private void ClearExplorerDropFeedback()
        {
            explorerDropIndicatorRect = default;
            explorerDropIntoGroupId = string.Empty;
            explorerDropOnBack = false;
            Repaint();
        }

        private static void DrawDropBorder(Rect rect)
        {
            var color = GUI.skin.settings.selectionColor;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 2f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 2f, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), color);
        }

        private void DrawUndoToolbar()
        {
            // Leave a physical pixel above and below the native mini-button
            // borders. Drawing directly against the layout group's clipping
            // edge trims the upper outline on some editor DPI scales.
            var rowRect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            const float gap = 2f;
            var buttonsRect = new Rect(rowRect.x, rowRect.y + 3f, rowRect.width, rowRect.height - 2f);
            var buttonWidth = (buttonsRect.width - gap) * 0.5f;
            var leftRect = new Rect(buttonsRect.x, buttonsRect.y, buttonWidth, buttonsRect.height);
            var rightRect = new Rect(leftRect.xMax + gap, buttonsRect.y, buttonsRect.width - buttonWidth - gap, buttonsRect.height);
            using (new EditorGUI.DisabledScope(!repository.CanUndo))
            {
                var content = new GUIContent("Undo", repository.CanUndo ? $"Undo {repository.NextUndoName}" : "Nothing to undo");
                if (GUI.Button(leftRect, content, EditorStyles.miniButton))
                    Run(repository.Undo);
            }
            using (new EditorGUI.DisabledScope(!repository.CanRedo))
            {
                var content = new GUIContent("Redo", repository.CanRedo ? $"Redo {repository.NextRedoName}" : "Nothing to redo");
                if (GUI.Button(rightRect, content, EditorStyles.miniButton))
                    Run(repository.Redo);
            }
        }

        private void DrawLayerTabs()
        {
            EnsureLayerNavigationState();
            EnsureStyles();
            var toolbarRect = GUILayoutUtility.GetRect(0f, LayerTabsHeight, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                lastTabsRect = toolbarRect;
            if (Event.current.type == EventType.MouseLeaveWindow)
                tabsHavePointer = false;
            else if (Event.current.isMouse || Event.current.type == EventType.MouseMove)
                tabsHavePointer = lastTabsRect.Contains(Event.current.mousePosition);
            GUI.Box(toolbarRect, GUIContent.none, layerTabsBarStyle);

            const float navigationButtonWidth = 24f;
            var backRect = new Rect(toolbarRect.x + 3f, toolbarRect.y + 3f, navigationButtonWidth, toolbarRect.height - 6f);
            var forwardRect = new Rect(backRect.xMax + 2f, backRect.y, navigationButtonWidth, backRect.height);
            using (new EditorGUI.DisabledScope(layerVisitHistoryIndex <= 0))
            {
                if (GUI.Button(backRect, new GUIContent("<", "Back to the previously visited layer"), layerTabNavigationStyle))
                    NavigateLayerHistory(-1);
            }
            using (new EditorGUI.DisabledScope(layerVisitHistoryIndex < 0 || layerVisitHistoryIndex >= layerVisitHistory.Count - 1))
            {
                if (GUI.Button(forwardRect, new GUIContent(">", "Forward to the next visited layer"), layerTabNavigationStyle))
                    NavigateLayerHistory(1);
            }

            var lastNavigationRight = forwardRect.xMax;
            if (windowMode != WindowMode.Master)
            {
                var newWorkspaceRect = new Rect(forwardRect.xMax + 4f, backRect.y, navigationButtonWidth, backRect.height);
                DrawNewWorkspaceButton(newWorkspaceRect);
                lastNavigationRight = newWorkspaceRect.xMax;
            }

            const float clearButtonWidth = 30f;
            var clearRect = new Rect(lastNavigationRight + 2f, backRect.y, clearButtonWidth, backRect.height);
            using (new EditorGUI.DisabledScope(openLayerIds.Count <= 1 || string.IsNullOrEmpty(activeLayerId)))
            {
                var trashIcon = ArchitectorIcons.Get("TreeEditor.Trash");
                var content = new GUIContent(trashIcon.image, "Close all tabs except the active layer");
                if (GUI.Button(clearRect, content, layerTabNavigationStyle))
                    ConfirmClearOtherLayerTabs();
            }

            var viewport = new Rect(clearRect.xMax + 5f, toolbarRect.y + 3f, Mathf.Max(0f, toolbarRect.xMax - clearRect.xMax - 5f), toolbarRect.height - 6f);
            var tabIds = openLayerIds.ToArray();
            var tabWidths = tabIds.Select(TabWidth).ToArray();
            var contentWidth = Mathf.Max(0f, tabWidths.Sum() - Mathf.Max(0, tabWidths.Length - 1) * TabBorderOverlap);
            var maximumScroll = Mathf.Max(0f, contentWidth - viewport.width);
            layerTabsScroll = Mathf.Round(Mathf.Clamp(layerTabsScroll, 0f, maximumScroll));
            // The strip narrows when the inspector slides out; the active tab must not end up behind it.
            if (Event.current.type == EventType.Repaint && !Mathf.Approximately(viewport.width, lastTabsViewportWidth))
            {
                if (viewport.width < lastTabsViewportWidth)
                    scrollActiveLayerIntoView = true;
                lastTabsViewportWidth = viewport.width;
            }
            if (scrollActiveLayerIntoView)
            {
                EnsureActiveTabVisible(tabIds, tabWidths, viewport.width, maximumScroll);
                scrollActiveLayerIntoView = false;
            }

            var current = Event.current;
            HandleLayerTabStripDrag(viewport, tabIds, tabWidths);
            if (current.type == EventType.MouseDrag
                && current.button == 0
                && !string.IsNullOrEmpty(pressedLayerTabId)
                && (current.mousePosition - pressedLayerTabPosition).sqrMagnitude >= TabDragThresholdSquared)
            {
                BeginLayerTabDrag(pressedLayerTabId);
                current.Use();
                return;
            }
            if (current.rawType == EventType.MouseUp && current.button == 0)
                pressedLayerTabId = string.Empty;
            if (current.type == EventType.ScrollWheel && viewport.Contains(current.mousePosition) && maximumScroll > 0f)
            {
                layerTabsScroll = Mathf.Clamp(layerTabsScroll + current.delta.y * 24f, 0f, maximumScroll);
                current.Use();
                Repaint();
            }

            var tabStart = 0f;
            for (var index = 0; index < tabIds.Length; index++)
            {
                var tabRect = new Rect(viewport.x + tabStart - layerTabsScroll, viewport.y, tabWidths[index], viewport.height);
                tabStart += tabWidths[index] - TabBorderOverlap;
                if (tabRect.xMax <= viewport.x || tabRect.x >= viewport.xMax)
                    continue;

                var closeRect = new Rect(tabRect.xMax - 22f, tabRect.center.y - 10f, 18f, 20f);
                if (current.type == EventType.MouseDown
                    && current.button == 2
                    && viewport.Contains(current.mousePosition)
                    && tabRect.Contains(current.mousePosition))
                {
                    pressedLayerTabId = string.Empty;
                    CloseLayerTab(tabIds[index]);
                    current.Use();
                    Repaint();
                    return;
                }
                if (current.type == EventType.MouseDown && current.button == 0 && viewport.Contains(current.mousePosition))
                {
                    if (closeRect.Contains(current.mousePosition))
                    {
                        pressedLayerTabId = string.Empty;
                        CloseLayerTab(tabIds[index]);
                        current.Use();
                        Repaint();
                        return;
                    }
                    if (tabRect.Contains(current.mousePosition))
                    {
                        OpenLayer(tabIds[index], true, false);
                        pressedLayerTabId = tabIds[index];
                        pressedLayerTabPosition = current.mousePosition;
                        current.Use();
                        Repaint();
                        return;
                    }
                }
            }

            GUI.BeginGroup(viewport);
            tabStart = 0f;
            for (var index = 0; index < tabIds.Length; index++)
            {
                var layer = repository.FindLayer(tabIds[index]);
                var tabRect = new Rect(tabStart - layerTabsScroll, 0f, tabWidths[index], viewport.height);
                tabStart += tabWidths[index] - TabBorderOverlap;
                if (layer == null || tabRect.xMax <= 0f || tabRect.x >= viewport.width)
                    continue;

                var active = string.Equals(layer.id, activeLayerId, StringComparison.Ordinal);
                if (Event.current.type == EventType.Repaint)
                    layerTabStyle.Draw(tabRect, GUIContent.none, tabRect.Contains(Event.current.mousePosition), false, active, false);
                var iconRect = new Rect(tabRect.x + 5f, tabRect.center.y - 8f, 16f, 16f);
                var status = GetExplorerStatus(layer.id);
                if ((status & ArchitectorExplorerEntryStatus.Error) != 0)
                    GUI.Label(iconRect, new GUIContent(ArchitectorIcons.Get("console.erroricon.sml").image, "Conformance errors or an invalid layer document in this layer."));
                else if ((status & ArchitectorExplorerEntryStatus.Changed) != 0)
                    GUI.Label(iconRect, new GUIContent(ArchitectorIcons.Get("console.warnicon.sml").image, "Changes since approval in this layer."));
                else
                    DrawLayerIconAsset(iconRect, active ? EditorStyles.boldLabel.normal.textColor : EditorStyles.label.normal.textColor);
                var labelRect = new Rect(iconRect.xMax + 4f, tabRect.y, tabRect.width - 48f, tabRect.height);
                GUI.Label(labelRect, new GUIContent(layer.name, layer.name), active ? EditorStyles.boldLabel : EditorStyles.label);
                var closeRect = new Rect(tabRect.xMax - 22f, tabRect.center.y - 10f, 18f, 20f);
                if (closeRect.Contains(Event.current.mousePosition) && Event.current.type == EventType.Repaint)
                {
                    var hover = GUI.skin.settings.selectionColor;
                    hover.a = 0.28f;
                    EditorGUI.DrawRect(closeRect, hover);
                }
                GUI.Label(closeRect, new GUIContent("\u00D7", $"Close {layer.name}"), tabCloseStyle);
            }
            GUI.EndGroup();
            if (layerTabDropIndicatorX >= 0f && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(layerTabDropIndicatorX - 1f, viewport.y + 2f, 2f, viewport.height - 4f), GUI.skin.settings.selectionColor);
            if (explorerTabDropActive && Event.current.type == EventType.Repaint)
                DrawDropBorder(viewport);
        }

        private void BeginLayerTabDrag(string layerId)
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.SetGenericData(LayerTabDragDataKey, new LayerTabDragPayload(GetInstanceID(), layerId));
            DragAndDrop.StartDrag("Reorder Architector Tab");
            pressedLayerTabId = string.Empty;
        }

        private void HandleLayerTabStripDrag(Rect viewport, IReadOnlyList<string> tabIds, IReadOnlyList<float> tabWidths)
        {
            var current = Event.current;
            if (current.type == EventType.DragExited)
            {
                layerTabDropIndicatorX = -1f;
                explorerTabDropActive = false;
                Repaint();
                return;
            }
            if ((current.type != EventType.DragUpdated && current.type != EventType.DragPerform)
                || !viewport.Contains(current.mousePosition))
            {
                return;
            }

            if (DragAndDrop.GetGenericData(LayerTabDragDataKey) is LayerTabDragPayload tabPayload)
            {
                explorerTabDropActive = false;
                if (tabPayload.ownerWindowId != GetInstanceID() || !openLayerIds.Contains(tabPayload.layerId))
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                    layerTabDropIndicatorX = -1f;
                    current.Use();
                    return;
                }

                var insertionIndex = TabInsertionIndex(current.mousePosition.x, viewport, tabWidths);
                layerTabDropIndicatorX = TabInsertionScreenX(insertionIndex, viewport, tabWidths);
                DragAndDrop.visualMode = DragAndDropVisualMode.Move;
                if (current.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    ReorderLayerTab(tabPayload.layerId, insertionIndex);
                    layerTabDropIndicatorX = -1f;
                }
                current.Use();
                Repaint();
                return;
            }

            if (!TryGetExplorerReferenceDrag(out var explorerPayload))
                return;
            layerTabDropIndicatorX = -1f;
            explorerTabDropActive = true;
            var layerIds = ResolveExplorerLayerIds(explorerPayload);
            DragAndDrop.visualMode = layerIds.Count == 0 ? DragAndDropVisualMode.Rejected : DragAndDropVisualMode.Link;
            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                explorerTabDropActive = false;
                if (layerIds.Count == 0)
                {
                    ShowNotification(new GUIContent("The dropped group contains no direct layers."));
                }
                else if (!ExplorerDropNeedsConfirmation(explorerPayload)
                         || EditorUtility.DisplayDialog(
                             "Add Group Layers",
                             $"Add {layerIds.Count} direct layer{(layerIds.Count == 1 ? string.Empty : "s")} from the dropped group selection to this window?\n\nNested groups are not included.",
                             "Add Layers",
                             "Cancel"))
                {
                    OpenLayersAsTabs(layerIds, layerIds[0]);
                }
            }
            current.Use();
            Repaint();
        }

        private int TabInsertionIndex(float mouseX, Rect viewport, IReadOnlyList<float> tabWidths)
        {
            var contentX = mouseX - viewport.x + layerTabsScroll;
            var cursor = 0f;
            for (var index = 0; index < tabWidths.Count; index++)
            {
                if (contentX < cursor + tabWidths[index] * 0.5f)
                    return index;
                cursor += tabWidths[index] - TabBorderOverlap;
            }
            return tabWidths.Count;
        }

        private float TabInsertionScreenX(int insertionIndex, Rect viewport, IReadOnlyList<float> tabWidths)
        {
            var contentX = 0f;
            for (var index = 0; index < Mathf.Min(insertionIndex, tabWidths.Count); index++)
                contentX += tabWidths[index] - TabBorderOverlap;
            return Mathf.Clamp(viewport.x + contentX - layerTabsScroll, viewport.x, viewport.xMax);
        }

        private void ReorderLayerTab(string layerId, int insertionIndex)
        {
            var oldIndex = openLayerIds.IndexOf(layerId);
            if (oldIndex < 0)
                return;
            openLayerIds.RemoveAt(oldIndex);
            if (insertionIndex > oldIndex)
                insertionIndex--;
            insertionIndex = Mathf.Clamp(insertionIndex, 0, openLayerIds.Count);
            openLayerIds.Insert(insertionIndex, layerId);
            scrollActiveLayerIntoView = false;
        }

        private float TabWidth(string layerId)
        {
            var name = repository.FindLayer(layerId)?.name ?? string.Empty;
            return Mathf.Round(Mathf.Max(EditorStyles.boldLabel.CalcSize(new GUIContent(name)).x + 52f, 88f));
        }

        private void EnsureActiveTabVisible(IReadOnlyList<string> tabIds, IReadOnlyList<float> tabWidths, float viewportWidth, float maximumScroll)
        {
            var activeIndex = -1;
            var activeStart = 0f;
            for (var index = 0; index < tabIds.Count; index++)
            {
                if (string.Equals(tabIds[index], activeLayerId, StringComparison.Ordinal))
                {
                    activeIndex = index;
                    break;
                }
                activeStart += tabWidths[index] - TabBorderOverlap;
            }
            if (activeIndex < 0)
                return;
            var activeEnd = activeStart + tabWidths[activeIndex];
            if (activeStart < layerTabsScroll)
                layerTabsScroll = activeStart;
            else if (activeEnd > layerTabsScroll + viewportWidth)
                layerTabsScroll = activeEnd - viewportWidth;
            layerTabsScroll = Mathf.Clamp(layerTabsScroll, 0f, maximumScroll);
        }

        public static void OpenLayerInMaster(string layerId)
        {
            Open();
            var window = Resources.FindObjectsOfTypeAll<ArchitectorMasterWindow>()
                .FirstOrDefault(candidate => candidate.windowMode == WindowMode.Master);
            if (window == null)
                return;
            window.Focus();
            window.OpenLayer(layerId);
            window.Repaint();
        }

        private void OpenLayer(string layerId, bool recordVisit = true, bool revealTab = true)
        {
            if (!loaded)
            {
                pendingLayerId = layerId ?? string.Empty;
                return;
            }
            if (repository?.FindLayer(layerId) == null)
                return;
            deferInspectorSelection = false;
            inspectorArranging = false;
            openLayerIds ??= new List<string>();
            if (!openLayerIds.Contains(layerId))
                openLayerIds.Add(layerId);
            if (revealTab && !string.Equals(activeLayerId, layerId, StringComparison.Ordinal))
                scrollActiveLayerIntoView = true;
            activeLayerId = layerId;
            SelectActiveLayerInExplorer(layerId);
            if (recordVisit)
                RecordLayerVisit(layerId);
            UpdateWindowTitle();
        }

        private void SelectActiveLayerInExplorer(string layerId)
        {
            if (windowMode != WindowMode.Master)
                return;
            var layer = repository?.FindLayer(layerId);
            if (layer == null)
                return;
            currentGroupId = layer.parentGroupId ?? string.Empty;
            search = string.Empty;
            selectedExplorerEntryIds.Clear();
            selectedExplorerEntryIds.Add(layer.id);
            explorerSelectionAnchorId = layer.id;
            layerCanvas.SelectOnly(string.Empty);
        }

        private void OpenLayersAsTabs(IEnumerable<string> layerIds, string layerToActivate)
        {
            var validIds = layerIds?
                .Where(layerId => repository?.FindLayer(layerId) != null)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            foreach (var layerId in validIds)
            {
                if (!openLayerIds.Contains(layerId))
                    openLayerIds.Add(layerId);
            }
            var activeId = validIds.Contains(layerToActivate, StringComparer.Ordinal)
                ? layerToActivate
                : validIds.FirstOrDefault();
            if (!string.IsNullOrEmpty(activeId))
                OpenLayer(activeId);
        }

        private void RecordLayerVisit(string layerId)
        {
            layerVisitHistory ??= new List<string>();
            if (layerVisitHistoryIndex >= 0
                && layerVisitHistoryIndex < layerVisitHistory.Count
                && string.Equals(layerVisitHistory[layerVisitHistoryIndex], layerId, StringComparison.Ordinal))
            {
                return;
            }
            if (layerVisitHistoryIndex < layerVisitHistory.Count - 1)
                layerVisitHistory.RemoveRange(layerVisitHistoryIndex + 1, layerVisitHistory.Count - layerVisitHistoryIndex - 1);
            layerVisitHistory.Add(layerId);
            const int maximumHistoryLength = 128;
            if (layerVisitHistory.Count > maximumHistoryLength)
                layerVisitHistory.RemoveRange(0, layerVisitHistory.Count - maximumHistoryLength);
            layerVisitHistoryIndex = layerVisitHistory.Count - 1;
        }

        private void NavigateLayerHistory(int direction)
        {
            var nextIndex = layerVisitHistoryIndex + direction;
            if (nextIndex < 0 || nextIndex >= layerVisitHistory.Count)
                return;
            layerVisitHistoryIndex = nextIndex;
            OpenLayer(layerVisitHistory[layerVisitHistoryIndex], false);
            Repaint();
        }

        private void CloseLayerTab(string layerId)
        {
            var closedIndex = openLayerIds.IndexOf(layerId);
            if (closedIndex < 0)
                return;
            openLayerIds.RemoveAt(closedIndex);
            RemoveLayerFromHistory(layerId);
            if (!string.Equals(activeLayerId, layerId, StringComparison.Ordinal))
                return;
            activeLayerId = openLayerIds.Count == 0
                ? string.Empty
                : openLayerIds[Mathf.Min(closedIndex, openLayerIds.Count - 1)];
            if (!string.IsNullOrEmpty(activeLayerId))
            {
                scrollActiveLayerIntoView = true;
                SelectActiveLayerInExplorer(activeLayerId);
                RecordLayerVisit(activeLayerId);
            }
            UpdateWindowTitle();
        }

        private void ConfirmClearOtherLayerTabs()
        {
            var layer = repository.FindLayer(activeLayerId);
            if (layer == null || openLayerIds.Count <= 1)
                return;
            var tabsToClose = openLayerIds.Count - 1;
            if (!EditorUtility.DisplayDialog(
                    "Clear Layer Tabs",
                    $"Close {tabsToClose} other layer tab{(tabsToClose == 1 ? string.Empty : "s")} and keep '{layer.name}' open?\n\nThe layers themselves will not be deleted.",
                    "Clear Tabs",
                    "Cancel",
                    DialogOptOutDecisionType.ForThisMachine,
                    ClearLayerTabsDialogKey))
            {
                return;
            }

            openLayerIds.Clear();
            openLayerIds.Add(activeLayerId);
            layerVisitHistory.Clear();
            layerVisitHistory.Add(activeLayerId);
            layerVisitHistoryIndex = 0;
            layerTabsScroll = 0f;
            scrollActiveLayerIntoView = true;
            UpdateWindowTitle();
            Repaint();
        }

        private void RemoveLayerFromHistory(string layerId)
        {
            for (var index = layerVisitHistory.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(layerVisitHistory[index], layerId, StringComparison.Ordinal))
                    continue;
                if (index <= layerVisitHistoryIndex)
                    layerVisitHistoryIndex--;
                layerVisitHistory.RemoveAt(index);
            }
            for (var index = layerVisitHistory.Count - 1; index > 0; index--)
            {
                if (!string.Equals(layerVisitHistory[index], layerVisitHistory[index - 1], StringComparison.Ordinal))
                    continue;
                if (index <= layerVisitHistoryIndex)
                    layerVisitHistoryIndex--;
                layerVisitHistory.RemoveAt(index);
            }
            layerVisitHistoryIndex = layerVisitHistory.Count == 0
                ? -1
                : Mathf.Clamp(layerVisitHistoryIndex, 0, layerVisitHistory.Count - 1);
        }

        private void DrawWorkspace()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
            {
                var layer = repository.FindLayer(activeLayerId);
                if (layer == null)
                {
                    DrawLayerTabs();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("Select or create a layer", EditorStyles.centeredGreyMiniLabel);
                    GUILayout.FlexibleSpace();
                    return;
                }

                DrawLayerTabs();
                var canvasRect = GUILayoutUtility.GetRect(10f, 10000f, 10f, 10000f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (Event.current.type == EventType.Repaint)
                    lastCanvasRect = canvasRect;
                var hotkeysRect = HotkeysButtonRect(canvasRect);
                DrawHotkeysButton(hotkeysRect);
                if (Event.current.type == EventType.MouseDown
                    && Event.current.button == 0
                    && canvasRect.Contains(Event.current.mousePosition))
                {
                    selectedExplorerEntryIds.Clear();
                    explorerSelectionAnchorId = string.Empty;
                }
                layerCanvas.Draw(canvasRect, layer, repository, typeIndex, userState, nodeClipboard, Run, Repaint, RequestContextMenu, layerId => OpenLayer(layerId), aiWork?.WorkspacesLocked != true);
                HandleLayerPortalDrop(canvasRect, layer);
                if (Event.current.type == EventType.Repaint)
                    PaintHotkeysButton(hotkeysRect);
            }
        }

        private static Rect HotkeysButtonRect(Rect canvasRect)
        {
            const float size = 22f;
            const float margin = 8f;
            return new Rect(canvasRect.xMax - size - margin, canvasRect.y + margin, size, size);
        }

        private static readonly GUIContent HotkeysContent = new("?", "Hotkeys");

        // The button runs before the canvas in every event, so a click on it never reaches the canvas and control ids stay stable;
        // the canvas paints over it, so it is painted again on top without taking a control id.
        private static void DrawHotkeysButton(Rect rect)
        {
            if (GUI.Button(rect, HotkeysContent, GUI.skin.button))
                PopupWindow.Show(rect, new ArchitectorHotkeysPopup());
        }

        private static void PaintHotkeysButton(Rect rect)
        {
            var hover = GUI.enabled && rect.Contains(Event.current.mousePosition);
            GUI.skin.button.Draw(rect, HotkeysContent, hover, false, false, false);
        }

        private void DrawNodeInspector()
        {
            UpdateInspectorSelection();
            if (repository.FindLayer(inspectedLayerId) == null)
                return;
            if (!string.IsNullOrEmpty(inspectedEdgeId))
            {
                edgeInspector.Draw(inspectedLayerId, inspectedEdgeId, repository, Run, layerCanvas.SelectedTrunkEdges);
                return;
            }
            if (!string.IsNullOrEmpty(inspectedNodeId))
            {
                nodeInspector.Draw(inspectedLayerId, inspectedNodeId, repository, typeIndex, Run, layerCanvas.SelectOnly, layerId => OpenLayer(layerId));
                return;
            }
            DrawLayerInspector(repository.FindLayer(inspectedLayerId));
        }

        private void UpdateInspectorSelection()
        {
            if (deferInspectorSelection)
                return;
            inspectedLayerId = string.Empty;
            inspectedNodeId = string.Empty;
            inspectedEdgeId = string.Empty;
            if (repository.FindLayer(activeLayerId) == null || layerCanvas.IsBoxSelecting)
                return;
            if (!string.IsNullOrEmpty(layerCanvas.SelectedEdgeId) || layerCanvas.SelectionCount == 1)
            {
                inspectedLayerId = activeLayerId;
                inspectedNodeId = layerCanvas.SelectedNodeId;
                inspectedEdgeId = layerCanvas.SelectedEdgeId;
                return;
            }
            if (selectedExplorerEntryIds.Count != 1)
                return;
            var selectedLayer = repository.FindLayer(selectedExplorerEntryIds.First());
            if (selectedLayer != null)
                inspectedLayerId = selectedLayer.id;
        }

        private void UpdateInspectorPointer(Event current)
        {
            if (current.rawType == EventType.MouseDown)
            {
                inspectorPointerDown = true;
                inspectorPointerDragged = false;
                inspectorPointerStart = current.mousePosition;
                inspectorPointerOnCanvas = lastCanvasRect.Contains(current.mousePosition);
                if (inspectorPointerOnCanvas)
                {
                    explorerKeyboardFocus = false;
                    stepPreviewLayerId = string.Empty;
                }
                inspectorPointerSelection = layerCanvas.SelectionKey;
                deferInspectorSelection = true;
            }
            else if (current.rawType == EventType.MouseDrag && inspectorPointerDown)
            {
                inspectorPointerDragged |= (current.mousePosition - inspectorPointerStart).sqrMagnitude >= TabDragThresholdSquared;
            }
            else if (current.rawType == EventType.MouseUp)
            {
                if (inspectorPointerDown && inspectorPointerDragged && layerCanvas.IsDraggingNode)
                    inspectorArranging = true;
                deferInspectorSelection = inspectorPointerDown && (inspectorPointerDragged || ArrangingClickKeepsInspector());
                inspectorPointerDown = false;
            }
            else if (current.rawType == EventType.DragExited)
            {
                inspectorPointerDown = false;
                deferInspectorSelection = false;
            }
            else if (!inspectorPointerDown && (current.rawType == EventType.KeyDown || current.rawType == EventType.ContextClick))
            {
                deferInspectorSelection = false;
            }
        }

        private bool ArrangingClickKeepsInspector()
        {
            if (!inspectorArranging || !inspectorPointerOnCanvas)
                return false;
            if (Event.current.button != 0)
                return true;
            var selection = layerCanvas.SelectionKey;
            if (selection == "|" || selection == inspectorPointerSelection)
            {
                inspectorArranging = false;
                return false;
            }
            return true;
        }

        private void DrawLayerInspector(ArchitectorLayerRecord layer)
        {
            if (!string.Equals(purposeLayerId, layer.id, StringComparison.Ordinal))
            {
                purposeLayerId = layer.id;
                purposeDraft = layer.purpose ?? string.Empty;
                purposeDirty = false;
            }
            else if (!purposeDirty && !string.Equals(purposeDraft, layer.purpose ?? string.Empty, StringComparison.Ordinal))
            {
                purposeDraft = layer.purpose ?? string.Empty;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(ArchitectorNodeInspector.Width), GUILayout.ExpandHeight(true)))
            {
                var approval = repository.GetLayerApprovalStatus(layer.id);
                using (new EditorGUILayout.HorizontalScope())
                {
                    // One height and vertical centering for every item, so the mini tags sit on the line of the bold title.
                    var rowHeight = GUILayout.Height(EditorGUIUtility.singleLineHeight);
                    headerTagStyle ??= new GUIStyle(EditorStyles.miniBoldLabel);
                    headerTagStyle.margin = new RectOffset(3, 3, 2, 2);
                    headerTagStyle.padding = new RectOffset(2, 2, 0, 0);
                    headerTagStyle.alignment = TextAnchor.MiddleLeft;
                    GUILayout.Label(layer.name, EditorStyles.boldLabel, GUILayout.ExpandWidth(false), rowHeight);
                    var approvalStyle = new GUIStyle(EditorStyles.boldLabel);
                    approvalStyle.normal.textColor = ApprovalStatusColor(approval.State);
                    GUILayout.Label($"({approval.State})", approvalStyle, GUILayout.ExpandWidth(false), rowHeight);
                    var conformance = DisplayedLayerConformance(layer.id);
                    if (conformance != null && IsPlanned(layer, approval, conformance))
                        GUILayout.Label(
                            new GUIContent("Planned", "The design is approved, but some typed nodes are not linked to code or their contracts do not pass conformance yet."),
                            headerTagStyle,
                            GUILayout.ExpandWidth(false),
                            rowHeight);
                    if (layer.approval?.approvedByAgent == true)
                    {
                        GUILayout.Label(
                            new GUIContent("By Agent", "An agent approved this layer without your review."),
                            headerTagStyle,
                            GUILayout.ExpandWidth(false),
                            rowHeight);
                        if (GUILayout.Button(new GUIContent("Confirm", "Record that you reviewed this layer."), EditorStyles.miniButton, GUILayout.ExpandWidth(false), rowHeight))
                            Run(() => repository.ApproveLayers(new[] { layer.id }));
                    }
                    GUILayout.FlexibleSpace();
                    DrawRelationshipFilters(layer.id);
                }
                var shownConformance = DisplayedLayerConformance(layer.id);
                if (shownConformance != null)
                    DrawConformance(layer.id, shownConformance);
                else
                    EditorGUILayout.LabelField(LayerConformanceFailed(layer.id) ? "Conformance unavailable" : "Checking conformance…", EditorStyles.miniLabel);
                var hiddenPortals = repository.GetHiddenPortalCount(layer.id);
                if (hiddenPortals > 0)
                    EditorGUILayout.LabelField(
                        new GUIContent(
                            $"Hidden Portals to draft layers: {hiddenPortals}",
                            "This layer has an approved baseline, so it shows only Portals to other approved layers. The hidden Portals and their relationships remain in the architecture."),
                        EditorStyles.miniLabel);
                {
                    var approved = string.Equals(approval.State, ArchitectorApprovalStates.Approved, StringComparison.Ordinal);
                    var buttonText = approved
                        ? "Revoke"
                        : string.Equals(approval.State, ArchitectorApprovalStates.Draft, StringComparison.Ordinal)
                            ? "Approve"
                            : "Approve Changes";
                    var buttonWidth = EditorStyles.miniButton.CalcSize(new GUIContent(buttonText)).x + 8f;
                    var buttonRow = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                    var buttonRect = new Rect(buttonRow.x, buttonRow.y, buttonWidth, buttonRow.height);
                    var buttonTint = approved
                        ? new Color(1f, 0.62f, 0.56f)
                        : new Color(0.62f, 0.96f, 0.68f);
                    if (DrawTintedMiniButton(buttonRect, buttonText, buttonTint))
                    {
                        if (approved)
                            Run(() => repository.RevokeLayerApprovals(new[] { layer.id }));
                        else
                            Run(() => repository.ApproveLayers(new[] { layer.id }));
                    }
                    if (approved && repository.HasUnapprovedResponsibilities(layer.id))
                    {
                        const string notesText = "Approve Notes";
                        var notesWidth = EditorStyles.miniButton.CalcSize(new GUIContent(notesText)).x + 8f;
                        var notesRect = new Rect(buttonRect.xMax + 4f, buttonRow.y, notesWidth, buttonRow.height);
                        if (DrawTintedMiniButton(notesRect, notesText, new Color(0.62f, 0.96f, 0.68f)))
                            Run(() => repository.ApproveLayers(new[] { layer.id }));
                        DrawBaselineChanges(layer.id);
                    }
                    if (string.Equals(approval.State, ArchitectorApprovalStates.Changed, StringComparison.Ordinal))
                    {
                        const string revertText = "Revert To Approved";
                        var revertWidth = EditorStyles.miniButton.CalcSize(new GUIContent(revertText)).x + 8f;
                        var revertRect = new Rect(buttonRect.xMax + 4f, buttonRow.y, revertWidth, buttonRow.height);
                        if (DrawTintedMiniButton(revertRect, revertText, new Color(1f, 0.85f, 0.55f))
                            && EditorUtility.DisplayDialog(
                                "Revert To Approved",
                                $"Restore '{layer.name}' to its approved version? Changes since approval are discarded; Undo can restore them.",
                                "Revert",
                                "Cancel"))
                            Run(() => repository.RevertLayerToApproved(layer.id));
                        DrawBaselineChanges(layer.id);
                    }
                }
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Purpose", EditorStyles.miniBoldLabel);
                var purposeControlName = $"Architector.Purpose.{layer.id}";
                GUI.SetNextControlName(purposeControlName);
                var purposeStyle = EditorStyles.textArea;
                var previousWordWrap = purposeStyle.wordWrap;
                purposeStyle.wordWrap = true;
                var next = EditorGUILayout.TextArea(
                    purposeDraft,
                    purposeStyle,
                    GUILayout.Width(ArchitectorNodeInspector.Width - 12f),
                    GUILayout.Height(90f));
                purposeStyle.wordWrap = previousWordWrap;
                var purposeRect = GUILayoutUtility.GetLastRect();
                var currentEvent = Event.current;
                if (currentEvent.type == EventType.MouseDown
                    && currentEvent.button == 0
                    && !purposeRect.Contains(currentEvent.mousePosition)
                    && string.Equals(GUI.GetNameOfFocusedControl(), purposeControlName, StringComparison.Ordinal))
                {
                    GUI.FocusControl(null);
                    Repaint();
                }
                if (!string.Equals(next, purposeDraft, StringComparison.Ordinal))
                {
                    purposeDraft = next;
                    purposeDirty = true;
                }
                DrawLayerScope(layer);
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!purposeDirty))
                    {
                        if (GUILayout.Button("Revert"))
                        {
                            purposeDraft = layer.purpose ?? string.Empty;
                            purposeDirty = false;
                            GUI.FocusControl(null);
                        }
                        if (GUILayout.Button("Save"))
                        {
                            Run(() => repository.UpdateLayerPurpose(layer.id, purposeDraft));
                            purposeDirty = false;
                            GUI.FocusControl(null);
                        }
                    }
                }
            }
        }

        private void DrawRelationshipFilters(string layerId)
        {
            var current = repository.LoadLayer(layerId).layout.visibleRelationshipKinds;
            var next = (int)(RelationshipKindFilter)EditorGUILayout.EnumFlagsField(
                GUIContent.none,
                (RelationshipKindFilter)current,
                GUILayout.Width(90f));
            next &= ArchitectorRelationshipVisibility.All;
            if (next != current)
                Run(() => repository.SetRelationshipVisibility(layerId, next));
        }

        private static bool IsPlanned(
            ArchitectorLayerRecord layer,
            ArchitectorLayerApprovalStatus approval,
            ArchitectorConformanceReport conformance)
        {
            if (string.Equals(approval.State, ArchitectorApprovalStates.Draft, StringComparison.Ordinal)
                || string.IsNullOrEmpty(layer.approval?.semanticHash))
                return false;
            return layer.approval.baseline.nodes.Any(node => string.Equals(node.type?.mode, "declared", StringComparison.Ordinal))
                   || conformance.ErrorCount > 0
                   || conformance.UnverifiedMemberCount > 0
                   || conformance.UnverifiedTypeFactCount > 0;
        }

        // The inspector shows a "checking" line only for a wait expected to be long. A check expected to be very short
        // runs at once on the Layout event; a check of moderate length runs on the next editor update after a paint,
        // and until then the last shown report stays on screen, so switching layers does not flicker. The expectation
        // is how long the last check of that layer (or any layer, for one never checked) took.
        private const double InstantConformanceSeconds = 0.05;
        private const double VisibleWaitSeconds = 0.5;
        [NonSerialized] private string conformanceRequestLayerId = string.Empty;
        [NonSerialized] private bool conformanceRequestPainted;
        [NonSerialized] private ArchitectorConformanceReport shownLayerConformance;
        [NonSerialized] private double lastConformanceSeconds = -1.0;
        [NonSerialized] private string failedConformanceKey = string.Empty;
        [NonSerialized] private readonly Dictionary<string, double> conformanceSeconds = new(StringComparer.Ordinal);

        private ArchitectorConformanceReport DisplayedLayerConformance(string layerId)
        {
            // Only the Layout event decides what is shown, so Layout and Repaint always draw the same controls.
            if (Event.current.type == EventType.Layout)
            {
                var expected = conformanceSeconds.TryGetValue(layerId, out var seconds) ? seconds : lastConformanceSeconds;
                var failed = string.Equals(failedConformanceKey, ConformanceKey(layerId), StringComparison.Ordinal);
                if (!failed && !IsLayerConformanceCurrent(layerId) && expected >= 0.0 && expected < InstantConformanceSeconds)
                    TryLayerConformance(layerId);
                if (IsLayerConformanceCurrent(layerId))
                    shownLayerConformance = layerConformance;
                else if (failed)
                    shownLayerConformance = null;
                else
                {
                    if (!string.Equals(conformanceRequestLayerId, layerId, StringComparison.Ordinal))
                    {
                        conformanceRequestLayerId = layerId;
                        conformanceRequestPainted = false;
                        EditorApplication.update -= StepLayerConformance;
                        EditorApplication.update += StepLayerConformance;
                    }
                    var sameLayer = layerConformance != null && string.Equals(layerConformanceLayerId, layerId, StringComparison.Ordinal);
                    if (sameLayer)
                        shownLayerConformance = layerConformance;
                    else if (expected < 0.0 || expected >= VisibleWaitSeconds)
                        shownLayerConformance = null;
                }
            }
            if (Event.current.type == EventType.Repaint && conformanceRequestLayerId.Length > 0)
                conformanceRequestPainted = true;
            return shownLayerConformance;
        }

        private bool LayerConformanceFailed(string layerId) =>
            string.Equals(failedConformanceKey, ConformanceKey(layerId), StringComparison.Ordinal);

        private string ConformanceKey(string layerId) => $"{layerId}|{repository.SemanticRevision}|{typeIndex.Generation}";

        // A layer whose check fails is not checked again until the architecture or the code changes.
        private void TryLayerConformance(string layerId)
        {
            try
            {
                GetLayerConformance(layerId);
            }
            catch (ArchitectorDataException)
            {
                failedConformanceKey = ConformanceKey(layerId);
            }
        }

        private bool IsLayerConformanceCurrent(string layerId) =>
            layerConformance != null
            && string.Equals(layerConformanceLayerId, layerId, StringComparison.Ordinal)
            && layerConformanceRepositoryRevision == repository.SemanticRevision
            && layerConformanceTypeGeneration == typeIndex.Generation;

        private void StepLayerConformance()
        {
            if (!conformanceRequestPainted)
                return;
            EditorApplication.update -= StepLayerConformance;
            var layerId = conformanceRequestLayerId;
            conformanceRequestLayerId = string.Empty;
            if (repository?.FindLayer(layerId) == null)
                return;
            TryLayerConformance(layerId);
            Repaint();
        }

        private ArchitectorConformanceReport GetLayerConformance(string layerId)
        {
            if (layerConformance == null
                || !string.Equals(layerConformanceLayerId, layerId, StringComparison.Ordinal)
                || layerConformanceRepositoryRevision != repository.SemanticRevision
                || layerConformanceTypeGeneration != typeIndex.Generation)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                layerConformance = ArchitectorConformance.Evaluate(repository, typeIndex, new[] { layerId }, int.MaxValue);
                lastConformanceSeconds = conformanceSeconds[layerId] = watch.Elapsed.TotalSeconds;
                layerConformanceLayerId = layerId;
                layerConformanceRepositoryRevision = repository.SemanticRevision;
                layerConformanceTypeGeneration = typeIndex.Generation;
            }
            return layerConformance;
        }

        private void DrawConformance(string layerId, ArchitectorConformanceReport conformance)
        {
            if (conformance.WarningCount == 0 && conformance.ErrorCount == 0)
                return;

            var text = $"{conformance.PassedCount} passed · "
                       + $"{conformance.WarningCount} {(conformance.WarningCount == 1 ? "warning" : "warnings")} · "
                       + $"{conformance.ErrorCount} {(conformance.ErrorCount == 1 ? "error" : "errors")}";
            var style = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = ConformanceStatusColor(conformance.State) },
                onNormal = { textColor = ConformanceStatusColor(conformance.State) },
                focused = { textColor = ConformanceStatusColor(conformance.State) },
                onFocused = { textColor = ConformanceStatusColor(conformance.State) }
            };
            var expanded = userState.GetConformanceExpanded(workspaceViewId, layerId);
            var nextExpanded = EditorGUILayout.Foldout(expanded, new GUIContent(text), true, style);
            if (nextExpanded != expanded)
            {
                expanded = nextExpanded;
                userState.SaveConformanceExpanded(workspaceViewId, layerId, expanded);
            }
            if (!expanded || conformance.Diagnostics.Count == 0)
                return;

            const float maximumHeight = 210f;
            EnsureConformanceLayout(conformance);
            var needsScroll = conformanceContentHeight + 4f > maximumHeight;
            var viewportHeight = needsScroll ? maximumHeight : conformanceContentHeight + 4f;
            conformanceScrollByLayerId.TryGetValue(layerId, out var scroll);
            if (needsScroll)
                scroll = EditorGUILayout.BeginScrollView(scroll, EditorStyles.helpBox, GUILayout.Height(viewportHeight));
            else
                GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(viewportHeight));
            var contentRect = GUILayoutUtility.GetRect(1f, conformanceContentHeight, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint || Event.current.isMouse)
            {
                var first = needsScroll
                    ? FindFirstVisibleConformanceRow(scroll.y)
                    : 0;
                var last = needsScroll
                    ? FindLastVisibleConformanceRow(scroll.y + viewportHeight)
                    : conformance.Diagnostics.Count - 1;
                for (var index = first; index <= last; index++)
                {
                    var diagnostic = conformance.Diagnostics[index];
                    var row = new Rect(
                        contentRect.x,
                        contentRect.y + conformanceRowOffsets[index],
                        contentRect.width,
                        conformanceRowHeights[index] - 2f);
                    DrawConformanceDiagnostic(row, diagnostic);
                }
            }
            if (needsScroll)
            {
                EditorGUILayout.EndScrollView();
                conformanceScrollByLayerId[layerId] = scroll;
            }
            else
            {
                GUILayout.EndVertical();
                conformanceScrollByLayerId[layerId] = Vector2.zero;
            }
        }

        private void EnsureConformanceLayout(ArchitectorConformanceReport conformance)
        {
            EnsureConformanceDiagnosticStyle();
            if (ReferenceEquals(conformanceLayoutReport, conformance)
                && Mathf.Approximately(conformanceLayoutPixelsPerPoint, EditorGUIUtility.pixelsPerPoint))
                return;

            conformanceLayoutReport = conformance;
            conformanceLayoutPixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            conformanceRowOffsets = new float[conformance.Diagnostics.Count];
            conformanceRowHeights = new float[conformance.Diagnostics.Count];
            conformanceContentHeight = 0f;
            const float textWidth = ArchitectorNodeInspector.Width - 62f;
            for (var index = 0; index < conformance.Diagnostics.Count; index++)
            {
                var content = new GUIContent(conformance.Diagnostics[index].Message);
                var textHeight = conformanceDiagnosticStyle.CalcHeight(content, textWidth);
                var rowHeight = Mathf.Max(38f, Mathf.Ceil(textHeight) + 8f);
                conformanceRowOffsets[index] = conformanceContentHeight;
                conformanceRowHeights[index] = rowHeight;
                conformanceContentHeight += rowHeight;
            }
        }

        private int FindFirstVisibleConformanceRow(float top)
        {
            var low = 0;
            var high = conformanceRowOffsets.Length - 1;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (conformanceRowOffsets[middle] + conformanceRowHeights[middle] < top)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private int FindLastVisibleConformanceRow(float bottom)
        {
            var low = 0;
            var high = conformanceRowOffsets.Length - 1;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (conformanceRowOffsets[middle] <= bottom)
                    low = middle;
                else
                    high = middle - 1;
            }
            return low;
        }

        private void EnsureConformanceDiagnosticStyle()
        {
            conformanceDiagnosticStyle ??= new GUIStyle(EditorStyles.wordWrappedLabel);
            conformanceDiagnosticStyle.wordWrap = true;
            conformanceDiagnosticStyle.clipping = TextClipping.Clip;
            conformanceDiagnosticStyle.fixedHeight = 0f;
            conformanceDiagnosticStyle.stretchHeight = true;
            conformanceDiagnosticStyle.alignment = TextAnchor.UpperLeft;
            var textColor = EditorStyles.wordWrappedLabel.normal.textColor;
            conformanceDiagnosticStyle.normal.textColor = textColor;
            conformanceDiagnosticStyle.onNormal.textColor = textColor;
            conformanceDiagnosticStyle.hover.textColor = textColor;
            conformanceDiagnosticStyle.onHover.textColor = textColor;
            conformanceDiagnosticStyle.active.textColor = textColor;
            conformanceDiagnosticStyle.onActive.textColor = textColor;
            conformanceDiagnosticStyle.focused.textColor = textColor;
            conformanceDiagnosticStyle.onFocused.textColor = textColor;
        }

        private void DrawConformanceDiagnostic(Rect rect, ArchitectorConformanceDiagnostic diagnostic)
        {
            var navigable = !string.IsNullOrEmpty(diagnostic.NodeId);
            if (navigable && rect.Contains(Event.current.mousePosition) && Event.current.type == EventType.Repaint)
            {
                var hover = GUI.skin.settings.selectionColor;
                hover.a = Mathf.Min(hover.a, 0.2f);
                EditorGUI.DrawRect(rect, hover);
            }
            var isError = string.Equals(diagnostic.Severity, ArchitectorConformanceSeverities.Error, StringComparison.Ordinal);
            var color = isError
                ? (EditorGUIUtility.isProSkin ? new Color(0.96f, 0.42f, 0.36f) : new Color(0.70f, 0.08f, 0.06f))
                : (EditorGUIUtility.isProSkin ? new Color(0.98f, 0.72f, 0.24f) : new Color(0.68f, 0.40f, 0.02f));
            var previous = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(rect.x + 5f, rect.y + 3f, 14f, 18f), isError ? "●" : "▲", EditorStyles.miniLabel);
            GUI.color = previous;

            var textRect = new Rect(rect.x + 22f, rect.y + 2f, rect.width - 27f, rect.height - 4f);
            var content = new GUIContent(diagnostic.Message, $"{diagnostic.Code}\nExpected: {diagnostic.Expected}\nObserved: {diagnostic.Observed}");
            EnsureConformanceDiagnosticStyle();
            if (navigable)
            {
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
                if (Event.current.type == EventType.MouseUp
                    && Event.current.button == 0
                    && rect.Contains(Event.current.mousePosition))
                {
                    layerCanvas.SelectOnly(diagnostic.NodeId);
                    if (!string.IsNullOrEmpty(diagnostic.MemberId))
                        nodeInspector.RequestMemberFocus(diagnostic.NodeId, diagnostic.MemberId);
                    Event.current.Use();
                    Repaint();
                }
            }
            GUI.Label(textRect, content, conformanceDiagnosticStyle);
        }

        private static bool DrawTintedMiniButton(Rect rect, string text, Color tint)
        {
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            var pressed = GUI.Button(rect, text, EditorStyles.miniButton);
            GUI.backgroundColor = previous;
            return pressed;
        }

        private static Color ConformanceStatusColor(string state)
        {
            if (string.Equals(state, ArchitectorConformanceStates.Conformant, StringComparison.Ordinal))
                return EditorGUIUtility.isProSkin ? new Color(0.48f, 0.86f, 0.52f) : new Color(0.08f, 0.48f, 0.12f);
            if (string.Equals(state, ArchitectorConformanceStates.Incomplete, StringComparison.Ordinal))
                return EditorGUIUtility.isProSkin ? new Color(0.98f, 0.72f, 0.24f) : new Color(0.68f, 0.40f, 0.02f);
            return EditorGUIUtility.isProSkin ? new Color(0.96f, 0.42f, 0.36f) : new Color(0.70f, 0.08f, 0.06f);
        }

        private static Color ApprovalStatusColor(string state)
        {
            if (string.Equals(state, ArchitectorApprovalStates.Approved, StringComparison.Ordinal))
                return EditorGUIUtility.isProSkin ? new Color(0.56f, 0.90f, 0.60f) : new Color(0.08f, 0.48f, 0.12f);
            if (string.Equals(state, ArchitectorApprovalStates.Changed, StringComparison.Ordinal))
                return EditorGUIUtility.isProSkin ? new Color(1f, 0.76f, 0.30f) : new Color(0.68f, 0.40f, 0.02f);
            return EditorGUIUtility.isProSkin ? new Color(0.52f, 0.52f, 0.52f) : new Color(0.42f, 0.42f, 0.42f);
        }

        private void HandleLayerPortalDrop(Rect canvasRect, ArchitectorLayerRecord activeLayer)
        {
            var current = Event.current;
            if ((current.type != EventType.DragUpdated && current.type != EventType.DragPerform)
                || !canvasRect.Contains(current.mousePosition)
                || !TryGetExplorerReferenceDrag(out var payload))
                return;

            var draggedLayer = payload.entryIds.Length == 1 ? repository.FindLayer(payload.entryIds[0]) : null;
            if (draggedLayer == null || string.Equals(draggedLayer.id, activeLayer.id, StringComparison.Ordinal))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                current.Use();
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                var canvasPoint = current.mousePosition - canvasRect.position;
                var requested = layerCanvas.CanvasToWorld(canvasPoint) - new Vector2(140f, 60f);
                var pair = default(ArchitectorPortalPair);
                Run(() => pair = repository.CreateLayerPortalPair(
                    activeLayer.id,
                    draggedLayer.id,
                    requested.x,
                    requested.y));
                if (!string.IsNullOrEmpty(pair.SourceNodeId))
                {
                    var created = repository.LoadLayer(activeLayer.id).semantic.nodes.First(node => node.id == pair.SourceNodeId);
                    if (repository.IsPortalEndpointVisible(activeLayer.id, created))
                        layerCanvas.SelectOnly(pair.SourceNodeId);
                    else
                        ShowNotification(new GUIContent(
                            $"Portal created. It is visible in '{draggedLayer.name}' and appears here once that layer is approved."));
                }
            }
            current.Use();
            Repaint();
        }

        private void HandleEmptyExplorerContext(Rect contentRect)
        {
            if (Event.current.type != EventType.ContextClick || !contentRect.Contains(Event.current.mousePosition))
                return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Create Group"), false, () => CreateGroup(currentGroupId));
            menu.AddItem(new GUIContent("Create Layer"), false, () => CreateLayer(currentGroupId));
            RequestContextMenu(menu, GUIUtility.GUIToScreenPoint(Event.current.mousePosition));
            Event.current.Use();
        }

        private void ShowGroupMenu(string groupId)
        {
            var group = repository.FindGroup(groupId);
            if (group == null)
                return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Open"), false, () =>
            {
                currentGroupId = groupId;
                search = string.Empty;
                Repaint();
            });
            if (group.layerIds.Count > 0)
                menu.AddItem(new GUIContent("Open in New Window"), false, () => OpenWorkspaceWindow(group.layerIds.ToArray()));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Create Group Inside"), false, () => CreateGroup(groupId));
            menu.AddItem(new GUIContent("Create Layer Inside"), false, () => CreateLayer(groupId));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Rename"), false, () => BeginRename(groupId));
            menu.AddItem(new GUIContent(DeleteMenuCaption(groupId)), false, () => DeleteExplorerSelection(groupId));
            menu.AddSeparator(string.Empty);
            menu.AddItem(
                new GUIContent("Arrange"),
                false,
                () => ArrangeLayers(ExplorerArrangeTargets(groupId), true));
            menu.AddItem(
                new GUIContent("Arrange All Again"),
                false,
                () => ArrangeLayers(ExplorerArrangeTargets(groupId), false));
            menu.AddSeparator(string.Empty);
            if (!repository.IsGroupLocked(groupId))
                menu.AddItem(new GUIContent("Lock…"), false, () => ArchitectorGroupLockWindow.ShowLock(groupId));
            else if (repository.IsGroupLockedForEditing(groupId))
                menu.AddItem(new GUIContent("Unlock…"), false, () => ArchitectorGroupLockWindow.ShowUnlock(groupId));
            else
            {
                menu.AddItem(new GUIContent("Relock"), false, () => repository.RelockGroup(groupId));
                menu.AddItem(new GUIContent("Remove Lock"), false, () => Run(() => repository.RemoveGroupLock(groupId)));
            }
            menu.AddSeparator(string.Empty);
            if (repository.IsGroupMarkedUnchecked(groupId))
                menu.AddItem(new GUIContent("Turn Checks On"), false, () => Run(() => repository.SetGroupChecks(groupId, true)));
            else if (repository.IsUnchecked(groupId))
                menu.AddDisabledItem(new GUIContent("Checks Are Off Through a Parent Group"));
            else
                menu.AddItem(new GUIContent("Turn Checks Off"), false, () => Run(() => repository.SetGroupChecks(groupId, false)));
            RequestContextMenu(menu, GUIUtility.GUIToScreenPoint(Event.current.mousePosition));
        }

        private void ShowLayerMenu(string layerId)
        {
            var layer = repository.FindLayer(layerId);
            if (layer == null)
                return;
            var siblings = string.IsNullOrEmpty(layer.parentGroupId)
                ? repository.Project.rootLayerIds
                : repository.FindGroup(layer.parentGroupId).layerIds;
            var index = siblings.IndexOf(layer.id);

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Open Workspace"), false, () =>
            {
                OpenLayer(layerId);
                Repaint();
            });
            menu.AddItem(new GUIContent("Open in New Window"), false, () => OpenLayerWindow(layerId));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Create Layer Below"), false, () => CreateLayer(layer.parentGroupId, index + 1));
            menu.AddItem(new GUIContent("Create Sibling Group"), false, () => CreateGroup(layer.parentGroupId));
            menu.AddItem(new GUIContent("Wrap in New Group"), false, () => Run(() =>
            {
                var groupId = repository.WrapLayerInNewGroup(layer.id, $"{layer.name} Group");
                currentGroupId = groupId;
            }));
            menu.AddSeparator(string.Empty);
            var arrangeTargets = ExplorerArrangeTargets(layerId);
            // One layer is arranged when asked; a selection of several skips layers already arranged and unchanged.
            menu.AddItem(
                new GUIContent(arrangeTargets.Count > 1 ? $"Arrange {arrangeTargets.Count} Layers" : "Arrange"),
                false,
                () => ArrangeLayers(arrangeTargets, arrangeTargets.Count > 1));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Rename"), false, () => BeginRename(layerId));
            menu.AddItem(new GUIContent(DeleteMenuCaption(layerId)), false, () => DeleteExplorerSelection(layerId));
            RequestContextMenu(menu, GUIUtility.GUIToScreenPoint(Event.current.mousePosition));
        }

        // The clicked entry, or the whole Explorer selection when the clicked entry is part of it; groups expand to every layer below them.
        private IReadOnlyList<string> ExplorerArrangeTargets(string entryId)
        {
            var entries = selectedExplorerEntryIds.Contains(entryId) && selectedExplorerEntryIds.Count > 1
                ? selectedExplorerEntryIds.ToArray()
                : new[] { entryId };
            var layers = new List<string>();
            void Collect(string id)
            {
                if (repository.FindLayer(id) != null)
                {
                    if (!layers.Contains(id))
                        layers.Add(id);
                    return;
                }
                var group = repository.FindGroup(id);
                if (group == null)
                    return;
                foreach (var layerId in group.layerIds)
                    Collect(layerId);
                foreach (var child in repository.Project.groups.Where(candidate => candidate.parentGroupId == id))
                    Collect(child.id);
            }
            foreach (var entry in entries)
                Collect(entry);
            return layers;
        }

        // Arrange runs one layer per editor update, so the window repaints between layers and its own bar offers Stop and Cancel And Undo.
        private sealed class ArrangeRun
        {
            public IReadOnlyList<string> LayerIds;
            public bool SkipArranged;
            public int Index;
            public int Skipped;
            public readonly List<string> Arranged = new();
            public readonly List<string> Failures = new();
        }

        [NonSerialized] private ArrangeRun arrangeRun;

        private bool Arranging => arrangeRun != null;

        private void ArrangeLayers(IReadOnlyList<string> layerIds, bool skipArranged)
        {
            if (aiWork?.WorkspacesLocked == true)
            {
                ShowNotification(new GUIContent("AI work is in progress. Unlock Architector to edit manually."));
                return;
            }
            if (Arranging || layerIds.Count == 0)
                return;
            arrangeRun = new ArrangeRun { LayerIds = layerIds, SkipArranged = skipArranged };
            EditorApplication.update -= StepArrange;
            EditorApplication.update += StepArrange;
            Repaint();
        }

        private void StepArrange()
        {
            var run = arrangeRun;
            if (run == null || repository == null)
            {
                EditorApplication.update -= StepArrange;
                return;
            }
            while (run.Index < run.LayerIds.Count)
            {
                var layer = repository.FindLayer(run.LayerIds[run.Index++]);
                if (layer == null)
                    continue;
                if (run.SkipArranged && userState.IsArranged(layer.id, repository.ArrangeFingerprint(layer.id)))
                {
                    run.Skipped++;
                    continue;
                }
                var revision = repository.Revision;
                try
                {
                    repository.ArrangeNodes(layer.id);
                    if (repository.Revision != revision)
                        run.Arranged.Add(layer.id);
                    userState.SetArranged(layer.id, repository.ArrangeFingerprint(layer.id));
                }
                catch (ArchitectorDataException exception)
                {
                    run.Failures.Add($"{layer.name}: {exception.Message}");
                }
                Repaint();
                return;
            }
            FinishArrange(false);
        }

        private void FinishArrange(bool undo)
        {
            var run = arrangeRun;
            if (run == null)
                return;
            EditorApplication.update -= StepArrange;
            arrangeRun = null;
            if (undo)
            {
                for (var step = 0; step < run.Arranged.Count && repository.CanUndo; step++)
                    repository.Undo();
                foreach (var layerId in run.Arranged)
                    userState.SetArranged(layerId, string.Empty);
            }
            var summary = undo
                ? "Arrange cancelled; every layer is back where it was."
                : $"Arranged {run.Arranged.Count} {(run.Arranged.Count == 1 ? "layer" : "layers")}"
                  + (run.Skipped > 0 ? $", {run.Skipped} already arranged and unchanged" : string.Empty)
                  + (run.Failures.Count > 0 ? $", {run.Failures.Count} not arranged" : string.Empty) + ".";
            ShowNotification(new GUIContent(summary));
            if (run.Failures.Count > 0)
                Debug.LogWarning("Architector Arrange skipped these layers:\n" + string.Join("\n", run.Failures));
            Repaint();
        }

        private (Rect bar, Rect stop, Rect cancel) ArrangeBarRects()
        {
            var canvas = lastCanvasRect.width > 1f ? lastCanvasRect : new Rect(0f, 0f, position.width, position.height);
            var bar = new Rect(canvas.x + 8f, canvas.yMax - 36f, Mathf.Max(220f, canvas.width - 16f), 28f);
            var cancel = new Rect(bar.xMax - 124f, bar.y + 4f, 120f, 20f);
            var stop = new Rect(cancel.x - 64f, bar.y + 4f, 60f, 20f);
            return (bar, stop, cancel);
        }

        // Read before the disabled workspace, so a click on the bar never reaches the canvas under it.
        private void HandleArrangeBarInput()
        {
            var current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0)
                return;
            var (bar, stop, cancel) = ArrangeBarRects();
            if (stop.Contains(current.mousePosition))
                FinishArrange(false);
            else if (cancel.Contains(current.mousePosition))
                FinishArrange(true);
            else if (!bar.Contains(current.mousePosition))
                return;
            current.Use();
        }

        private void PaintArrangeBar()
        {
            var run = arrangeRun;
            if (run == null || Event.current.type != EventType.Repaint)
                return;
            var (bar, stop, cancel) = ArrangeBarRects();
            EditorStyles.helpBox.Draw(bar, false, false, false, false);
            var next = run.Index < run.LayerIds.Count ? repository.FindLayer(run.LayerIds[run.Index])?.name : null;
            var text = $"Arranging {run.Index} of {run.LayerIds.Count}" + (next == null ? string.Empty : $": {next}");
            EditorGUI.ProgressBar(new Rect(bar.x + 4f, bar.y + 4f, stop.x - bar.x - 8f, 20f), (float)run.Index / run.LayerIds.Count, text);
            GUI.skin.button.Draw(stop, new GUIContent("Stop"), stop.Contains(Event.current.mousePosition), false, false, false);
            GUI.skin.button.Draw(cancel, new GUIContent("Cancel And Undo"), cancel.Contains(Event.current.mousePosition), false, false, false);
        }

        private void CreateGroup(string parentGroupId)
        {
            Run(() =>
            {
                var id = repository.CreateGroup(parentGroupId, "New Group");
                selectedExplorerEntryIds.Clear();
                explorerSelectionAnchorId = string.Empty;
                BeginRename(id);
            });
        }

        private void CreateLayer(string parentGroupId, int? index = null)
        {
            Run(() =>
            {
                var id = repository.CreateLayer(parentGroupId, "New Layer", index);
                selectedExplorerEntryIds.Clear();
                explorerSelectionAnchorId = string.Empty;
                BeginRename(id);
            });
        }

        private void BeginRename(string id)
        {
            renameEntryId = id;
            renameValue = repository.FindGroup(id)?.name ?? repository.FindLayer(id)?.name ?? string.Empty;
            renameNeedsFocus = true;
            Repaint();
        }

        private bool HandleRenameCompletion(Rect fieldRect)
        {
            var current = Event.current;
            if (current.type == EventType.KeyDown && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter))
            {
                CommitRename();
                current.Use();
                return true;
            }
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                renameEntryId = string.Empty;
                renameNeedsFocus = false;
                GUI.FocusControl(null);
                current.Use();
                Repaint();
                return true;
            }
            if (current.type == EventType.MouseDown && !fieldRect.Contains(current.mousePosition))
            {
                CommitRename();
                return true;
            }
            return false;
        }

        private void CommitRename()
        {
            if (repository == null || string.IsNullOrEmpty(renameEntryId))
                return;
            var id = renameEntryId;
            var value = renameValue;
            var currentName = repository.FindGroup(id)?.name ?? repository.FindLayer(id)?.name;
            renameEntryId = string.Empty;
            renameNeedsFocus = false;
            if (currentName == null || string.Equals(currentName, value?.Trim(), StringComparison.Ordinal))
                return;
            Run(() => repository.RenameEntry(id, value));
        }

        private string DeleteMenuCaption(string fallbackId)
        {
            var count = selectedExplorerEntryIds.Contains(fallbackId) ? selectedExplorerEntryIds.Count : 1;
            return count == 1 ? "Delete" : $"Delete {count} Entries";
        }

        private void DeleteExplorerSelection(string fallbackId)
        {
            var ids = selectedExplorerEntryIds.Contains(fallbackId)
                ? selectedExplorerEntryIds.ToArray()
                : new[] { fallbackId };
            Run(() => repository.DeleteEntries(ids));
            selectedExplorerEntryIds.Clear();
            explorerSelectionAnchorId = string.Empty;
            pressedExplorerEntryId = string.Empty;
        }

        private void NavigateBack()
        {
            var current = repository.FindGroup(currentGroupId);
            currentGroupId = current?.parentGroupId ?? string.Empty;
            search = string.Empty;
            selectedExplorerEntryIds.Clear();
            explorerSelectionAnchorId = string.Empty;
        }

        private string CurrentLocationName()
        {
            if (string.IsNullOrEmpty(currentGroupId))
                return "Project";
            var current = repository.FindGroup(currentGroupId);
            if (current == null)
                return "Project";
            var parentName = string.IsNullOrEmpty(current.parentGroupId)
                ? "Project"
                : repository.FindGroup(current.parentGroupId)?.name ?? "Project";
            return $"{parentName} / {current.name}";
        }

        private void EnsureSelectionExists()
        {
            if (!string.IsNullOrEmpty(currentGroupId) && repository?.FindGroup(currentGroupId) == null)
                currentGroupId = string.Empty;
            EnsureLayerNavigationState();
        }

        private void EnsureLayerNavigationState()
        {
            openLayerIds ??= new List<string>();
            layerVisitHistory ??= new List<string>();
            if (repository == null)
                return;

            var previousActiveIndex = openLayerIds.IndexOf(activeLayerId);
            var validLayerIds = new HashSet<string>(repository.Project.layers.Select(layer => layer.id), StringComparer.Ordinal);
            openLayerIds.RemoveAll(layerId => !validLayerIds.Contains(layerId));
            for (var index = layerVisitHistory.Count - 1; index >= 0; index--)
            {
                if (validLayerIds.Contains(layerVisitHistory[index]) && openLayerIds.Contains(layerVisitHistory[index]))
                    continue;
                if (index <= layerVisitHistoryIndex)
                    layerVisitHistoryIndex--;
                layerVisitHistory.RemoveAt(index);
            }
            layerVisitHistoryIndex = layerVisitHistory.Count == 0
                ? -1
                : Mathf.Clamp(layerVisitHistoryIndex, 0, layerVisitHistory.Count - 1);

            if (!string.IsNullOrEmpty(activeLayerId) && validLayerIds.Contains(activeLayerId))
            {
                if (!openLayerIds.Contains(activeLayerId))
                    openLayerIds.Add(activeLayerId);
                if (layerVisitHistory.Count == 0)
                    RecordLayerVisit(activeLayerId);
                return;
            }

            activeLayerId = openLayerIds.Count == 0
                ? string.Empty
                : openLayerIds[Mathf.Clamp(previousActiveIndex, 0, openLayerIds.Count - 1)];
            if (!string.IsNullOrEmpty(activeLayerId))
            {
                scrollActiveLayerIntoView = true;
                SelectActiveLayerInExplorer(activeLayerId);
                RecordLayerVisit(activeLayerId);
            }
        }

        private void OnRepositoryChanged()
        {
            EnsureSelectionExists();
            selectedExplorerEntryIds.RemoveWhere(id => repository.FindGroup(id) == null && repository.FindLayer(id) == null);
            UpdateWindowTitle();
            Repaint();
        }

        private void OnAiWorkChanged()
        {
            if (aiWork?.WorkspacesLocked == true)
            {
                pendingContextMenu = null;
                pendingContextMenuNeedsRepaint = false;
                pressedExplorerEntryId = string.Empty;
                pressedLayerTabId = string.Empty;
                layerCanvas?.CancelInteraction();
                GUI.FocusControl(null);
            }
            Repaint();
        }

        private void HandleUndoRedoShortcut()
        {
            if (Event.current.type != EventType.KeyDown || !Event.current.control)
                return;
            if (Event.current.keyCode == KeyCode.Z)
            {
                Run(repository.Undo);
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.Y)
            {
                Run(repository.Redo);
                Event.current.Use();
            }
        }

        private void HandleExplorerDeleteShortcut()
        {
            var current = Event.current;
            if (current.type != EventType.KeyDown
                || current.keyCode != KeyCode.Delete
                || selectedExplorerEntryIds.Count == 0
                || !string.IsNullOrEmpty(renameEntryId)
                || EditorGUIUtility.editingTextField)
            {
                return;
            }

            var ids = selectedExplorerEntryIds.ToArray();
            Run(() => repository.DeleteEntries(ids));
            selectedExplorerEntryIds.Clear();
            explorerSelectionAnchorId = string.Empty;
            pressedExplorerEntryId = string.Empty;
            current.Use();
        }

        private void Run(Action action)
        {
            if (aiWork?.WorkspacesLocked == true)
            {
                ShowNotification(new GUIContent("AI work is in progress. Unlock Architector to edit manually."));
                return;
            }
            if (Arranging)
            {
                ShowNotification(new GUIContent("Arrange is running. Stop it to edit."));
                return;
            }
            try
            {
                action();
                EnsureSelectionExists();
                Repaint();
            }
            catch (ArchitectorDataException exception)
            {
                ShowNotification(new GUIContent(exception.Message));
                Repaint();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ShowNotification(new GUIContent(exception.Message));
            }
        }

        private void RequestContextMenu(GenericMenu menu, Vector2 screenPosition)
        {
            pendingContextMenu = menu;
            pendingContextMenuPosition = screenPosition;
            pendingContextMenuNeedsRepaint = true;
            Repaint();
        }

        private void SchedulePendingContextMenu()
        {
            if (pendingContextMenu == null || !pendingContextMenuNeedsRepaint || Event.current.type != EventType.Repaint)
                return;

            pendingContextMenuNeedsRepaint = false;
            var menu = pendingContextMenu;
            var screenPosition = pendingContextMenuPosition;
            pendingContextMenu = null;
            EditorApplication.delayCall += () =>
            {
                if (this == null)
                    return;
                menu.DropDown(new Rect(GUIUtility.ScreenToGUIPoint(screenPosition), Vector2.zero));
            };
        }

        private void DrawLayerIconAsset(Rect rect, Color color)
        {
            var themeName = EditorGUIUtility.isProSkin ? "dark" : "light";
            var scaleSuffix = EditorGUIUtility.pixelsPerPoint > 1f ? "@2x" : string.Empty;
            var path = ArchitectorPackagePaths.Icon($"layer_icon_{themeName}{scaleSuffix}.png");
            if (layerIcon == null || !string.Equals(loadedLayerIconPath, path, StringComparison.Ordinal))
            {
                layerIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(path)
                            ?? AssetDatabase.LoadAssetAtPath<Texture2D>(ArchitectorPackagePaths.Icon($"layer_icon_{themeName}.png"));
                loadedLayerIconPath = path;
            }

            if (layerIcon == null)
            {
                DrawFallbackLayerIcon(rect, color);
                return;
            }

            GUI.DrawTexture(rect, layerIcon, ScaleMode.ScaleToFit, true);
        }

        private static void DrawFallbackLayerIcon(Rect rect, Color color)
        {
            var leftNode = new Rect(rect.x, rect.center.y - 4f, 7f, 7f);
            var rightNode = new Rect(rect.xMax - 7f, rect.center.y - 4f, 7f, 7f);
            EditorGUI.DrawRect(leftNode, color);
            EditorGUI.DrawRect(rightNode, color);

            var start = new Vector3(leftNode.xMax, leftNode.center.y);
            var end = new Vector3(rightNode.xMin - 1f, rightNode.center.y);
            Handles.BeginGUI();
            Handles.color = color;
            Handles.DrawLine(start, end);
            Handles.DrawLine(end, end + new Vector3(-3f, -2f));
            Handles.DrawLine(end, end + new Vector3(-3f, 2f));
            Handles.color = Color.white;
            Handles.EndGUI();
        }

        private void EnsureStyles()
        {
            if (versionStyle == null)
            {
                versionStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontStyle = FontStyle.Normal
                };
                versionStyle.normal.textColor = EditorGUIUtility.isProSkin
                    ? new Color(0.62f, 0.62f, 0.62f)
                    : new Color(0.38f, 0.38f, 0.38f);
            }

            if (breadcrumbStyle == null)
            {
                breadcrumbStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontStyle = FontStyle.Normal
                };
            }

            if (tabCloseStyle == null)
            {
                tabCloseStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Normal,
                    fontSize = 14,
                    padding = new RectOffset(),
                    margin = new RectOffset()
                };
            }

            if (layerTabsBarStyle == null)
            {
                layerTabsBarStyle = new GUIStyle(EditorStyles.toolbar)
                {
                    fixedHeight = 0f,
                    stretchHeight = true,
                    margin = new RectOffset(),
                    overflow = new RectOffset()
                };
            }
            if (layerTabStyle == null)
            {
                layerTabStyle = new GUIStyle(EditorStyles.toolbarButton)
                {
                    fixedHeight = 0f,
                    stretchHeight = true,
                    margin = new RectOffset(),
                    overflow = new RectOffset()
                };
            }
            reviewCountStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.LowerRight,
                fontSize = 10,
                padding = new RectOffset(),
                margin = new RectOffset(),
                clipping = TextClipping.Overflow
            };
            if (layerTabNavigationStyle == null)
            {
                layerTabNavigationStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    fixedHeight = 0f,
                    stretchHeight = true,
                    margin = new RectOffset(),
                    overflow = new RectOffset()
                };
            }

            breadcrumbStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.78f, 0.78f, 0.78f)
                : new Color(0.20f, 0.20f, 0.20f);
            breadcrumbStyle.hover.textColor = breadcrumbStyle.normal.textColor;
            breadcrumbStyle.active.textColor = breadcrumbStyle.normal.textColor;
            breadcrumbStyle.focused.textColor = breadcrumbStyle.normal.textColor;
            breadcrumbStyle.onNormal.textColor = breadcrumbStyle.normal.textColor;
            breadcrumbStyle.onHover.textColor = breadcrumbStyle.normal.textColor;
            breadcrumbStyle.onActive.textColor = breadcrumbStyle.normal.textColor;
            breadcrumbStyle.onFocused.textColor = breadcrumbStyle.normal.textColor;
            tabCloseStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.72f, 0.72f, 0.72f)
                : new Color(0.30f, 0.30f, 0.30f);
            tabCloseStyle.hover.textColor = tabCloseStyle.normal.textColor;
            tabCloseStyle.active.textColor = tabCloseStyle.normal.textColor;
            tabCloseStyle.focused.textColor = tabCloseStyle.normal.textColor;
            versionStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.62f, 0.62f, 0.62f)
                : new Color(0.38f, 0.38f, 0.38f);
            versionStyle.hover.textColor = versionStyle.normal.textColor;
            versionStyle.active.textColor = versionStyle.normal.textColor;
            versionStyle.focused.textColor = versionStyle.normal.textColor;
            versionStyle.onNormal.textColor = versionStyle.normal.textColor;
            versionStyle.onHover.textColor = versionStyle.normal.textColor;
            versionStyle.onActive.textColor = versionStyle.normal.textColor;
            versionStyle.onFocused.textColor = versionStyle.normal.textColor;
        }

        private sealed class ExplorerDragPayload
        {
            public ExplorerDragPayload(int ownerWindowId, string[] entryIds)
            {
                this.ownerWindowId = ownerWindowId;
                this.entryIds = entryIds;
            }

            public readonly int ownerWindowId;
            public readonly string[] entryIds;
        }

        private sealed class LayerTabDragPayload
        {
            public LayerTabDragPayload(int ownerWindowId, string layerId)
            {
                this.ownerWindowId = ownerWindowId;
                this.layerId = layerId;
            }

            public readonly int ownerWindowId;
            public readonly string layerId;
        }
    }
}
