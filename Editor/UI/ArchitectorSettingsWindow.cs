using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed partial class ArchitectorSettingsWindow : EditorWindow
    {
        private ArchitectorRepository repository;
        private ArchitectorAiWorkCoordinator aiWork;
        private string loadError = string.Empty;
        private Vector2 scroll;
        [NonSerialized] private bool sourceIndexExpanded;
        [NonSerialized] private bool geometryExpanded;
        [NonSerialized] private bool rulesExpanded;
        [NonSerialized] private bool paletteExpanded;
        [NonSerialized] private bool utilitiesExpanded = true;
        private bool includedRootsExpanded = true;
        private bool excludedRootsExpanded;
        [NonSerialized] private bool optimizationExpanded;
        private bool darkPaletteExpanded;
        private bool lightPaletteExpanded;
        [NonSerialized] private bool aiIntegrationExpanded = true;
        private const string WorkflowControlName = "ArchitectorAgentWorkflow";
        private double workflowHighlightUntil;
        private bool workflowFocusPending;
        private readonly Dictionary<string, ArchitectorAgentIntegrationProbeResult> integrationStatuses = new(StringComparer.Ordinal);

        [MenuItem("Window/Architector/Settings")]
        public static void Open()
        {
            var master = Resources.FindObjectsOfTypeAll<ArchitectorMasterWindow>()
                .FirstOrDefault(candidate => candidate.IsMasterWindow);
            if (master != null)
                master.Focus();
            var window = master != null
                ? GetWindow<ArchitectorSettingsWindow>("Architector Settings", true, typeof(ArchitectorMasterWindow))
                : GetWindow<ArchitectorSettingsWindow>();
            window.titleContent = new GUIContent("Architector Settings");
            window.minSize = new Vector2(680f, 420f);
            window.Show();
        }

        public static void OpenAgentWorkflow()
        {
            Open();
            var window = GetWindow<ArchitectorSettingsWindow>();
            window.aiIntegrationExpanded = true;
            window.workflowHighlightUntil = EditorApplication.timeSinceStartup + 2d;
            window.workflowFocusPending = true;
            window.Repaint();
        }

        private void DrawWorkflowHighlight(Rect rect)
        {
            if (workflowFocusPending && Event.current.type == EventType.Repaint)
            {
                workflowFocusPending = false;
                scroll.y = Mathf.Max(0f, rect.y - 48f);
                EditorGUI.FocusTextInControl(null);
                GUI.FocusControl(WorkflowControlName);
            }
            if (EditorApplication.timeSinceStartup >= workflowHighlightUntil)
                return;
            if (Event.current.type == EventType.Repaint)
            {
                var color = new Color(1f, 0.76f, 0.20f);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y - 1f, rect.width, 1f), color);
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax, rect.width, 1f), color);
                EditorGUI.DrawRect(new Rect(rect.x - 1f, rect.y - 1f, 1f, rect.height + 2f), color);
                EditorGUI.DrawRect(new Rect(rect.xMax, rect.y - 1f, 1f, rect.height + 2f), color);
            }
            Repaint();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Architector Settings");
            minSize = new Vector2(680f, 420f);
            ArchitectorPackageUpdates.Changed -= OnUpdatesChanged;
            ArchitectorPackageUpdates.Changed += OnUpdatesChanged;
            updateShownTag = ArchitectorPackageUpdates.State.availableTag;
            updateExpanded = ArchitectorPackageUpdates.UpdateAvailable;
            EditorApplication.delayCall -= CheckForUpdatesOnOpen;
            EditorApplication.delayCall += CheckForUpdatesOnOpen;
            if (!ArchitectorSession.TryGet(out var session, out loadError))
                return;
            repository = session.Repository;
            aiWork = session.AiWork;
            repository.Changed += Repaint;
            aiWork.Changed += Repaint;
        }

        private void OnDisable()
        {
            ArchitectorPackageUpdates.Changed -= OnUpdatesChanged;
            EditorApplication.delayCall -= CheckForUpdatesOnOpen;
            if (repository != null)
                repository.Changed -= Repaint;
            if (aiWork != null)
                aiWork.Changed -= Repaint;
        }

        private void OnGUI()
        {
            if (repository == null)
            {
                DrawUpdates();
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
                if (GUILayout.Button("Retry"))
                    OnEnable();
                return;
            }

            ArchitectorAiWorkUi.DrawSettingsBanner(aiWork);
            var aiLocked = aiWork?.SettingsLocked == true;
            if (aiLocked)
                ArchitectorAiWorkUi.ConsumeInputWhenLocked();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUILayout.VerticalScope(ArchitectorFoldoutBox.ContentStyle))
            {
                using (new EditorGUI.DisabledScope(aiLocked))
                    DrawUpdates();
                var draft = ArchitectorJson.Clone(repository.Settings);
                EditorGUI.BeginChangeCheck();
                using (new EditorGUI.DisabledScope(aiLocked))
                using (new LabelWidthScope(Mathf.Clamp(position.width * 0.52f, 190f, 280f)))
                {
                    DrawSourceIndex(draft.sourceIndex);
                    DrawRules(draft.rules);
                    DrawGeometry(draft.geometry, draft.arrange);
                    DrawPalette(draft.palette);
                    DrawAiIntegration(draft.agent);
                    DrawOptimization(draft.optimization);
                    DrawUtilities();
                }
                if (EditorGUI.EndChangeCheck())
                    Apply(draft);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSourceIndex(ArchitectorSourceIndexSettings sourceIndex)
        {
            using var box = new ArchitectorFoldoutBox("C# Source Index", ref sourceIndexExpanded);
            if (!box.Expanded)
                return;
            EditorGUILayout.HelpBox("Only these project-relative roots will be scanned for C# types. Excluded roots always win.", MessageType.None);
            includedRootsExpanded = EditorGUILayout.Foldout(includedRootsExpanded, $"Included Roots ({sourceIndex.includedRoots.Count})", true);
            if (includedRootsExpanded)
            {
                using (new EditorGUI.IndentLevelScope())
                    DrawPathList(sourceIndex.includedRoots, "Assets/Scripts");
            }

            excludedRootsExpanded = EditorGUILayout.Foldout(excludedRootsExpanded, $"Excluded Roots ({sourceIndex.excludedRoots.Count})", true);
            if (excludedRootsExpanded)
            {
                using (new EditorGUI.IndentLevelScope())
                    DrawPathList(sourceIndex.excludedRoots, "Assets/Plugins");
            }
        }

        private void DrawRules(ArchitectorRuleSettings rules)
        {
            using var box = new ArchitectorFoldoutBox("Rules", ref rulesExpanded);
            if (!box.Expanded)
                return;
            rules.dependencyCycles = (ArchitectorDependencyCyclePolicy)EditorGUILayout.Popup(
                new GUIContent("Dependency Cycles", "Reject refuses any change that closes a cycle of dependency relationships. Report keeps cycles that exist in the code, so the map can show every real dependency, and lists them as conformance warnings. Reject can be chosen again only after the cycles are removed. Shared with the team."),
                (int)rules.dependencyCycles,
                new[] { "Reject", "Report" });
            var cycles = repository.FindDependencyCycles();
            if (cycles.Count > 0)
                EditorGUILayout.HelpBox($"The graph has {cycles.Count} dependency cycles. Layer Inspector and conformance list them.", MessageType.None);
            var checksOff = rules.uncheckedGroupIds.Select(id => repository.FindGroup(id)?.name).Where(name => name != null).ToArray();
            EditorGUILayout.LabelField(
                new GUIContent("Checks Off", "Groups whose layers conformance skips and which cannot be approved. Turn checks on or off from a group's context menu; agents cannot change it. Shared with the team."),
                new GUIContent(checksOff.Length == 0 ? "None" : string.Join(", ", checksOff)));
        }

        private static void DrawPathList(List<string> paths, string newValue)
        {
            for (var index = 0; index < paths.Count; index++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    paths[index] = EditorGUILayout.DelayedTextField(paths[index]);
                    if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(24f)))
                    {
                        paths.RemoveAt(index);
                        GUI.changed = true;
                        break;
                    }
                }
            }

            if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(70f)))
            {
                paths.Add(newValue);
                GUI.changed = true;
            }
        }

        private void DrawGeometry(ArchitectorGeometrySettings geometry, ArchitectorArrangeSettings arrange)
        {
            using var box = new ArchitectorFoldoutBox("Workspace Geometry", ref geometryExpanded);
            if (!box.Expanded)
                return;
            geometry.minimumNodeGap = EditorGUILayout.FloatField("Minimum Node Gap", geometry.minimumNodeGap);
            geometry.gridSize = EditorGUILayout.FloatField("Grid Size", geometry.gridSize);
            geometry.snapToGrid = EditorGUILayout.Toggle("Snap To Grid", geometry.snapToGrid);
            geometry.fitLayerOnOpen = EditorGUILayout.Toggle(
                new GUIContent("Fit Layer On Open", "Each time a layer is opened, the canvas centers the whole layer and zooms out to fit the current window size, instead of restoring the last position on that layer."),
                geometry.fitLayerOnOpen);
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Arrange", EditorStyles.boldLabel);
            DrawArrange(arrange);
        }

        private void DrawArrange(ArchitectorArrangeSettings arrange)
        {
            arrange.maxGroupRelationships = EditorGUILayout.IntSlider(
                new GUIContent(
                    "Group Relationships",
                    "A connected part of a layer with more relationships than this is split into groups around its most connected nodes."),
                arrange.maxGroupRelationships,
                4,
                64);
            arrange.improvementSeconds = EditorGUILayout.Slider(
                new GUIContent(
                    "Improvement Time (s)",
                    "Arrange first lays a layer out quickly, then tries node swaps that shorten and uncross its lines until this time runs out. The layout is always complete; less time means fewer improvements on large layers. 0 skips the improvement."),
                arrange.improvementSeconds,
                0f,
                30f);
        }

        private void DrawOptimization(ArchitectorOptimizationSettings optimization)
        {
            using var box = new ArchitectorFoldoutBox("Optimization", ref optimizationExpanded);
            if (!box.Expanded)
                return;

            {
                GUILayout.Label("Member Search Scope", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "This is the maximum member scope reflected for picker candidates. Picker filters may narrow it, but cannot enable categories disabled here.",
                    MessageType.None);
                optimization.memberParentDepth = EditorGUILayout.IntPopup(
                    "Maximum Parent Depth",
                    optimization.memberParentDepth,
                    new[] { "No parents", "1 level", "2 levels", "All parents" },
                    new[] { 0, 1, 2, -1 });
                optimization.includeInterfaceMembers = EditorGUILayout.Toggle(
                    new GUIContent("Include Interfaces", "Allows interface members to be reflected and offered by the member picker."),
                    optimization.includeInterfaceMembers);
                optimization.includeFrameworkMembers = EditorGUILayout.Toggle(
                    new GUIContent("Include Framework API", "Allows members declared by Unity and system/framework types to be offered by the member picker."),
                    optimization.includeFrameworkMembers);
                GUILayout.Label("Canvas", EditorStyles.boldLabel);
                optimization.relationshipMerging = (ArchitectorRelationshipMerging)EditorGUILayout.Popup(
                    new GUIContent("Relationship Merging", "Builds shared paths for incoming relationships of the same type on one node side. Branches join along the route to shorten separate runs. All enabled modes combine parallel relationships between the same nodes when their type and preferred sides match. Maximum uses the same threshold as 2 or more."),
                    (int)optimization.relationshipMerging,
                    new[] { "Off", "3 or more", "2 or more", "Maximum" });
                optimization.hideRoutesWhileDragging = EditorGUILayout.Toggle(
                    new GUIContent("Hide Routes While Dragging", "Relationships of a dragged node are hidden and appear again when they are routed after the drop. Other relationships stay. Turn off to reroute them on every drag step, which is slower on large layers."),
                    optimization.hideRoutesWhileDragging);
                optimization.routeInBackground = EditorGUILayout.Toggle(
                    new GUIContent("Route In Background", "Calculates relationships on a background thread after a change, so the window stays responsive. They appear a moment later. A new change replaces a calculation that is still running."),
                    optimization.routeInBackground);
            }
        }

        private void DrawPalette(ArchitectorPaletteSettings palette)
        {
            using var box = new ArchitectorFoldoutBox("Color Palette", ref paletteExpanded);
            if (!box.Expanded)
                return;
            EditorGUILayout.HelpBox("Separate palettes keep nodes, text, types, edges and selections readable in both Unity themes.", MessageType.None);

            darkPaletteExpanded = EditorGUILayout.Foldout(darkPaletteExpanded, "Dark Theme", true);
            if (darkPaletteExpanded)
                DrawThemePalette(palette.dark, true);

            lightPaletteExpanded = EditorGUILayout.Foldout(lightPaletteExpanded, "Light Theme", true);
            if (lightPaletteExpanded)
                DrawThemePalette(palette.light, false);
        }

        private static void DrawThemePalette(ArchitectorThemePaletteSettings palette, bool dark)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                DrawColor("Workspace", palette.workspace);
                DrawColor("Grid", palette.grid);
                DrawColor("Node", palette.node);
                DrawColor("Node Header", palette.nodeHeader);
                DrawColor("Node Border", palette.nodeBorder);
                DrawColor("Entry Portal Outline", palette.entryPortalOutline);
                DrawColor("Exit Portal Outline", palette.exitPortalOutline);
                DrawColor("Title Text", palette.titleText);
                DrawColor("Type Text", palette.typeText);
                DrawColor("Dependency Edge", palette.edge);
                DrawColor("Data Flow Edge", palette.dataFlowEdge);
                DrawColor("Event Edge", palette.eventEdge);
                DrawColor("Callback Edge", palette.callbackEdge);
                DrawColor("Selected Edge", palette.selectedEdge);
                DrawColor("Incoming Edge Highlight", palette.incomingEdgeHighlight);
                DrawColor("Selection", palette.selection);
                if (GUILayout.Button("Reset Theme Palette", EditorStyles.miniButton))
                {
                    CopyPalette(dark ? ArchitectorThemePaletteSettings.CreateDark() : ArchitectorThemePaletteSettings.CreateLight(), palette);
                    GUI.changed = true;
                }
            }
        }

        private static void DrawColor(string label, ArchitectorColorRecord color)
        {
            color.Set(EditorGUILayout.ColorField(label, color.ToColor()));
        }

        private void DrawUtilities()
        {
            using var box = new ArchitectorFoldoutBox("Utilities", ref utilitiesExpanded);
            if (!box.Expanded)
                return;
            var confirmClear = !EditorUtility.GetDialogOptOutDecision(DialogOptOutDecisionType.ForThisMachine, ArchitectorMasterWindow.ClearLayerTabsDialogKey);
            var confirmClearNow = EditorGUILayout.Toggle(new GUIContent("Confirm Clearing Tabs",
                "Ask before the trash button closes every layer tab except the active one. Stored for this computer."), confirmClear);
            if (confirmClearNow != confirmClear)
                EditorUtility.SetDialogOptOutDecision(DialogOptOutDecisionType.ForThisMachine, ArchitectorMasterWindow.ClearLayerTabsDialogKey, !confirmClearNow);
            if (GUILayout.Button("Open Architector Integrations"))
                OpenIntegrations();
            if (GUILayout.Button("Open Architector Review"))
                EditorApplication.delayCall += ArchitectorReviewWindow.Open;
            if (GUILayout.Button("Reveal Settings JSON"))
                EditorUtility.RevealInFinder(repository.Paths.SettingsPath);
        }

        private void DrawAiIntegration(ArchitectorAgentSettings agent)
        {
            using var box = new ArchitectorFoldoutBox("AI Integration", ref aiIntegrationExpanded);
            if (!box.Expanded)
                return;

            {
                GUILayout.Label("This Computer", EditorStyles.miniBoldLabel);
                var preferences = ArchitectorAgentPreferences.Current;
                var modes = ArchitectorAgentWorkflowModes.All;
                var missingModes = ArchitectorAgentWorkflowModes.Optional
                    .Where(mode => !ArchitectorAgentWorkflowModes.IsInstalled(mode.Id))
                    .ToArray();
                var modeIndex = Mathf.Max(0, modes.ToList().FindIndex(mode => mode.Id == preferences.WorkflowMode));
                GUI.SetNextControlName(WorkflowControlName);
                var selectedMode = EditorGUILayout.Popup(
                    new GUIContent("Agent Workflow", "Stored only for you on this computer."),
                    modeIndex,
                    modes.Select(mode => mode.DisplayName)
                        .Concat(missingModes.Select(mode => $"{mode.DisplayName} (Not Installed)"))
                        .ToArray());
                DrawWorkflowHighlight(GUILayoutUtility.GetLastRect());
                if (selectedMode >= modes.Count)
                {
                    selectedMode = modeIndex;
                    OpenIntegrations();
                }
                var lockWindows = EditorGUILayout.Toggle(
                    new GUIContent("Lock Windows During Agent Work", "Stored only for you on this computer. Revision checks protect every write either way."),
                    preferences.LockWindowsDuringAgentWork);
                if (selectedMode != modeIndex)
                    preferences.WorkflowMode = modes[selectedMode].Id;
                if (lockWindows != preferences.LockWindowsDuringAgentWork)
                    preferences.LockWindowsDuringAgentWork = lockWindows;
                EditorGUILayout.HelpBox(
                    modes[selectedMode].Description,
                    modes[selectedMode].ShowsNotice ? MessageType.Warning : MessageType.Info);

                EditorGUILayout.Space(6f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Connections", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(
                            new GUIContent(" Add Connection", EditorGUIUtility.IconContent("Toolbar Plus").image, "Open the list of integrations."),
                            GUILayout.Height(22f)))
                        OpenIntegrations();
                }
                foreach (var descriptor in ArchitectorAgentIntegrationRegistry.Descriptors.Where(candidate => ArchitectorAgentIntegrationRegistry.IsAdded(agent, candidate)))
                {
                    var enabled = IntegrationEnabled(agent, descriptor.id);
                    if (!integrationStatuses.TryGetValue(descriptor.id, out var status))
                    {
                        status = ArchitectorAgentIntegrationRegistry.Probe(descriptor, enabled);
                        integrationStatuses[descriptor.id] = status;
                    }
                    var row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight + 8f);
                    GUI.Box(row, GUIContent.none, EditorStyles.helpBox);
                    var content = new Rect(row.x + 6f, row.y + 4f, row.width - 12f, EditorGUIUtility.singleLineHeight);
                    var toggleText = enabled ? "Disable" : "Enable";
                    var toggleWidth = EditorStyles.miniButton.CalcSize(new GUIContent(toggleText)).x + 10f;
                    var setupWidth = EditorStyles.miniButton.CalcSize(new GUIContent("Open Setup")).x + 10f;
                    var checkWidth = EditorStyles.miniButton.CalcSize(new GUIContent("Check Connection")).x + 10f;
                    var toggleRect = new Rect(content.xMax - toggleWidth, content.y, toggleWidth, content.height);
                    var setupRect = new Rect(toggleRect.x - setupWidth - 4f, content.y, setupWidth, content.height);
                    var checkRect = new Rect(setupRect.x - checkWidth - 4f, content.y, checkWidth, content.height);
                    var serverStopped = string.Equals(status.State, ArchitectorAgentIntegrationStates.Connected, StringComparison.Ordinal);
                    var warningContent = new GUIContent("MCP server is not running", status.Details);
                    var warningWidth = serverStopped ? EditorStyles.miniLabel.CalcSize(warningContent).x : 0f;
                    var warningRect = new Rect(checkRect.x - warningWidth - (serverStopped ? 4f : 0f), content.y, warningWidth, content.height);
                    var dotRect = new Rect(content.x, content.y, 18f, content.height);
                    var labelRect = new Rect(dotRect.xMax + 2f, content.y, Mathf.Max(0f, warningRect.x - dotRect.xMax - 8f), content.height);

                    var previousContentColor = GUI.contentColor;
                    GUI.contentColor = IntegrationStatusColor(status.State);
                    GUI.Label(dotRect, new GUIContent("●", status.Details), EditorStyles.label);
                    GUI.contentColor = previousContentColor;

                    var version = string.IsNullOrEmpty(status.PackageVersion) ? string.Empty : $" · v{status.PackageVersion}";
                    var port = status.Port > 0 ? $" · port:{status.Port}" : string.Empty;
                    GUI.Label(labelRect, new GUIContent($"{descriptor.displayName}{version}{port}", descriptor.displayName));
                    if (serverStopped)
                    {
                        GUI.contentColor = IntegrationStatusColor(status.State);
                        GUI.Label(warningRect, warningContent, EditorStyles.miniLabel);
                        GUI.contentColor = previousContentColor;
                    }
                    if (GUI.Button(checkRect, "Check Connection", EditorStyles.miniButton))
                        integrationStatuses[descriptor.id] = ArchitectorAgentIntegrationRegistry.Probe(descriptor, enabled);
                    if (GUI.Button(setupRect, "Open Setup", EditorStyles.miniButton))
                        ArchitectorAgentIntegrationRegistry.OpenSetup(descriptor);

                    var previousBackgroundColor = GUI.backgroundColor;
                    GUI.backgroundColor = enabled
                        ? new Color(1f, 0.62f, 0.56f)
                        : new Color(0.62f, 0.96f, 0.68f);
                    if (GUI.Button(toggleRect, toggleText, EditorStyles.miniButton))
                    {
                        SetIntegrationEnabled(agent, descriptor.id, !enabled);
                        integrationStatuses[descriptor.id] = ArchitectorAgentIntegrationRegistry.Probe(descriptor, !enabled);
                        GUI.changed = true;
                    }
                    GUI.backgroundColor = previousBackgroundColor;
                }

                EditorGUILayout.HelpBox(
                    "The Architector server comes with the package. Every optional Unity MCP package is isolated in its own adapter and may be removed without changing canonical architecture data.",
                    MessageType.None);
            }
        }

        private static void OpenIntegrations()
        {
            EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem("Window/Architector/Integrations");
        }

        private static Color IntegrationStatusColor(string state)
        {
            if (string.Equals(state, ArchitectorAgentIntegrationStates.Active, StringComparison.Ordinal))
                return new Color(0.30f, 0.82f, 0.38f);
            if (string.Equals(state, ArchitectorAgentIntegrationStates.Connected, StringComparison.Ordinal))
                return new Color(0.95f, 0.72f, 0.22f);
            if (string.Equals(state, ArchitectorAgentIntegrationStates.Unavailable, StringComparison.Ordinal))
                return new Color(0.95f, 0.52f, 0.24f);
            return new Color(0.55f, 0.55f, 0.55f);
        }

        private static bool IntegrationEnabled(ArchitectorAgentSettings agent, string id)
        {
            return agent.integrations.FirstOrDefault(record => string.Equals(record.id, id, StringComparison.Ordinal))?.enabled ?? true;
        }

        private static void SetIntegrationEnabled(ArchitectorAgentSettings agent, string id, bool enabled)
        {
            var record = agent.integrations.FirstOrDefault(candidate => string.Equals(candidate.id, id, StringComparison.Ordinal));
            if (record == null)
            {
                record = new ArchitectorAgentIntegrationRecord { id = id };
                agent.integrations.Add(record);
            }
            record.enabled = enabled;
        }

        private void Apply(ArchitectorSettingsDocument draft)
        {
            if (aiWork?.SettingsLocked == true)
            {
                ShowNotification(new GUIContent("AI work is in progress. Unlock Settings to edit manually."));
                return;
            }
            if (string.Equals(ArchitectorJson.Serialize(draft), ArchitectorJson.Serialize(repository.Settings), StringComparison.Ordinal))
                return;
            try
            {
                repository.UpdateSettings("Change Settings", settings =>
                {
                    settings.sourceIndex = draft.sourceIndex;
                    settings.geometry = draft.geometry;
                    settings.optimization = draft.optimization;
                    settings.arrange = draft.arrange;
                    settings.rules = draft.rules;
                    settings.palette = draft.palette;
                    settings.agent = draft.agent;
                });
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ShowNotification(new GUIContent(exception.Message));
            }
        }

        private static void CopyPalette(ArchitectorThemePaletteSettings source, ArchitectorThemePaletteSettings target)
        {
            target.workspace = source.workspace;
            target.grid = source.grid;
            target.node = source.node;
            target.nodeHeader = source.nodeHeader;
            target.nodeBorder = source.nodeBorder;
            target.entryPortalOutline = source.entryPortalOutline;
            target.exitPortalOutline = source.exitPortalOutline;
            target.titleText = source.titleText;
            target.typeText = source.typeText;
            target.edge = source.edge;
            target.dataFlowEdge = source.dataFlowEdge;
            target.eventEdge = source.eventEdge;
            target.callbackEdge = source.callbackEdge;
            target.selectedEdge = source.selectedEdge;
            target.incomingEdgeHighlight = source.incomingEdgeHighlight;
            target.selection = source.selection;
        }

        private readonly struct LabelWidthScope : IDisposable
        {
            private readonly float previous;

            public LabelWidthScope(float width)
            {
                previous = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = width;
            }

            public void Dispose()
            {
                EditorGUIUtility.labelWidth = previous;
            }
        }
    }
}
