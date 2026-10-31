using UnityEditor;
using UnityEngine;

namespace Architector.Editor.Agents
{
    public sealed class ArchitectorMcpSetupWindow : EditorWindow
    {
        private bool installing;
        private string error;
        private Vector2 scroll;

        [MenuItem("Window/Architector/MCP Server")]
        public static void Open()
        {
            var window = GetWindow<ArchitectorMcpSetupWindow>(true, "Architector MCP Server");
            window.minSize = new Vector2(560f, 420f);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Connect any MCP client to this project. No .NET SDK and no other Unity MCP package needed.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Bridge", ArchitectorAgentBridge.Port > 0 ? $"listening on port {ArchitectorAgentBridge.Port}" : "not running");
            EditorGUILayout.LabelField("Server", ArchitectorMcpInstaller.IsInstalled ? $"version {ArchitectorMcpInstaller.Version} installed" : "not installed");
            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(installing))
            {
                var label = installing ? "Downloading..." : ArchitectorMcpInstaller.IsInstalled ? "Reinstall Server" : "Install Server";
                if (GUILayout.Button(label, GUILayout.Height(26f)))
                    Install();
            }
            if (!string.IsNullOrEmpty(error))
                EditorGUILayout.HelpBox($"{error}\n{ArchitectorMcpInstaller.DownloadUrl}", MessageType.Error);
            if (!ArchitectorMcpInstaller.IsInstalled)
                return;

            scroll = EditorGUILayout.BeginScrollView(scroll);
            Snippet("Claude Code: run in a terminal", ArchitectorMcpInstaller.ClaudeCommand);
            Snippet("Codex: add to config.toml", ArchitectorMcpInstaller.CodexConfig);
            Snippet("Cursor and other clients: add to their MCP JSON file", ArchitectorMcpInstaller.JsonConfig);
            EditorGUILayout.EndScrollView();
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
            error = null;
            ArchitectorMcpInstaller.Install(result =>
            {
                installing = false;
                error = result;
                Repaint();
            });
        }
    }
}
