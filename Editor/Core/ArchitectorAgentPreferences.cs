using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public static class ArchitectorAgentWorkflowModes
    {
        public const string Safe = "safe";
        public const string Free = "free";
    }

    public sealed class ArchitectorAgentPreferences
    {
        private readonly string keyPrefix;

        public ArchitectorAgentPreferences(string projectRoot)
        {
            var root = Path.GetFullPath(projectRoot ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(root));
            keyPrefix = $"Architector.{BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty)}.";
        }

        public static event Action Changed;

        public static ArchitectorAgentPreferences Current =>
            new(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));

        public string WorkflowMode
        {
            get => string.Equals(EditorPrefs.GetString(keyPrefix + "WorkflowMode", ArchitectorAgentWorkflowModes.Safe),
                ArchitectorAgentWorkflowModes.Free,
                StringComparison.Ordinal)
                ? ArchitectorAgentWorkflowModes.Free
                : ArchitectorAgentWorkflowModes.Safe;
            set
            {
                var mode = string.Equals(value, ArchitectorAgentWorkflowModes.Free, StringComparison.Ordinal)
                    ? ArchitectorAgentWorkflowModes.Free
                    : ArchitectorAgentWorkflowModes.Safe;
                if (string.Equals(mode, WorkflowMode, StringComparison.Ordinal))
                    return;
                EditorPrefs.SetString(keyPrefix + "WorkflowMode", mode);
                Changed?.Invoke();
            }
        }

        public bool LockWindowsDuringAgentWork
        {
            get => EditorPrefs.GetBool(keyPrefix + "LockWindowsDuringAgentWork", true);
            set
            {
                if (value == LockWindowsDuringAgentWork)
                    return;
                EditorPrefs.SetBool(keyPrefix + "LockWindowsDuringAgentWork", value);
                Changed?.Invoke();
            }
        }

        public void Clear()
        {
            EditorPrefs.DeleteKey(keyPrefix + "WorkflowMode");
            EditorPrefs.DeleteKey(keyPrefix + "LockWindowsDuringAgentWork");
            Changed?.Invoke();
        }
    }
}
