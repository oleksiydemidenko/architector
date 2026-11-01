using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorAgentWorkflowMode
    {
        public ArchitectorAgentWorkflowMode(
            string id,
            string displayName,
            string description,
            string agentRule,
            bool agentMayApprove = false,
            bool showsNotice = false)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            AgentRule = agentRule;
            AgentMayApprove = agentMayApprove;
            ShowsNotice = showsNotice;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string AgentRule { get; }
        public bool AgentMayApprove { get; }
        public bool ShowsNotice { get; }
    }

    public sealed class ArchitectorOptionalWorkflowMode
    {
        public ArchitectorOptionalWorkflowMode(string id, string displayName, string description, string packageName, string installUrl)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            PackageName = packageName;
            InstallUrl = installUrl;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string PackageName { get; }
        public string InstallUrl { get; }
    }

    public static class ArchitectorAgentWorkflowModes
    {
        public static readonly ArchitectorOptionalWorkflowMode[] Optional =
        {
            new(
                "lazy",
                "Lazy",
                "The agent writes and approves layers on its own. You do not need to understand the architecture in this mode. Architector is less effective without your control.",
                "com.oleksiydemidenko.architector-lazy-mode",
                "https://github.com/oleksiydemidenko/architector.git?path=/LazyMode~")
        };

        public const string Safe = "safe";
        public const string Free = "free";

        private static readonly List<ArchitectorAgentWorkflowMode> Modes = new()
        {
            new ArchitectorAgentWorkflowMode(
                Safe,
                "Safe",
                "Safe: the agent presents a new or changed design and waits for your go-ahead before implementing it. Architector asks you to confirm every approval the agent requests.",
                "Present a new or changed design and wait for the user's go-ahead before implementing it."),
            new ArchitectorAgentWorkflowMode(
                Free,
                "Free",
                "Free: the agent implements unapproved designs in unlocked groups on its own. Architector asks you to confirm every approval the agent requests.",
                "Implement unapproved designs in unlocked groups autonomously.")
        };

        public static IReadOnlyList<ArchitectorAgentWorkflowMode> All => Modes;

        public static void Register(ArchitectorAgentWorkflowMode mode)
        {
            Modes.RemoveAll(existing => string.Equals(existing.Id, mode.Id, StringComparison.Ordinal));
            Modes.Add(mode);
        }

        public static bool IsInstalled(string id)
        {
            return Modes.Any(mode => string.Equals(mode.Id, id, StringComparison.Ordinal));
        }

        public static ArchitectorAgentWorkflowMode Find(string id)
        {
            return Modes.FirstOrDefault(mode => string.Equals(mode.Id, id, StringComparison.Ordinal)) ?? Modes[0];
        }
    }

    public sealed class ArchitectorAgentPreferences
    {
        private static ArchitectorAgentPreferences current;
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
            current ??= new ArchitectorAgentPreferences(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));

        public ArchitectorAgentWorkflowMode Mode =>
            ArchitectorAgentWorkflowModes.Find(EditorPrefs.GetString(keyPrefix + "WorkflowMode", ArchitectorAgentWorkflowModes.Safe));

        public string WorkflowMode
        {
            get => Mode.Id;
            set
            {
                var mode = ArchitectorAgentWorkflowModes.Find(value).Id;
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
