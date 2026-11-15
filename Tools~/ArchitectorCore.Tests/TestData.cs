using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Architector.Editor;

namespace Architector.Core.Tests
{
    internal static class TestData
    {
        // The package worktree or clone that holds these tests.
        public static string PackageRoot
        {
            get
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory != null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
                    directory = directory.Parent;
                return directory?.FullName ?? AppContext.BaseDirectory;
            }
        }

        // A Unity project with Architector data: ARCHITECTOR_TEST_PROJECT, else christmas-smile-friend beside the
        // Release folder of the package clones. Null when there is none: the tests that need it are ignored.
        public static string RepositoryWithArchitecture()
        {
            foreach (var candidate in new[]
                     {
                         Environment.GetEnvironmentVariable("ARCHITECTOR_TEST_PROJECT"),
                         Path.GetFullPath(Path.Combine(PackageRoot, "..", "..", "christmas-smile-friend"))
                     })
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, "Architector", ArchitectorPaths.ProjectFileName)))
                    return candidate;
            }
            return null;
        }

        public static Dictionary<string, string> CanonicalFiles(string projectRoot)
        {
            var data = Path.Combine(projectRoot, ArchitectorPaths.DataDirectoryName);
            return Directory.GetFiles(data, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(data, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(ArchitectorStorageCodec.IsCanonicalPath)
                .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(data, path)).Replace("\r\n", "\n"), StringComparer.Ordinal);
        }

        // A fresh copy of a project's Architector folder in a temporary project, so a test can write to it.
        public static string CopyOfArchitecture(string projectRoot)
        {
            var target = Path.Combine(Path.GetTempPath(), "ArchitectorCoreTests", Guid.NewGuid().ToString("N"));
            foreach (var folder in new[] { "Assets", "ProjectSettings" })
                Directory.CreateDirectory(Path.Combine(target, folder));
            var source = Path.Combine(projectRoot, ArchitectorPaths.DataDirectoryName);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, ArchitectorPaths.DataDirectoryName, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination);
            }
            return target;
        }
    }
}
