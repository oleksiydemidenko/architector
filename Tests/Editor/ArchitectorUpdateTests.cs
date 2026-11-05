using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorUpdateTests
    {
        [TestCase("v0.2.10", "0.2.9", 1)]
        [TestCase("0.2.2", "0.2.1", 1)]
        [TestCase("0.2.0-preview.10", "0.2.0-preview.2", 1)]
        [TestCase("0.2.0-preview.1", "0.2.0", -1)]
        [TestCase("0.2.0-alpha", "0.2.0-beta", -1)]
        [TestCase("0.2.0-1", "0.2.0-alpha", -1)]
        [TestCase("0.2.0-alpha", "0.2.0-alpha.1", -1)]
        [TestCase("0.2.0+build.2", "0.2.0+build.1", 0)]
        public void VersionsUseSemanticOrder(string left, string right, int expected)
        {
            Assert.That(ArchitectorUpdateVersion.TryParse(left, out var first), Is.True);
            Assert.That(ArchitectorUpdateVersion.TryParse(right, out var second), Is.True);
            Assert.That(Math.Sign(first.CompareTo(second)), Is.EqualTo(expected));
        }

        [TestCase("0.2")]
        [TestCase("0.2.1.4")]
        [TestCase("00.2.1")]
        [TestCase("0.2.1-preview.01")]
        [TestCase("0.2.1-")]
        [TestCase("0.2.1+bad/name")]
        [TestCase("not-a-version")]
        public void InvalidTagsAreNotVersions(string version)
        {
            Assert.That(ArchitectorUpdateVersion.TryParse(version, out _), Is.False);
        }

        [TestCase("6000.3", null, "6000.3.27f1", true)]
        [TestCase("6000.4", null, "6000.3.27f1", false)]
        [TestCase("6000.3", "27f2", "6000.3.27f1", false)]
        [TestCase("6000.3", "27f1", "6000.3.27f2", true)]
        [TestCase("6000.3", "0f1", "6000.3.0b1", false)]
        [TestCase("6000.3", "0f1", "6000.3.0p1", true)]
        [TestCase(null, null, "6000.3.27f1", true)]
        [TestCase("invalid", null, "6000.3.27f1", false)]
        public void UnityRequirementsGateUpdates(string minimum, string release, string current, bool compatible)
        {
            Assert.That(ArchitectorUpdateVersion.SupportsUnity(minimum, release, current), Is.EqualTo(compatible));
        }

        [Test]
        public void ATagNeedsAReleaseAndBothUploadedPlatformAssets()
        {
            var release = Release();
            Assert.That(ArchitectorPackageUpdates.ReleaseReady(null, "architector-mcp-linux-x64", true), Is.False);
            Assert.That(ArchitectorPackageUpdates.ReleaseReady(release, "architector-mcp-linux-x64", true), Is.True);
            Assert.That(ArchitectorPackageUpdates.ReleaseReady(release, "architector-mcp-win-x64.exe", true), Is.False);
            release["assets"][1]["state"] = "new";
            Assert.That(ArchitectorPackageUpdates.ReleaseReady(release, "architector-mcp-linux-x64", true), Is.False);
            ((JArray)release["assets"]).RemoveAt(1);
            Assert.That(ArchitectorPackageUpdates.ReleaseReady(release, "architector-mcp-linux-x64", true), Is.False);
        }

        [Test]
        public void PrereleasesAreSelectableAndDraftsAreNeverOffered()
        {
            var release = Release();
            release["prerelease"] = true;
            Assert.That(ArchitectorPackageUpdates.ReleaseReady(release, "architector-mcp-linux-x64", false), Is.False);
            Assert.That(ArchitectorPackageUpdates.ReleaseReady(release, "architector-mcp-linux-x64", true), Is.True);
            release["draft"] = true;
            Assert.That(ArchitectorPackageUpdates.ReleaseReady(release, "architector-mcp-linux-x64", true), Is.False);
        }

        [Test]
        public void ChangelogShowsOnlyTheVersionsBeingInstalled()
        {
            const string text = "# Changelog\n\n## Unreleased\nfuture\n\n## 0.3.0\nnewer\n\n## 0.2.2\nsecond\n\n## 0.2.1\nfirst\n\n## 0.2.0\nalready installed\n";
            var changes = ArchitectorPackageUpdates.ChangelogBetween(text, "0.2.0", "v0.2.2");
            Assert.That(changes, Does.Contain("second").And.Contain("first"));
            Assert.That(changes, Does.Not.Contain("future").And.Not.Contain("newer").And.Not.Contain("already installed"));
        }

        [Test]
        public void ChecksumsRejectWrongMissingAndDuplicateFiles()
        {
            const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            const string name = "architector-mcp-linux-x64";
            var manifest = expected + "  " + name + "\r\n";
            Assert.That(ArchitectorReleaseAssets.ReadChecksum(manifest, name), Is.EqualTo(expected));
            Assert.Throws<InvalidDataException>(() => ArchitectorReleaseAssets.ReadChecksum(manifest, "another-platform"));
            Assert.Throws<InvalidDataException>(() => ArchitectorReleaseAssets.ReadChecksum(manifest + manifest, name));
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "abc");
                Assert.DoesNotThrow(() => ArchitectorReleaseAssets.VerifyFile(path, expected));
                File.AppendAllText(path, "tampered");
                Assert.Throws<InvalidDataException>(() => ArchitectorReleaseAssets.VerifyFile(path, expected));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void AnAgentLeaseBlocksUpdatesEvenWhenWindowLockingIsOff()
        {
            var work = new ArchitectorAiWorkCoordinator(locksWindows: () => false);
            work.Begin("test");
            Assert.That(work.SettingsLocked, Is.False);
            Assert.That(ArchitectorPackageUpdates.InstallBlocker(work), Does.Contain("active agent work session"));
        }

        [TestCase("v0.2.4", "0.2.4", "0.2.4", true, true)]
        [TestCase("v0.2.4", "0.2.4", null, true, true)]
        [TestCase("v0.2.4", "0.2.4", "0.2.3", true, false)]
        [TestCase("v0.2.4", "0.2.4", "0.2.4", false, false)]
        [TestCase("v0.2.5", "0.2.4", "0.2.4", true, false)]
        [TestCase("v0.2.4", "0.2.4-preview.1", null, true, false)]
        [TestCase(null, "0.2.4", "0.2.4", true, false)]
        [TestCase("v0.2.4", "invalid", null, true, false)]
        public void CurrentStatusRequiresVerifiedVersionsAndTheMatchingServer(string latest, string installed, string lazy, bool server, bool expected)
        {
            var result = new ArchitectorUpdateState { latestTag = latest, checkedUtc = DateTime.UtcNow.ToString("O") };
            Assert.That(ArchitectorPackageUpdates.InstallationCurrent(result, installed, lazy, server), Is.EqualTo(expected));
        }

        [Test]
        public void UncheckedFailedOrPendingChecksNeverClaimTheInstallationIsCurrent()
        {
            var result = new ArchitectorUpdateState { latestTag = "v0.2.4" };
            Assert.That(ArchitectorPackageUpdates.InstallationCurrent(result, "0.2.4", null, true), Is.False);
            result.checkedUtc = DateTime.UtcNow.ToString("O");
            result.error = "HTTP 403";
            Assert.That(ArchitectorPackageUpdates.InstallationCurrent(result, "0.2.4", null, true), Is.False);
            result.error = null;
            result.pendingVersion = "0.2.4";
            Assert.That(ArchitectorPackageUpdates.InstallationCurrent(result, "0.2.4", null, true), Is.False);
            result.pendingVersion = null;
            result.availableTag = "v0.2.5";
            Assert.That(ArchitectorPackageUpdates.InstallationCurrent(result, "0.2.4", null, true), Is.False);
        }

        [Test]
        public void PackageRegistrationCompletesUpdatesWithoutAnAssemblyReload()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var paths = typeof(ArchitectorPackagePaths);
            var updates = typeof(ArchitectorPackageUpdates);
            var version = paths.GetField("version", flags);
            var root = paths.GetField("root", flags);
            var state = updates.GetField("state", flags);
            var installed = ArchitectorPackagePaths.Version;
            var previousVersion = version.GetValue(null);
            var previousRoot = root.GetValue(null);
            var previousState = state.GetValue(null);
            var cacheKey = (string)updates.GetProperty("CacheKey", flags).GetValue(null);
            var hadCache = EditorPrefs.HasKey(cacheKey);
            var previousCache = EditorPrefs.GetString(cacheKey);
            try
            {
                var pending = new ArchitectorUpdateState { installedVersion = "0.0.1", pendingVersion = installed };
                EditorPrefs.SetString(cacheKey, JsonUtility.ToJson(pending));
                state.SetValue(null, pending);
                version.SetValue(null, "0.0.1");
                root.SetValue(null, "stale package path");
                paths.GetMethod("OnPackagesRegistered", flags).Invoke(null, new object[] { null });
                Assert.That(root.GetValue(null), Is.Null);
                updates.GetMethod("RefreshPackages", flags).Invoke(null, null);
                Assert.That(ArchitectorPackagePaths.Version, Is.EqualTo(installed));
                Assert.That(ArchitectorPackageUpdates.State.installedVersion, Is.EqualTo(installed));
                Assert.That(ArchitectorPackageUpdates.State.pendingVersion, Is.Null);
                Assert.That(ArchitectorPackageUpdates.State.availableTag, Is.Null);
                Assert.That(ArchitectorPackageUpdates.State.notice, Does.Contain("Package updated"));
            }
            finally
            {
                version.SetValue(null, previousVersion);
                root.SetValue(null, previousRoot);
                state.SetValue(null, previousState);
                if (hadCache)
                    EditorPrefs.SetString(cacheKey, previousCache);
                else
                    EditorPrefs.DeleteKey(cacheKey);
            }
        }

        [Test]
        public void ReleaseNotesShowHeadingsBulletsAndBoldWithoutMarkdown()
        {
            var text = ArchitectorReleaseNotesView.Format("## 0.2.7\r\n\r\n- **Rules.** Reject `cycles`.\n- Faster.\n");

            Assert.That(text, Is.EqualTo("<b>0.2.7</b>\n•  <b>Rules.</b> Reject cycles.\n•  Faster."));
        }

        private static JObject Release() => JObject.Parse(
            "{ 'draft': false, 'prerelease': false, 'assets': [ { 'name': 'architector-mcp-linux-x64', 'state': 'uploaded' }, { 'name': 'checksums.txt', 'state': 'uploaded' } ] }");
    }
}
