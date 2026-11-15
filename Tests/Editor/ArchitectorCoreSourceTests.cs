using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Architector.Editor.Tests
{
    // The Architector core (Tools~/ArchitectorCore) compiles the package's Editor files without Unity, for the command line.
    // Every file it compiles keeps its Unity references inside #if UNITY_EDITOR, and its JSON writer gives the text
    // JsonUtility gives, so files written without Unity read as files written by the Editor.
    public sealed class ArchitectorCoreSourceTests
    {
        private static readonly Regex UnityReference = new(@"\b(UnityEditor|EditorApplication|SessionState|EditorPrefs|AssetDatabase|EditorUtility|EditorGUI|JsonUtility)\b|\bApplication\.|\bDebug\.Log");

        private static string PackageRoot =>
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ArchitectorRepository).Assembly)?.resolvedPath;

        [Test]
        public void FilesSharedWithTheCoreGuardTheirUnityReferences()
        {
            var package = PackageRoot;
            Assert.That(package, Is.Not.Null);
            var project = File.ReadAllText(Path.Combine(package, "Tools~", "ArchitectorCore", "ArchitectorCore.csproj"));
            var exclude = Regex.Match(project, "Exclude=\"([^\"]*)\"").Groups[1].Value
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace("../../", string.Empty).Replace('\\', '/'))
                .ToArray();
            Assert.That(exclude, Does.Contain("Editor/UI/**"));
            Assert.That(exclude, Does.Contain("Editor/Unity/**"));

            var shared = Directory.GetFiles(Path.Combine(package, "Editor"), "*.cs", SearchOption.AllDirectories)
                .Select(path => path.Substring(package.Length + 1).Replace('\\', '/'))
                .Where(path => !exclude.Any(pattern => pattern.EndsWith("/**", StringComparison.Ordinal)
                    ? path.StartsWith(pattern.Substring(0, pattern.Length - 2), StringComparison.Ordinal)
                    : path == pattern))
                .ToArray();
            Assert.That(shared, Does.Contain("Editor/Persistence/ArchitectorRepository.cs"));
            Assert.That(shared, Does.Contain("Editor/Agents/ArchitectorAgentEdit.cs"));

            var unguarded = shared.SelectMany(path => Unguarded(path, File.ReadAllLines(Path.Combine(package, path)))).ToArray();
            Assert.That(unguarded, Is.Empty, "Guard these with #if UNITY_EDITOR, move them behind ArchitectorHost, or exclude the file in ArchitectorCore.csproj.");
        }

        [Test]
        public void TheScannerSeesOnlyUnguardedReferences()
        {
            var lines = new[]
            {
                "#if UNITY_EDITOR", "using UnityEditor;", "#else", "using System;", "#endif",
                "var root = Application.dataPath;", "// The Unity Editor, in words, is fine."
            };
            Assert.That(Unguarded("File.cs", lines), Is.EqualTo(new[] { "File.cs:6" }));
        }

        [Test]
        public void TheCoreWritesDocumentsAsJsonUtilityDoes()
        {
            var settings = new ArchitectorSettingsDocument();
            settings.geometry.gridSize = 0.055f;
            settings.geometry.minimumNodeGap = 123.456f;
            var layer = new ArchitectorLayerDocument
            {
                layerId = "layer_1",
                nodes =
                {
                    new ArchitectorNodeRecord { id = "node_1", title = "Quotes \" and \\ slashes\nand — dashes\t", members = { new ArchitectorMemberRecord { id = "member_1", declaration = "public Int32 Count { get; }" } } },
                    new ArchitectorNodeRecord { id = "node_2" }
                }
            };
            var layout = new ArchitectorLayerLayoutDocument
            {
                layerId = "layer_1",
                nodes = { new ArchitectorNodeLayoutRecord { nodeId = "node_1", x = -0.1f, y = 1e7f, width = 300f, height = 1f / 3f } }
            };
            var project = new ArchitectorProjectDocument { groups = { new ArchitectorGroupRecord { id = "group_1", name = "G" } } };

            foreach (var document in new object[] { settings, layer, layout, project })
            {
                Assert.That(ArchitectorJsonText.ToJson(document), Is.EqualTo(JsonUtility.ToJson(document, true)), document.GetType().Name);
                var text = JsonUtility.ToJson(document, true);
                Assert.That(ArchitectorJsonText.ToJson(ArchitectorJsonText.FromJson(text, document.GetType())), Is.EqualTo(text), document.GetType().Name + " read back");
            }
        }

        [TestCase(0.1f)]
        [TestCase(0.055f)]
        [TestCase(1f / 3f)]
        [TestCase(123456.789f)]
        [TestCase(1e-7f)]
        [TestCase(3.4e38f)]
        [TestCase(-2.5f)]
        public void TheCoreWritesNumbersAsJsonUtilityDoes(float value)
        {
            var record = new ArchitectorNodeLayoutRecord { x = value };
            Assert.That(ArchitectorJsonText.ToJson(record), Is.EqualTo(JsonUtility.ToJson(record, true)));
        }

        private static IEnumerable<string> Unguarded(string path, IReadOnlyList<string> lines)
        {
            // Each open #if records whether its current branch is the UNITY_EDITOR one.
            var blocks = new Stack<(bool editor, bool active)>();
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index].Trim();
                if (line.StartsWith("#if", StringComparison.Ordinal))
                {
                    var editor = Regex.IsMatch(line, @"^#if\s+UNITY_EDITOR\b");
                    blocks.Push((editor, editor));
                    continue;
                }
                if (line.StartsWith("#else", StringComparison.Ordinal) || line.StartsWith("#elif", StringComparison.Ordinal))
                {
                    var block = blocks.Pop();
                    blocks.Push((block.editor, false));
                    continue;
                }
                if (line.StartsWith("#endif", StringComparison.Ordinal))
                {
                    blocks.Pop();
                    continue;
                }
                if (line.StartsWith("//", StringComparison.Ordinal) || blocks.Any(block => block.active))
                    continue;
                if (UnityReference.IsMatch(line))
                    yield return $"{path}:{index + 1}";
            }
        }
    }
}
