using System;
using System.Linq;
using Architector.Editor;
using NUnit.Framework;

namespace Architector.Core.Tests
{
    public sealed class JsonTextTests
    {
        // Every Architector document on disk was written by Unity's JsonUtility. Decoding the repository and encoding it
        // again without Unity must give the same text, byte for byte.
        [Test]
        public void EveryStoredDocumentOfARealRepositoryIsWrittenAgainAsUnityWroteIt()
        {
            var root = TestData.RepositoryWithArchitecture();
            if (root == null)
                Assert.Ignore("No Architector repository found; set ARCHITECTOR_TEST_PROJECT.");
            var files = TestData.CanonicalFiles(root);
            Assert.That(files.Count, Is.GreaterThan(10));

            var decoded = ArchitectorStorageCodec.Decode(files);
            Assert.That(decoded.LayerErrors, Is.Empty);
            var encoded = ArchitectorStorageCodec.Encode(decoded.Project, decoded.Layers);
            encoded[ArchitectorPaths.SettingsFileName] =
                ArchitectorJson.Serialize(ArchitectorJson.Deserialize<ArchitectorSettingsDocument>(files[ArchitectorPaths.SettingsFileName]));

            // A baseline approved before 0.3.12 has no approvedByAgent; JsonUtility writes the field's default as well.
            string Current(string path, string text) => path.EndsWith(ArchitectorPaths.BaselineFileSuffix, StringComparison.Ordinal)
                                                        && !text.Contains("\"approvedByAgent\"")
                ? text.Replace("\",\n    \"baseline\": {", "\",\n    \"approvedByAgent\": false,\n    \"baseline\": {")
                : text;
            var different = files.Keys.Where(path => !encoded.TryGetValue(path, out var text) || text != Current(path, files[path])).ToArray();
            foreach (var path in different.Take(3))
                TestContext.Out.WriteLine($"{path}: {(encoded.TryGetValue(path, out var text) ? FirstDifference(text, files[path]) : "not written")}");
            Assert.That(different, Is.Empty);
            // Derived files the codec writes as well (the digest) must equal the stored ones too.
            var derived = encoded.Keys.Where(path => !files.ContainsKey(path)).ToArray();
            var data = System.IO.Path.Combine(root, ArchitectorPaths.DataDirectoryName);
            Assert.That(derived.Where(path => !System.IO.File.Exists(System.IO.Path.Combine(data, path))
                                              || System.IO.File.ReadAllText(System.IO.Path.Combine(data, path)).Replace("\r\n", "\n") != encoded[path]),
                Is.Empty);
        }

        [TestCase(750f, "750.0")]
        [TestCase(0.72f, "0.7200000286102295")]
        [TestCase(-75f, "-75.0")]
        [TestCase(0.25f, "0.25")]
        [TestCase(0.055f, "0.054999999701976779")]
        public void FloatsAreWrittenAsJsonUtilityWritesThem(float value, string expected)
        {
            Assert.That(ArchitectorJsonText.FormatReal(value), Is.EqualTo(expected));
        }

        [Test]
        public void ReadingKeepsDefaultsForAbsentFieldsAndIgnoresUnknownOnes()
        {
            var settings = ArchitectorJsonText.FromJson<ArchitectorSettingsDocument>("{\"unknown\": 3, \"geometry\": {\"gridSize\": 30.0}}");

            Assert.That(settings.geometry.gridSize, Is.EqualTo(30f));
            Assert.That(settings.arrange.improvementSeconds, Is.EqualTo(ArchitectorArrangeSettings.DefaultImprovementSeconds));
        }

        [Test]
        public void TextIsEscapedAsJsonUtilityEscapesIt()
        {
            var group = new ArchitectorGroupRecord { name = "a \"b\" \\ c\nd — e" };

            Assert.That(ArchitectorJsonText.ToJson(group), Does.Contain("\"name\": \"a \\\"b\\\" \\\\ c\\nd — e\""));
        }

        private static string FirstDifference(string a, string b)
        {
            var index = 0;
            while (index < a.Length && index < b.Length && a[index] == b[index])
                index++;
            string Around(string text) => text.Substring(Math.Max(0, index - 60), Math.Min(120, text.Length - Math.Max(0, index - 60)));
            return $"{index}\n--- written\n{Around(a)}\n--- stored\n{Around(b)}";
        }
    }
}
