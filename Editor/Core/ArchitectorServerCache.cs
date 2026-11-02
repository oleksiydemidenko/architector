using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Architector.Editor
{
    public static class ArchitectorServerCache
    {
        public static string Root => Path.GetDirectoryName(Path.GetDirectoryName(ArchitectorPackagePaths.ServerBinaryPath));

        public static void RecordProject(string root, string project, string version, string previousVersion)
        {
            RequireDirectory(root);
            project = Path.GetFullPath(project);
            var directory = Path.Combine(root, ".projects");
            RequireDirectory(directory);
            Directory.CreateDirectory(directory);
            using var sha = SHA256.Create();
            var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(project))).Replace("-", string.Empty);
            var path = Path.Combine(directory, key + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(new ArchitectorServerProject
                { project = project, version = version, previousVersion = previousVersion }));
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }

        public static string PreviousVersion(string root, string current)
        {
            if (!Directory.Exists(root) || !ArchitectorUpdateVersion.TryParse(current, out var installed))
                return string.Empty;
            return Directory.GetDirectories(root).Select(Path.GetFileName)
                .Where(name => ArchitectorUpdateVersion.TryParse(name, out var version) && version.CompareTo(installed) < 0)
                .Where(name => File.Exists(Path.Combine(root, name, "architector-mcp")) || File.Exists(Path.Combine(root, name, "architector-mcp.exe")))
                .OrderByDescending(name => { ArchitectorUpdateVersion.TryParse(name, out var version); return version; })
                .FirstOrDefault() ?? string.Empty;
        }

        public static IReadOnlyList<string> RunningBinaries()
        {
            var result = new List<string>();
            foreach (var process in Process.GetProcessesByName("architector-mcp"))
            {
                using (process)
                {
                    try { result.Add(process.MainModule.FileName); }
                    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        throw new IOException("A running MCP server could not be identified; cleanup was skipped.", exception);
                    }
                }
            }
            return result;
        }

        public static IReadOnlyList<string> Cleanup(string root, IEnumerable<string> protectedVersions,
            IEnumerable<string> references, out int removed)
        {
            root = Path.GetFullPath(root);
            RequireDirectory(root);
            removed = 0;
            var warnings = new List<string>();
            if (!Directory.Exists(root))
                return warnings;
            var keep = new HashSet<string>(protectedVersions.Where(version => !string.IsNullOrEmpty(version)), StringComparer.Ordinal);
            var usage = Path.Combine(root, ".projects");
            RequireDirectory(usage);
            if (Directory.Exists(usage))
                foreach (var path in Directory.GetFiles(usage, "*.json"))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("A project usage record is a symbolic link; cleanup was skipped.");
                    var entry = JsonUtility.FromJson<ArchitectorServerProject>(File.ReadAllText(path));
                    if (entry == null || string.IsNullOrEmpty(entry.project))
                        throw new InvalidDataException("A project usage record is invalid; cleanup was skipped.");
                    var manifest = Path.Combine(entry.project, "Packages", "manifest.json");
                    if (!File.Exists(manifest))
                    {
                        File.Delete(path);
                        continue;
                    }
                    var dependency = (string)JObject.Parse(File.ReadAllText(manifest))["dependencies"]?[ArchitectorPackageUpdates.PackageName];
                    if (dependency == null)
                        continue;
                    var hash = dependency.LastIndexOf('#');
                    var version = hash >= 0 ? dependency.Substring(hash + 1).TrimStart('v') : entry.version;
                    keep.Add(ArchitectorUpdateVersion.TryParse(version, out _) ? version : entry.version);
                    keep.Add(entry.previousVersion ?? string.Empty);
                }
            var paths = references.Select(NormalizeReference).ToArray();
            var normalizedRoot = NormalizeReference(root).TrimEnd('/') + "/";
            foreach (var directory in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (!ArchitectorUpdateVersion.TryParse(name, out _) || keep.Contains(name)
                    || paths.Any(reference => reference.IndexOf(normalizedRoot + name + "/", StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                try
                {
                    RequireDirectory(directory);
                    var files = Directory.GetFiles(directory);
                    if (Directory.GetDirectories(directory).Length > 0 || files.Any(path => !KnownFile(Path.GetFileName(path))
                        || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                    {
                        warnings.Add("Kept " + name + ": contains files not owned by the server installer.");
                        continue;
                    }
                    foreach (var path in files.OrderBy(path => Path.GetExtension(path) == ".exe" || Path.GetExtension(path).Length == 0 ? 0 : 1))
                        File.Delete(path);
                    Directory.Delete(directory, false);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add("Kept " + name + ": " + exception.Message);
                }
            }
            return warnings;
        }

        private static bool KnownFile(string file) => file == "architector-mcp" || file == "architector-mcp.exe"
            || file == "architector-mcp.pdb" || file == "architector-mcp.sha256" || file == "architector-mcp.exe.sha256"
            || file == "architector-mcp.download" || file == "architector-mcp.exe.download";

        private static string NormalizeReference(string value) => (value ?? string.Empty).Replace("\\\\", "\\").Replace('\\', '/');

        private static void RequireDirectory(string path)
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Server cache paths must not contain symbolic links: " + directory.FullName);
        }
    }
}
