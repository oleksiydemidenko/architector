using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Architector.Editor.Agents
{
    public sealed class ArchitectorIntegrationsWindow : EditorWindow
    {
        private const string Title = "Architector Integrations";
        private const string ExpandedKey = "Architector.Integrations.Expanded.";

        private ArchitectorRepository repository;
        private ArchitectorAiWorkCoordinator aiWork;
        private string loadError = string.Empty;
        private Vector2 scroll;
        private bool installing;
        private string installError;
        private bool manualSetupExpanded;
        private IReadOnlyList<ArchitectorSetupItem> setupItems;
        private ArchitectorTypeIndex typeIndex;
        private Request packageRequest;
        private string packageError;
        private readonly Dictionary<string, ArchitectorAgentIntegrationProbeResult> probes = new(StringComparer.Ordinal);
        private readonly Dictionary<ArchitectorMcpClient, ArchitectorMcpClientState> clientStates = new();
        private readonly Dictionary<ArchitectorMcpClient, Task<ArchitectorMcpClientState>> clientTasks = new();

        [MenuItem("Window/Architector/Integrations")]
        public static void Open()
        {
            var master = Resources.FindObjectsOfTypeAll<ArchitectorMasterWindow>()
                .FirstOrDefault(candidate => candidate.IsMasterWindow);
            if (master != null)
                master.Focus();
            var window = master != null
                ? GetWindow<ArchitectorIntegrationsWindow>(Title, true, typeof(ArchitectorMasterWindow))
                : GetWindow<ArchitectorIntegrationsWindow>();
            window.titleContent = new GUIContent(Title);
            window.minSize = new Vector2(560f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(Title);
            minSize = new Vector2(560f, 420f);
            ArchitectorAgentPreferences.Changed += OnChanged;
            ArchitectorPackageUpdates.Changed += OnChanged;
            if (!ArchitectorSession.TryGet(out var session, out loadError))
                return;
            repository = session.Repository;
            aiWork = session.AiWork;
            typeIndex = session.TypeIndex;
            repository.Changed += OnChanged;
            foreach (var client in ArchitectorMcpClient.All)
                RefreshClient(client);
        }

        private void OnDisable()
        {
            ArchitectorAgentPreferences.Changed -= OnChanged;
            ArchitectorPackageUpdates.Changed -= OnChanged;
            if (repository != null)
                repository.Changed -= OnChanged;
        }

        private void OnChanged()
        {
            probes.Clear();
            setupItems = null;
            Repaint();
        }

        private void OnFocus()
        {
            OnChanged();
        }

        private void Update()
        {
            foreach (var pair in clientTasks.Where(pair => pair.Value.IsCompleted).ToArray())
            {
                clientStates[pair.Key] = pair.Value.Status == TaskStatus.RanToCompletion
                    ? pair.Value.Result
                    : new ArchitectorMcpClientState(ArchitectorMcpClientStates.Failed, pair.Value.Exception?.GetBaseException().Message);
                clientTasks.Remove(pair.Key);
                Repaint();
            }
            if (packageRequest is { IsCompleted: true })
            {
                packageError = packageRequest.Status == StatusCode.Failure ? packageRequest.Error?.message : null;
                packageRequest = null;
                OnChanged();
            }
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

            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUILayout.VerticalScope(ArchitectorFoldoutBox.ContentStyle))
            {
                EditorGUILayout.LabelField("Choose how agents reach Architector. The Architector server comes with the package; the others are optional bridges.", EditorStyles.wordWrappedLabel);
                if (packageRequest != null)
                    EditorGUILayout.HelpBox("Unity is updating the package.", MessageType.Info);
                if (!string.IsNullOrEmpty(packageError))
                    EditorGUILayout.HelpBox(packageError, MessageType.Error);
                EditorGUILayout.Space(6f);
                DrawSetupCheck();
                foreach (var descriptor in ArchitectorAgentIntegrationRegistry.Descriptors)
                {
                    var open = SessionState.GetBool(ExpandedKey + descriptor.id, false);
                    using (var box = new ArchitectorFoldoutBox(descriptor.displayName, ref open))
                    {
                        if (box.Expanded)
                        {
                            if (descriptor.builtIn)
                                DrawServer();
                            else
                                DrawBridge(descriptor);
                        }
                    }
                    SessionState.SetBool(ExpandedKey + descriptor.id, open);
                    if (descriptor.builtIn)
                        DrawOptionalModes();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSetupCheck()
        {
            var setupExpanded = SessionState.GetBool(ExpandedKey + "setup", true);
            using var box = new ArchitectorFoldoutBox("Setup Check", ref setupExpanded);
            SessionState.SetBool(ExpandedKey + "setup", setupExpanded);
            if (!box.Expanded)
                return;
            setupItems ??= ArchitectorSetupCheck.Run(repository, typeIndex);
            foreach (var item in setupItems)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var icon = EditorGUIUtility.IconContent(item.Passed ? "TestPassed" : "console.warnicon.sml").image;
                    GUILayout.Label(new GUIContent(item.Name, icon), GUILayout.Width(170f), GUILayout.Height(18f));
                    GUILayout.Label(item.Details, EditorStyles.wordWrappedMiniLabel);
                }
            }
            if (GUILayout.Button("Check Again", EditorStyles.miniButton, GUILayout.Width(100f)))
                setupItems = null;
        }

        private void DrawOptionalModes()
        {
            foreach (var mode in ArchitectorAgentWorkflowModes.Optional)
            {
                var open = SessionState.GetBool(ExpandedKey + mode.Id, false);
                using (var box = new ArchitectorFoldoutBox($"{mode.DisplayName} Mode", ref open))
                {
                    if (box.Expanded)
                        DrawOptionalMode(mode);
                }
                SessionState.SetBool(ExpandedKey + mode.Id, open);
            }
        }

        private void DrawOptionalMode(ArchitectorOptionalWorkflowMode mode)
        {
            var installed = ArchitectorAgentWorkflowModes.IsInstalled(mode.Id);
            var preferences = ArchitectorAgentPreferences.Current;
            var active = installed && string.Equals(preferences.WorkflowMode, mode.Id, StringComparison.Ordinal);
            EditorGUILayout.LabelField(mode.Description, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Package", installed ? "installed" : "not installed");
            EditorGUILayout.LabelField("Mode", active ? "on" : "off");

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(packageRequest != null || ArchitectorPackageUpdates.IsInstalling))
            {
                if (installed)
                {
                    if (GUILayout.Button(active ? "Turn Off" : "Turn On", GUILayout.Height(24f)))
                        preferences.WorkflowMode = active ? ArchitectorAgentWorkflowModes.Safe : mode.Id;
                    if (GUILayout.Button("Remove Package", GUILayout.Height(24f)))
                    {
                        packageError = null;
                        packageRequest = Client.Remove(mode.PackageName);
                        ArchitectorPackageUpdates.TrackPackageOperation(packageRequest);
                    }
                }
                else if (GUILayout.Button("Install Package", GUILayout.Height(24f)))
                {
                    packageError = null;
                    packageRequest = Client.Add($"{mode.InstallUrl}#v{ArchitectorMcpInstaller.Version}");
                    ArchitectorPackageUpdates.TrackPackageOperation(packageRequest);
                }
            }
        }

        private void DrawServer()
        {
            EditorGUILayout.LabelField("Bridge", ArchitectorAgentBridge.Port > 0 ? $"listening on port {ArchitectorAgentBridge.Port}" : "not running");
            EditorGUILayout.LabelField("Server", ArchitectorMcpInstaller.IsInstalled ? $"version {ArchitectorMcpInstaller.Version} installed" : "not installed");
            using (new EditorGUI.DisabledScope(installing || ArchitectorPackageUpdates.IsInstalling || packageRequest != null))
            {
                var label = installing ? "Downloading..." : ArchitectorMcpInstaller.IsInstalled ? "Reinstall Server" : "Install Server";
                if (GUILayout.Button(label, GUILayout.Height(26f)))
                    Install();
            }
            if (!string.IsNullOrEmpty(installError))
                EditorGUILayout.HelpBox($"{installError}\n{ArchitectorMcpInstaller.DownloadUrl}", MessageType.Error);
            if (!ArchitectorMcpInstaller.IsInstalled)
                return;

            EditorGUILayout.Space(6f);
            GUILayout.Label("Agents", EditorStyles.boldLabel);
            foreach (var client in ArchitectorMcpClient.All)
                DrawClient(client);

            EditorGUILayout.Space(4f);
            manualSetupExpanded = EditorGUILayout.Foldout(manualSetupExpanded, "Manual Setup", true);
            if (!manualSetupExpanded)
                return;
            Snippet("Claude Code: run in a terminal", ArchitectorMcpInstaller.ClaudeCommand);
            Snippet("Codex: add to config.toml", ArchitectorMcpInstaller.CodexConfig);
            Snippet("Cursor and other clients: add to their MCP JSON file", ArchitectorMcpInstaller.JsonConfig);
        }

        private void DrawClient(ArchitectorMcpClient client)
        {
            clientStates.TryGetValue(client, out var state);
            var busy = clientTasks.ContainsKey(client);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(client.DisplayName, GUILayout.Width(120f));
                GUILayout.Label(new GUIContent(busy ? "Checking..." : StateText(state), state.Details), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(busy || string.Equals(state.State, ArchitectorMcpClientStates.CliNotFound, StringComparison.Ordinal)))
                {
                    var connected = string.Equals(state.State, ArchitectorMcpClientStates.Connected, StringComparison.Ordinal);
                    if (GUILayout.Button(connected ? "Disconnect" : "Connect", EditorStyles.miniButton, GUILayout.Width(80f)))
                        RunClient(client, !connected);
                }
            }
            if (string.Equals(state.State, ArchitectorMcpClientStates.Failed, StringComparison.Ordinal) && !string.IsNullOrEmpty(state.Details))
                EditorGUILayout.HelpBox(state.Details, MessageType.Error);
        }

        private static string StateText(ArchitectorMcpClientState state)
        {
            return state.State switch
            {
                ArchitectorMcpClientStates.Connected => "Connected",
                ArchitectorMcpClientStates.OtherVersion => "Connected to another version",
                ArchitectorMcpClientStates.CliNotFound => "Command line tool not found",
                ArchitectorMcpClientStates.Failed => "Failed",
                _ => "Not connected"
            };
        }

        private void RefreshClient(ArchitectorMcpClient client)
        {
            var binary = ArchitectorMcpInstaller.BinaryPath;
            var root = ArchitectorMcpInstaller.ProjectRoot;
            clientTasks[client] = Task.Run(() => ArchitectorMcpInstaller.CheckClient(client, binary, root));
        }

        private void RunClient(ArchitectorMcpClient client, bool connect)
        {
            var binary = ArchitectorMcpInstaller.BinaryPath;
            var root = ArchitectorMcpInstaller.ProjectRoot;
            clientTasks[client] = Task.Run(() => connect
                ? ArchitectorMcpInstaller.ConnectClient(client, binary, root)
                : ArchitectorMcpInstaller.DisconnectClient(client, binary, root));
        }

        private void DrawBridge(ArchitectorAgentIntegrationDescriptor descriptor)
        {
            if (!probes.TryGetValue(descriptor.id, out var probe))
            {
                probe = ArchitectorAgentIntegrationRegistry.Probe(descriptor, true);
                probes[descriptor.id] = probe;
            }
            var packageInstalled = !string.IsNullOrEmpty(probe.PackageVersion);
            var added = ArchitectorAgentIntegrationRegistry.IsAdded(repository.Settings.agent, descriptor);
            EditorGUILayout.LabelField("Package", packageInstalled ? $"version {probe.PackageVersion} installed" : "not installed");
            EditorGUILayout.LabelField("Connection", added ? "added to this project" : "not added");

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(aiWork?.SettingsLocked == true || packageRequest != null || ArchitectorPackageUpdates.IsInstalling))
            {
                if (!packageInstalled && !string.IsNullOrEmpty(descriptor.installUrl)
                    && GUILayout.Button("Install Package", GUILayout.Height(24f)))
                {
                    packageError = null;
                    packageRequest = Client.Add(descriptor.installUrl);
                    ArchitectorPackageUpdates.TrackPackageOperation(packageRequest);
                }
                if (GUILayout.Button(added ? "Remove Connection" : "Add Connection", GUILayout.Height(24f)))
                    SetAdded(descriptor, !added);
                if (GUILayout.Button(packageInstalled ? "Open Setup" : "Open Documentation", GUILayout.Height(24f)))
                    ArchitectorAgentIntegrationRegistry.OpenSetup(descriptor);
            }
        }

        private void SetAdded(ArchitectorAgentIntegrationDescriptor descriptor, bool added)
        {
            repository.UpdateSettings(added ? "Add Connection" : "Remove Connection", settings =>
            {
                settings.agent.integrations.RemoveAll(record => string.Equals(record.id, descriptor.id, StringComparison.Ordinal));
                if (added)
                    settings.agent.integrations.Add(new ArchitectorAgentIntegrationRecord { id = descriptor.id, enabled = true });
            });
        }

        private static void Snippet(string title, string text)
        {
            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(title, EditorStyles.boldLabel);
                if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(60f)))
                    EditorGUIUtility.systemCopyBuffer = text;
            }
            EditorGUILayout.SelectableLabel(text, EditorStyles.textArea, GUILayout.Height(EditorStyles.textArea.CalcHeight(new GUIContent(text), 520f) + 4f));
        }

        private void Install()
        {
            installing = true;
            installError = null;
            ArchitectorMcpInstaller.Install(result =>
            {
                if (!this)
                    return;
                installing = false;
                installError = result;
                foreach (var client in ArchitectorMcpClient.All)
                    RefreshClient(client);
                OnChanged();
            });
        }
    }
}
