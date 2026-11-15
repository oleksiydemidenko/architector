using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Architector.Editor.Agents
{
    public readonly struct ArchitectorSetupItem
    {
        public ArchitectorSetupItem(string name, bool passed, string details)
        {
            Name = name;
            Passed = passed;
            Details = details;
        }

        public string Name { get; }
        public bool Passed { get; }
        public string Details { get; }
    }

    public static class ArchitectorSetupCheck
    {
        public static IReadOnlyList<ArchitectorSetupItem> Run(ArchitectorRepository repository, ArchitectorTypeIndex typeIndex)
        {
            var roots = repository.Settings.sourceIndex.includedRoots;
            var missingRoots = roots
                .Where(root => !Directory.Exists(Path.Combine(ArchitectorHost.Current.ProjectRoot, root)))
                .ToArray();
            var typeCount = typeIndex.Types.Count;
            var items = new List<ArchitectorSetupItem>
            {
                new ArchitectorSetupItem(
                    "Source Roots",
                    roots.Count > 0 && missingRoots.Length == 0,
                    roots.Count == 0
                        ? "No source root is set in Settings."
                        : missingRoots.Length > 0
                            ? $"Missing folders: {string.Join(", ", missingRoots)}."
                            : string.Join(", ", roots)),
                new ArchitectorSetupItem(
                    "Indexed Types",
                    typeCount > 0,
                    typeCount > 0 ? typeCount.ToString() : "No types found. Check the source roots in Settings.")
            };
#if UNITY_EDITOR
            // The server binary, the bridge and the player setting are the Unity Editor's; the command line has none.
            items.AddRange(new[]
            {
                new ArchitectorSetupItem(
                    "MCP Server and CLI",
                    ArchitectorMcpInstaller.IsInstalled,
                    ArchitectorMcpInstaller.IsInstalled ? $"Version {ArchitectorMcpInstaller.Version}: one binary for both." : "Not installed."),
                new ArchitectorSetupItem(
                    "Editor Bridge",
                    ArchitectorAgentBridge.Port > 0,
                    ArchitectorAgentBridge.Port > 0 ? $"Listening on port {ArchitectorAgentBridge.Port}." : "Not running."),
                new ArchitectorSetupItem(
                    "Run In Background",
                    PlayerSettings.runInBackground,
                    PlayerSettings.runInBackground
                        ? "On."
                        : "Off. The Editor stops answering agents while its window has no focus.")
            });
#endif
            return items;
        }
    }
}
