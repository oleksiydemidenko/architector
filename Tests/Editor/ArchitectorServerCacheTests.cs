using System;
using System.IO;
using Architector.Editor.Agents;
using NUnit.Framework;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorServerCacheTests
    {
        private string root;

        [SetUp]
        public void SetUp() { root = Path.Combine(Path.GetTempPath(), "architector-cache-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }

        [TearDown]
        public void TearDown() { Directory.Delete(root, true); }

        private void Version(string version)
        {
            Directory.CreateDirectory(Path.Combine(root, version));
            File.WriteAllText(Path.Combine(root, version, "architector-mcp.exe"), "test");
        }

        [Test]
        public void CleanupRetainsCurrentPreviousReferencedAndOtherProjectVersions()
        {
            foreach (var version in new[] { "0.1.0", "0.2.0", "0.3.0", "0.4.0", "0.5.0", "0.6.0" })
                Version(version);
            var project = Path.Combine(root, "other-project");
            Directory.CreateDirectory(Path.Combine(project, "Packages"));
            File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"),
                "{\"dependencies\":{\"com.oleksiydemidenko.architector\":\"https://github.com/oleksiydemidenko/architector.git#v0.3.0\"}}");
            ArchitectorServerCache.RecordProject(root, project, "0.2.0", "0.2.0");
            var references = new[] { Path.Combine(root, "0.4.0", "architector-mcp.exe") };
            Assert.That(ArchitectorServerCache.Cleanup(root, new[] { "0.6.0", "0.5.0" }, references, out var removed), Is.Empty);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(Directory.Exists(Path.Combine(root, "0.1.0")), Is.False);
            foreach (var version in new[] { "0.2.0", "0.3.0", "0.4.0", "0.5.0", "0.6.0" })
                Assert.That(Directory.Exists(Path.Combine(root, version)), Is.True);
        }

        [Test]
        public void UnknownFilesAndUnversionedDirectoriesAreNotDeleted()
        {
            Version("0.1.0");
            File.WriteAllText(Path.Combine(root, "0.1.0", "user-data.txt"), "keep");
            Version("notes");
            var warnings = ArchitectorServerCache.Cleanup(root, Array.Empty<string>(), Array.Empty<string>(), out var removed);
            Assert.That(removed, Is.Zero);
            Assert.That(warnings.Count, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(root, "0.1.0", "user-data.txt")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(root, "notes")), Is.True);
        }

        [Test]
        public void MalformedUsageFailsBeforeAnyVersionIsRemoved()
        {
            Version("0.1.0");
            Directory.CreateDirectory(Path.Combine(root, ".projects"));
            File.WriteAllText(Path.Combine(root, ".projects", "broken.json"), "{}");
            Assert.Throws<InvalidDataException>(() => ArchitectorServerCache.Cleanup(root, Array.Empty<string>(), Array.Empty<string>(), out _));
            Assert.That(Directory.Exists(Path.Combine(root, "0.1.0")), Is.True);
        }

        [Test]
        public void ACorruptServerOrMissingReceiptRequiresRepair()
        {
            var binary = Path.Combine(root, "architector-mcp.exe");
            File.WriteAllText(binary, "abc");
            Assert.That(ArchitectorServerMaintenance.Verified(binary), Is.False);
            File.WriteAllText(binary + ".sha256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            Assert.That(ArchitectorServerMaintenance.Verified(binary), Is.True);
            File.AppendAllText(binary, "corrupt");
            Assert.That(ArchitectorServerMaintenance.Verified(binary), Is.False);
        }

        [TestCase("args: --project C:/game\nenv: -", true)]
        [TestCase("args: --project C:/other\nenv: -", false)]
        [TestCase("args: --project C:/game --custom\nenv: -", false)]
        [TestCase("args: --project C:/game\nenv: CUSTOM=value", false)]
        public void OnlyDefaultOwnedRegistrationsMayBeRetargeted(string details, bool owned)
        {
            var binary = "C:/servers/0.2.5/architector-mcp.exe";
            Assert.That(ArchitectorMcpInstaller.ManagedRegistrationBinary("command: " + binary + "\n" + details, "C:/game", "C:/servers") != null, Is.EqualTo(owned));
        }
    }
}
