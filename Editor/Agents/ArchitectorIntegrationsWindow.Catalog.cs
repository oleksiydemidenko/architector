using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor.Agents
{
    public sealed partial class ArchitectorIntegrationsWindow
    {
        private const int IntegrationsPerPage = 6;
        [SerializeField] private string integrationSearch = string.Empty;
        [SerializeField] private string integrationTag = string.Empty;
        [SerializeField] private int integrationPage;
        private List<ArchitectorIntegrationEntry> catalog;
        private IReadOnlyList<ArchitectorIntegrationEntry> filteredIntegrations;
        private string[] integrationTags;

        private void EnsureCatalog()
        {
            if (catalog != null)
                return;
            catalog = new List<ArchitectorIntegrationEntry>();
            foreach (var mode in ArchitectorAgentWorkflowModes.Optional)
                catalog.Add(new ArchitectorIntegrationEntry(mode.Id, mode.DisplayName + " Mode", new[] { "Extension" },
                    () => DrawOptionalMode(mode), updates: ArchitectorPackageUpdateProvider.Instance));
            foreach (var descriptor in ArchitectorAgentIntegrationRegistry.Descriptors)
                catalog.Add(new ArchitectorIntegrationEntry(descriptor.id, descriptor.displayName, descriptor.tags,
                    () => { if (descriptor.builtIn) DrawServer(); else DrawBridge(descriptor); }, descriptor.builtIn ? 0 : 10,
                    descriptor.builtIn ? ArchitectorPackageUpdateProvider.Instance : descriptor.updates));
            catalog.AddRange(ArchitectorIntegrationCatalog.Entries);
            integrationTags = catalog.SelectMany(entry => entry.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void DrawIntegrationList()
        {
            EnsureCatalog();
            DrawIntegrationTags();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                integrationSearch = EditorGUILayout.TextField(new GUIContent("Search", "Search names or tags, e.g. Lazy, MCP or tag:Extension."),
                    integrationSearch, EditorStyles.toolbarSearchField);
                if (EditorGUI.EndChangeCheck())
                    ResetIntegrationSearch();
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(integrationSearch)))
                    if (GUILayout.Button(new GUIContent("×", "Clear search"), EditorStyles.miniButton, GUILayout.Width(24f)))
                    {
                        integrationSearch = string.Empty;
                        GUI.FocusControl(null);
                        ResetIntegrationSearch();
                    }
            }

            filteredIntegrations ??= ArchitectorIntegrationCatalog.Filter(catalog, integrationSearch, integrationTag);
            DrawIntegrationPager();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (filteredIntegrations.Count == 0)
                EditorGUILayout.HelpBox("No integrations match this search.", MessageType.Info);
            foreach (var entry in filteredIntegrations.Skip(integrationPage * IntegrationsPerPage).Take(IntegrationsPerPage))
            {
                var expanded = SessionState.GetBool(ExpandedKey + entry.Id, false);
                var update = entry.Updates == null ? null : ArchitectorIntegrationUpdates.State(entry.Updates.Id).Update;
                var title = entry.DisplayName + "   ·   " + string.Join(", ", entry.Tags);
                if (update?.Available == true)
                    title += "   ·   " + ArchitectorFoldoutBox.WarningText(update.availableVersion + " available");
                using (var box = new ArchitectorFoldoutBox(new GUIContent(title), ref expanded,
                           update?.Available == true ? EditorGUIUtility.IconContent("console.warnicon.sml").image : null))
                {
                    if (box.Expanded)
                    {
                        entry.DrawDetails();
                        DrawIntegrationUpdates(entry);
                    }
                }
                SessionState.SetBool(ExpandedKey + entry.Id, expanded);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawIntegrationUpdates(ArchitectorIntegrationEntry entry)
        {
            if (entry.Updates == null)
                return;
            var state = ArchitectorIntegrationUpdates.State(entry.Updates.Id);
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Updates", EditorStyles.boldLabel);
            if (state.Update != null)
            {
                EditorGUILayout.LabelField("Installed", state.Update.installedVersion ?? "—");
                EditorGUILayout.LabelField("Available", state.Update.availableVersion ?? "—");
                if (!string.IsNullOrEmpty(state.Update.details))
                    EditorGUILayout.HelpBox(state.Update.details, state.Update.compatible ? MessageType.Info : MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(state.Error))
                EditorGUILayout.HelpBox(state.Error, MessageType.Error);
            if (!string.IsNullOrEmpty(state.Notice))
                EditorGUILayout.HelpBox(state.Notice, MessageType.Info);
            if (state.Checking || state.Installing)
                EditorGUILayout.LabelField(state.Checking ? "Checking…" : "Updating…");
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(ArchitectorIntegrationUpdates.IsBusy || ArchitectorPackageUpdates.IsBusy))
            {
                if (GUILayout.Button("Check for Updates"))
                    ArchitectorIntegrationUpdates.Check(entry.Updates);
                using (new EditorGUI.DisabledScope(state.Update?.Available != true || ArchitectorPackageUpdates.OperationBlocker(aiWork).Length > 0))
                    if (GUILayout.Button("Update"))
                    {
                        if (EditorUtility.DisplayDialog("Update " + entry.DisplayName,
                                "Install " + state.Update.availableVersion + "? Unity may reload assemblies.", "Update", "Cancel"))
                            ArchitectorIntegrationUpdates.Install(entry.Updates, aiWork);
                    }
            }
            if (state.Update?.Available == true)
            {
                var notesKey = ExpandedKey + entry.Id + ".notes";
                SessionState.SetBool(notesKey, ArchitectorReleaseNotesView.Draw(state.Update.releaseNotes, SessionState.GetBool(notesKey, true)));
            }
        }

        private void DrawIntegrationTags()
        {
            var usedWidth = 0f;
            EditorGUILayout.BeginHorizontal();
            foreach (var tag in new[] { string.Empty }.Concat(integrationTags))
            {
                var label = string.IsNullOrEmpty(tag) ? "All" : tag;
                var width = EditorStyles.miniButton.CalcSize(new GUIContent(label)).x + 12f;
                if (usedWidth > 0f && usedWidth + width > position.width - 32f)
                {
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    usedWidth = 0f;
                }
                var selected = string.Equals(integrationTag, tag, StringComparison.OrdinalIgnoreCase);
                if (GUILayout.Toggle(selected, label, EditorStyles.miniButton, GUILayout.Width(width)) && !selected)
                {
                    integrationTag = tag;
                    ResetIntegrationSearch();
                }
                usedWidth += width + 4f;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawIntegrationPager()
        {
            var pageCount = Math.Max(1, (filteredIntegrations.Count + IntegrationsPerPage - 1) / IntegrationsPerPage);
            integrationPage = Mathf.Clamp(integrationPage, 0, pageCount - 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Integrations ({filteredIntegrations.Count})", EditorStyles.boldLabel);
                if (pageCount <= 1)
                    return;
                using (new EditorGUI.DisabledScope(integrationPage == 0))
                    if (GUILayout.Button("‹", EditorStyles.miniButtonLeft, GUILayout.Width(24f)))
                        ChangeIntegrationPage(integrationPage - 1);
                GUILayout.Label($"{integrationPage + 1} / {pageCount}", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(54f));
                using (new EditorGUI.DisabledScope(integrationPage >= pageCount - 1))
                    if (GUILayout.Button("›", EditorStyles.miniButtonRight, GUILayout.Width(24f)))
                        ChangeIntegrationPage(integrationPage + 1);
            }
            if (Event.current.type == EventType.KeyDown && !EditorGUIUtility.editingTextField
                && (Event.current.keyCode == KeyCode.PageDown || Event.current.keyCode == KeyCode.PageUp))
            {
                ChangeIntegrationPage(Mathf.Clamp(integrationPage + (Event.current.keyCode == KeyCode.PageDown ? 1 : -1), 0, pageCount - 1));
                Event.current.Use();
            }
        }

        private void ChangeIntegrationPage(int page)
        {
            integrationPage = page;
            scroll = Vector2.zero;
            Repaint();
        }

        private void ResetIntegrationSearch()
        {
            filteredIntegrations = null;
            ChangeIntegrationPage(0);
        }
    }
}
