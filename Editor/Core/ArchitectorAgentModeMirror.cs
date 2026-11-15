using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Architector.Editor
{
    // The user's workflow mode lives in the Editor (EditorPrefs per user and project), and optional modes such as Lazy
    // register from their own package. A command line without the Editor sees neither, so the Editor mirrors the
    // resolved mode to Library/Architector/agent-mode.json (per checkout and per user, like the preference) and the
    // command line adopts it: in Lazy mode it approves layers on its own as the Editor would. No file means Safe.
    public static class ArchitectorAgentModeMirror
    {
        public const string FileName = "agent-mode.json";

        public static string PathFor(string projectRoot) => Path.Combine(projectRoot, "Library", "Architector", FileName);

        public static string Text(ArchitectorAgentWorkflowMode mode)
        {
            return new JObject
            {
                ["workflowMode"] = mode.Id,
                ["displayName"] = mode.DisplayName,
                ["description"] = mode.Description,
                ["agentRule"] = mode.AgentRule,
                ["agentMayApprove"] = mode.AgentMayApprove,
                ["showsNotice"] = mode.ShowsNotice
            }.ToString(Formatting.Indented);
        }

        // Writes the file only when its text changes; returns false when it could not be written.
        public static bool Write(string projectRoot, ArchitectorAgentWorkflowMode mode)
        {
            var path = PathFor(projectRoot);
            var text = Text(mode);
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == text)
                    return true;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static bool TryRead(string projectRoot, out ArchitectorAgentWorkflowMode mode)
        {
            mode = null;
            var path = PathFor(projectRoot);
            try
            {
                if (!File.Exists(path))
                    return false;
                var data = JObject.Parse(File.ReadAllText(path));
                var id = (string)data["workflowMode"];
                if (string.IsNullOrEmpty(id))
                    return false;
                mode = new ArchitectorAgentWorkflowMode(
                    id,
                    (string)data["displayName"] ?? id,
                    (string)data["description"] ?? string.Empty,
                    (string)data["agentRule"] ?? string.Empty,
                    (bool?)data["agentMayApprove"] ?? false,
                    (bool?)data["showsNotice"] ?? false);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidCastException or FormatException)
            {
                return false;
            }
        }

        // Without the Editor: registers the mirrored mode when this process does not know it and makes it current.
        public static bool Adopt(string projectRoot)
        {
            if (!TryRead(projectRoot, out var mode))
                return false;
            if (!ArchitectorAgentWorkflowModes.IsInstalled(mode.Id))
                ArchitectorAgentWorkflowModes.Register(mode);
            ArchitectorAgentPreferences.Current.WorkflowMode = mode.Id;
            return true;
        }
    }
}
