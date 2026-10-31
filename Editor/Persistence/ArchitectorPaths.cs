using System;
using System.IO;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorPaths
    {
        public const string DataDirectoryName = "Architector";
        public const string ProjectFileName = "architector.project.json";
        public const string SettingsFileName = "architector.settings.json";
        public const string DigestFileName = "Architecture.digest.md";
        public const string GroupsDirectoryName = "Groups";
        public const string LayersDirectoryName = "Layers";
        public const string LayoutsDirectoryName = "Layouts";
        public const string LinksDirectoryName = "Links";
        public const string BaselinesDirectoryName = "Baselines";
        public const string GroupFileSuffix = ".group.json";
        public const string LayerFileSuffix = ".layer.json";
        public const string LayoutFileSuffix = ".layout.json";
        public const string LinkFileSuffix = ".link.json";
        public const string BaselineFileSuffix = ".baseline.json";

        public ArchitectorPaths(string repositoryRoot)
        {
            RepositoryRoot = Path.GetFullPath(repositoryRoot);
            DataRoot = Path.Combine(RepositoryRoot, DataDirectoryName);
            ProjectPath = Path.Combine(DataRoot, ProjectFileName);
            SettingsPath = Path.Combine(DataRoot, SettingsFileName);
            GroupsRoot = Path.Combine(DataRoot, GroupsDirectoryName);
            LayersRoot = Path.Combine(DataRoot, LayersDirectoryName);
            LayoutsRoot = Path.Combine(DataRoot, LayoutsDirectoryName);
            LinksRoot = Path.Combine(DataRoot, LinksDirectoryName);
            UserStateRoot = Path.Combine(RepositoryRoot, "Library", DataDirectoryName);
            UserStatePath = Path.Combine(UserStateRoot, "user-state.json");
            AiWorkRecoveryPath = Path.Combine(UserStateRoot, "ai-work-session.json");
        }

        public string RepositoryRoot { get; }
        public string DataRoot { get; }
        public string ProjectPath { get; }
        public string SettingsPath { get; }
        public string GroupsRoot { get; }
        public string LayersRoot { get; }
        public string LayoutsRoot { get; }
        public string LinksRoot { get; }
        public string UserStateRoot { get; }
        public string UserStatePath { get; }
        public string AiWorkRecoveryPath { get; }

        public static ArchitectorPaths ForCurrentProject()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return new ArchitectorPaths(root);
        }

        public static string GroupPath(string groupId) => $"{GroupsDirectoryName}/{groupId}{GroupFileSuffix}";
        public static string LayerPath(string layerId) => $"{LayersDirectoryName}/{layerId}{LayerFileSuffix}";
        public static string LayoutPath(string layerId) => $"{LayoutsDirectoryName}/{layerId}{LayoutFileSuffix}";
        public static string LinkPath(string pairId) => $"{LinksDirectoryName}/{pairId}{LinkFileSuffix}";
        public static string BaselinePath(string layerId) => $"{BaselinesDirectoryName}/{layerId}{BaselineFileSuffix}";
        public static string GroupPath(string groupId, string name) => Labeled(GroupsDirectoryName, groupId, EntryLabel(name), GroupFileSuffix);
        public static string LayerPath(string layerId, string name) => Labeled(LayersDirectoryName, layerId, EntryLabel(name), LayerFileSuffix);
        public static string LayoutPath(string layerId, string name) => Labeled(LayoutsDirectoryName, layerId, EntryLabel(name), LayoutFileSuffix);
        public static string BaselinePath(string layerId, string name) => Labeled(BaselinesDirectoryName, layerId, EntryLabel(name), BaselineFileSuffix);

        public static string LinkPath(string pairId, string firstLayerName, string secondLayerName)
        {
            var first = EntryLabel(firstLayerName);
            var second = EntryLabel(secondLayerName);
            var label = first.Length == 0 ? second : second.Length == 0 ? first : first + "--" + second;
            return Labeled(LinksDirectoryName, pairId, label, LinkFileSuffix);
        }

        public static string EntryLabel(string name)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var character in (name ?? string.Empty).Trim())
            {
                if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
                    builder.Append(character);
                else if ((char.IsWhiteSpace(character) || character == '-') && builder.Length > 0 && builder[builder.Length - 1] != '-')
                    builder.Append('-');
                if (builder.Length >= MaximumLabelLength)
                    break;
            }
            return builder.ToString().Trim('-');
        }

        private const int MaximumLabelLength = 40;

        private static string Labeled(string directory, string id, string label, string suffix) =>
            label.Length == 0 ? $"{directory}/{id}{suffix}" : $"{directory}/{id}.{label}{suffix}";

        public string ToAbsoluteDataPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new ArgumentException("A non-empty relative data path is required.", nameof(relativePath));

            var absolute = Path.GetFullPath(Path.Combine(DataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(DataRoot, absolute);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new ArchitectorDataException($"Path '{relativePath}' escapes the Architector data directory.");
            return absolute;
        }
    }
}
