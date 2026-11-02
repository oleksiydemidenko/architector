using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed partial class ArchitectorSettingsWindow
    {
        [NonSerialized] private bool updateExpanded;
        private string updateShownTag;
        private Vector2 updateNotesScroll;

        private void CheckForUpdatesOnOpen()
        {
            ArchitectorPackageUpdates.RefreshInstallation();
            ArchitectorPackageUpdates.EnsureChecked();
        }

        private void OnFocus()
        {
            ArchitectorPackageUpdates.RefreshInstallation();
        }

        private void OnUpdatesChanged()
        {
            var tag = ArchitectorPackageUpdates.State.availableTag;
            if (ArchitectorPackageUpdates.UpdateAvailable && tag != updateShownTag)
            {
                updateShownTag = tag;
                updateExpanded = true;
            }
            Repaint();
        }

        private void DrawUpdates()
        {
            var available = ArchitectorPackageUpdates.UpdateAvailable;
            var title = available
                ? new GUIContent("Update — " + ArchitectorPackageUpdates.State.availableTag + " available", EditorGUIUtility.IconContent("console.warnicon.sml").image)
                : new GUIContent("Update");
            using var box = new ArchitectorFoldoutBox(title, ref updateExpanded,
                ArchitectorPackageUpdates.IsCurrent ? EditorGUIUtility.IconContent("TestPassed").image : null);
            if (!box.Expanded)
                return;

            var state = ArchitectorPackageUpdates.State;
            EditorGUILayout.LabelField("Installed", ArchitectorPackagePaths.Version);
            EditorGUILayout.LabelField("Available", state.latestTag ?? "—");
            if (DateTime.TryParse(state.checkedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt))
                EditorGUILayout.LabelField("Last Check", checkedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            using (new EditorGUI.DisabledScope(ArchitectorPackageUpdates.IsBusy))
            {
                var automatic = EditorGUILayout.Toggle("Check Automatically", ArchitectorPackageUpdates.AutomaticChecks);
                if (automatic != ArchitectorPackageUpdates.AutomaticChecks)
                    ArchitectorPackageUpdates.AutomaticChecks = automatic;
                var prereleases = EditorGUILayout.Toggle("Include Prereleases", ArchitectorPackageUpdates.IncludePrereleases);
                if (prereleases != ArchitectorPackageUpdates.IncludePrereleases)
                    ArchitectorPackageUpdates.IncludePrereleases = prereleases;
            }

            var sourceWarning = ArchitectorPackageUpdates.SourceWarning;
            if (sourceWarning.Length > 0)
                EditorGUILayout.HelpBox(sourceWarning, MessageType.Warning);
            if (!string.IsNullOrEmpty(state.error))
                EditorGUILayout.HelpBox(state.error, MessageType.Error);
            if (!string.IsNullOrEmpty(state.notice))
                EditorGUILayout.HelpBox(state.notice, MessageType.Info);
            if (ArchitectorPackageUpdates.IsBusy)
                EditorGUILayout.HelpBox(ArchitectorPackageUpdates.IsChecking ? "Checking releases…"
                    : ArchitectorPackageUpdates.IsInstalling ? "Updating Architector…" : "Another installation is in progress…", MessageType.Info);

            var blocker = ArchitectorPackageUpdates.InstallBlocker(aiWork);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(ArchitectorPackageUpdates.IsBusy || sourceWarning.Length > 0
                    || EditorApplication.isCompiling || EditorApplication.isUpdating))
                    if (GUILayout.Button("Check for Updates"))
                        ArchitectorPackageUpdates.Check();
                if (GUILayout.Button("Release Details"))
                    Application.OpenURL(available ? ArchitectorPackageUpdates.ReleaseUrl : "https://github.com/oleksiydemidenko/architector/releases");
                using (new EditorGUI.DisabledScope(!available || blocker.Length > 0))
                    if (GUILayout.Button(available ? "Update to " + state.availableTag : "Update"))
                        ConfirmPackageUpdate();
            }
            if (available && blocker.Length > 0)
                EditorGUILayout.HelpBox(blocker, MessageType.Info);
            EditorGUILayout.HelpBox(
                "Installation is manual. An installed Lazy Mode package is updated together with Architector. Updating recompiles the Editor. Afterward, open Integrations to install the matching MCP server and reconnect your clients. Commit the package manifest and lock file to share the update with the team.",
                MessageType.None);
            if (GUILayout.Button("Open Integrations", EditorStyles.miniButton))
                OpenIntegrations();
            if (available && !string.IsNullOrEmpty(state.releaseNotes))
            {
                EditorGUILayout.LabelField("Changes", EditorStyles.boldLabel);
                updateNotesScroll = EditorGUILayout.BeginScrollView(updateNotesScroll, GUILayout.MaxHeight(240f));
                EditorGUILayout.LabelField(state.releaseNotes, EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndScrollView();
            }
        }

        private void ConfirmPackageUpdate()
        {
            if (!EditorUtility.DisplayDialog(
                    "Update Architector",
                    "Finish any unsaved text edits before continuing. Unity will update Architector and its installed Lazy Mode package, then recompile and reload assemblies. The MCP server and client connections are updated separately in Integrations.",
                    "Update", "Cancel"))
                return;
            GUI.FocusControl(null);
            ArchitectorPackageUpdates.Install(aiWork);
        }
    }
}
