using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorSettingsWindow : EditorWindow
    {
        private ArchitectorRepository repository;
        private ArchitectorAiWorkCoordinator aiWork;
        private string loadError = string.Empty;
        private Vector2 scroll;
        private bool includedRootsExpanded = true;
        private bool excludedRootsExpanded;
        private bool optimizationExpanded;
        private bool experimentalExpanded;
        private bool darkPaletteExpanded;
        private bool lightPaletteExpanded;
        private bool aiIntegrationExpanded = true;
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

        private void OnEnable()
        {
            titleContent = new GUIContent("Architector Settings");
            minSize = new Vector2(680f, 420f);
            if (!ArchitectorSession.TryGet(out var session, out loadError))
                return;
            repository = session.Repository;
            aiWork = session.AiWork;
            repository.Changed += Repaint;
            aiWork.Changed += Repaint;
        }

        private void OnDisable()
        {
            if (repository != null)
                repository.Changed -= Repaint;
            if (aiWork != null)
                aiWork.Changed -= Repaint;
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

            ArchitectorAiWorkUi.DrawSettingsBanner(aiWork);
            var aiLocked = aiWork?.SettingsLocked == true;
            if (aiLocked)
                ArchitectorAiWorkUi.ConsumeInputWhenLocked();

            var draft = ArchitectorJson.Clone(repository.Settings);
            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(aiLocked))
            using (new LabelWidthScope(Mathf.Clamp(position.width * 0.52f, 190f, 280f)))
            {
                scroll = EditorGUILayout.BeginScrollView(scroll);
                DrawSourceIndex(draft.sourceIndex);
                EditorGUILayout.Space(8f);
                DrawGeometry(draft.geometry);
                EditorGUILayout.Space(8f);
                DrawPalette(draft.palette);
                EditorGUILayout.Space(8f);
                DrawAiIntegration(draft.agent);
                EditorGUILayout.Space(8f);
                DrawOptimization(draft.optimization);
                EditorGUILayout.Space(8f);
                DrawExperimental(draft.optimization);
                EditorGUILayout.Space(8f);
                DrawUtilities();
                EditorGUILayout.EndScrollView();
            }

            if (EditorGUI.EndChangeCheck())
                Apply(draft);
        }

        private void DrawSourceIndex(ArchitectorSourceIndexSettings sourceIndex)
        {
            GUILayout.Label("C# Source Index", EditorStyles.boldLabel);
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

        private static void DrawGeometry(ArchitectorGeometrySettings geometry)
        {
            GUILayout.Label("Workspace Geometry", EditorStyles.boldLabel);
            geometry.minimumNodeGap = EditorGUILayout.FloatField("Minimum Node Gap", geometry.minimumNodeGap);
            geometry.gridSize = EditorGUILayout.FloatField("Grid Size", geometry.gridSize);
            geometry.snapToGrid = EditorGUILayout.Toggle("Snap To Grid", geometry.snapToGrid);
        }

        private void DrawOptimization(ArchitectorOptimizationSettings optimization)
        {
            optimizationExpanded = EditorGUILayout.Foldout(optimizationExpanded, "Optimization", true);
            if (!optimizationExpanded)
                return;

            using (new EditorGUI.IndentLevelScope())
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
            }
        }

        private void DrawExperimental(ArchitectorOptimizationSettings optimization)
        {
            experimentalExpanded = EditorGUILayout.Foldout(experimentalExpanded, "Experimental", true);
            if (!experimentalExpanded)
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                GUILayout.Label("Routing", EditorStyles.boldLabel);
                var gridRoutingLabel = new GUIContent(
                    "Grid Routing",
                    "Routes stable relationships through grid-cell centers with a one-cell attachment zone around nodes. The current router remains the fallback.");
                optimization.gridRouting = EditorGUILayout.Toggle(gridRoutingLabel, optimization.gridRouting);
                EditorGUILayout.HelpBox(
                    optimization.gridRouting
                        ? "Experimental cell routing is active. Disable it to restore the current visibility router immediately."
                        : "The proven visibility router remains active.",
                    MessageType.None);

                var label = new GUIContent(
                    "High Quality Drag Routing",
                    "Recalculates full obstacle-aware routes while dragging, except while dragged nodes overlap other nodes. This can reduce performance on large graphs.");
                optimization.highQualityDragRouting = EditorGUILayout.Toggle(label, optimization.highQualityDragRouting);
                EditorGUILayout.HelpBox(
                    optimization.highQualityDragRouting
                        ? "Full obstacle-aware routes are previewed during drag. Overlapping nodes temporarily use the fast preview."
                        : "Fast preview routes are used during drag. Full routes are recalculated and cached when the nodes are released.",
                    MessageType.None);
            }
        }

        private void DrawPalette(ArchitectorPaletteSettings palette)
        {
            GUILayout.Label("Color Palette", EditorStyles.boldLabel);
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
            GUILayout.Label("Utilities", EditorStyles.boldLabel);
            if (GUILayout.Button("Reveal Settings JSON"))
                EditorUtility.RevealInFinder(repository.Paths.SettingsPath);
        }

        private void DrawAiIntegration(ArchitectorAgentSettings agent)
        {
            aiIntegrationExpanded = EditorGUILayout.Foldout(aiIntegrationExpanded, "AI Integration", true);
            if (!aiIntegrationExpanded)
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                GUILayout.Label("This Computer", EditorStyles.miniBoldLabel);
                var preferences = ArchitectorAgentPreferences.Current;
                var free = EditorGUILayout.Popup(
                    new GUIContent("Agent Workflow", "Stored only for you on this computer."),
                    string.Equals(preferences.WorkflowMode, ArchitectorAgentWorkflowModes.Free, StringComparison.Ordinal) ? 1 : 0,
                    new[] { "Safe", "Free" }) == 1;
                var lockWindows = EditorGUILayout.Toggle(
                    new GUIContent("Lock Windows During Agent Work", "Stored only for you on this computer. Revision checks protect every write either way."),
                    preferences.LockWindowsDuringAgentWork);
                if (free != string.Equals(preferences.WorkflowMode, ArchitectorAgentWorkflowModes.Free, StringComparison.Ordinal))
                    preferences.WorkflowMode = free ? ArchitectorAgentWorkflowModes.Free : ArchitectorAgentWorkflowModes.Safe;
                if (lockWindows != preferences.LockWindowsDuringAgentWork)
                    preferences.LockWindowsDuringAgentWork = lockWindows;
                EditorGUILayout.HelpBox(
                    free
                        ? "Free: the agent implements unapproved designs in unlocked groups on its own. Approval is still set only when you ask for it."
                        : "Safe: the agent presents a new or changed design and waits for your go-ahead before implementing it. Approval is set only when you ask for it.",
                    MessageType.Info);

                EditorGUILayout.Space(6f);
                GUILayout.Label("Connections", EditorStyles.boldLabel);
                foreach (var descriptor in ArchitectorAgentIntegrationRegistry.Descriptors)
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
                    "Architector owns the gateway and contract. Every optional Unity MCP package is isolated in its own adapter and may be removed without changing canonical architecture data.",
                    MessageType.None);
            }
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
