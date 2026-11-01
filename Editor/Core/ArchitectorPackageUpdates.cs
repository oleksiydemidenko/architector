using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Architector.Editor
{
    public static class ArchitectorPackageUpdates
    {
        public const string PackageName = "com.oleksiydemidenko.architector";
        public const string LazyModeName = "com.oleksiydemidenko.architector-lazy-mode";
        public const string RepositoryUrl = "https://github.com/oleksiydemidenko/architector.git";
        private const string ApiRoot = "https://api.github.com/repos/oleksiydemidenko/architector";
        private const string PreferenceKey = "Architector.Updates.";
        private static ArchitectorUpdateState state;
        private static AddAndRemoveRequest installRequest;
        private static Request integrationRequest;
        private static bool verifying;

        public static event Action Changed;

        public static ArchitectorUpdateState State => state ??= Restore();
        public static bool IsChecking { get; private set; }
        public static bool IsInstalling => verifying || installRequest != null;
        public static bool IsBusy => IsChecking || IsInstalling || ArchitectorReleaseAssets.IsInstallingServer || integrationRequest is { IsCompleted: false };
        public static bool UpdateAvailable => !string.IsNullOrEmpty(State.availableTag) && SourceWarning.Length == 0;
        public static string ReleaseUrl => "https://github.com/oleksiydemidenko/architector/releases/tag/" + Uri.EscapeDataString(State.availableTag ?? string.Empty);
        private static string CacheKey => PreferenceKey + Application.dataPath;

        public static void TrackPackageOperation(Request request)
        {
            integrationRequest = request;
        }

        public static bool AutomaticChecks
        {
            get => EditorPrefs.GetBool(PreferenceKey + "AutomaticChecks", true);
            set
            {
                EditorPrefs.SetBool(PreferenceKey + "AutomaticChecks", value);
                EnsureChecked();
            }
        }

        public static bool IncludePrereleases
        {
            get => EditorPrefs.GetBool(PreferenceKey + "IncludePrereleases", true);
            set
            {
                EditorPrefs.SetBool(PreferenceKey + "IncludePrereleases", value);
                state = NewState();
                Save();
                Changed?.Invoke();
                EnsureChecked();
            }
        }

        public static string SourceWarning
        {
            get
            {
                var package = PackageInfo.FindForAssembly(typeof(ArchitectorPackageUpdates).Assembly);
                if (package == null)
                    return "The installed Architector package could not be identified.";
                if (package.source == PackageSource.Local || package.source == PackageSource.Embedded)
                    return "Architector uses a local clone. Update it with Git; its file path works only on this machine.";
                if (!OfficialGitPackage(package, PackageName, string.Empty))
                    return "Update Architector through the source from which it was installed.";
                var lazy = PackageInfo.GetAllRegisteredPackages().FirstOrDefault(candidate => candidate.name == LazyModeName);
                if (lazy != null && !OfficialGitPackage(lazy, LazyModeName, "?path=/LazyMode~"))
                    return "Lazy Mode uses another source. Install both packages from the Architector Git repository before updating them together.";
                return string.Empty;
            }
        }

        public static void EnsureChecked()
        {
            if (!AutomaticChecks || IsBusy || SourceWarning.Length > 0 || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            if (DateTime.TryParse(State.checkedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt)
                && DateTime.UtcNow - checkedAt < TimeSpan.FromDays(1))
                return;
            Check();
        }

        public static async void Check()
        {
            if (IsBusy || SourceWarning.Length > 0)
                return;
            IsChecking = true;
            state = NewState();
            Changed?.Invoke();
            try
            {
                var tagsTask = GetPages(ApiRoot + "/tags");
                var releasesTask = GetPages(ApiRoot + "/releases");
                await Task.WhenAll(tagsTask, releasesTask);
                var tags = new List<(string Tag, ArchitectorUpdateVersion Version)>();
                if (!ArchitectorUpdateVersion.TryParse(ArchitectorPackagePaths.Version, out var installed))
                    throw new InvalidDataException("The installed package version is invalid.");
                foreach (var tag in tagsTask.Result)
                {
                    var name = (string)tag["name"];
                    if (name != null && name.StartsWith("v", StringComparison.Ordinal)
                        && ArchitectorUpdateVersion.TryParse(name, out var version) && version.CompareTo(installed) > 0)
                        tags.Add((name, version));
                }
                var unavailable = 0;
                foreach (var candidate in tags.OrderByDescending(item => item.Version))
                {
                    var release = releasesTask.Result.FirstOrDefault(item => (string)item["tag_name"] == candidate.Tag);
                    if (!IncludePrereleases && (candidate.Version.IsPrerelease || (bool?)release?["prerelease"] == true))
                        continue;
                    if (!ReleaseReady(release, ArchitectorReleaseAssets.ServerFileName, IncludePrereleases))
                    {
                        unavailable++;
                        continue;
                    }
                    if (!await ValidateVersion(candidate.Tag))
                    {
                        unavailable++;
                        continue;
                    }
                    state.availableTag = candidate.Tag;
                    state.releaseNotes = ChangelogBetween(
                        await ArchitectorReleaseAssets.GetText(RawUrl(candidate.Tag, "CHANGELOG.md")),
                        ArchitectorPackagePaths.Version, candidate.Tag);
                    break;
                }
                state.notice = unavailable > 0
                    ? "Some newer tags are still publishing or are not compatible with this Editor and platform."
                    : string.IsNullOrEmpty(state.availableTag) ? "No newer release is available for the selected channel." : string.Empty;
            }
            catch (Exception exception)
            {
                state.availableTag = null;
                state.error = "Could not check for updates: " + exception.Message;
            }
            finally
            {
                IsChecking = false;
                if (!ArchitectorReleaseAssets.IsReloading)
                {
                    state.checkedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    Save();
                    Changed?.Invoke();
                }
            }
        }

        public static string InstallBlocker(ArchitectorAiWorkCoordinator work)
        {
            if (IsBusy)
                return "Wait for the current check or package operation to finish.";
            if (work == null)
                return "The architecture session is unavailable. Resolve its load error before updating.";
            if (work.IsActive)
                return "End the active agent work session before updating.";
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "Exit Play Mode and wait for compilation and asset import to finish.";
            if (Resources.FindObjectsOfTypeAll<EditorWindow>().Any(window => window.hasUnsavedChanges))
                return "Save unsaved Editor window changes before updating.";
            return SourceWarning;
        }

        public static async void Install(ArchitectorAiWorkCoordinator work)
        {
            var blocker = InstallBlocker(work);
            if (blocker.Length > 0 || !UpdateAvailable)
                return;
            var tag = State.availableTag;
            verifying = true;
            state.error = null;
            Changed?.Invoke();
            try
            {
                var release = JObject.Parse(await ArchitectorReleaseAssets.GetText(ApiRoot + "/releases/tags/" + Uri.EscapeDataString(tag)));
                if (!ReleaseReady(release, ArchitectorReleaseAssets.ServerFileName, IncludePrereleases) || !await ValidateVersion(tag))
                    throw new InvalidDataException("This release is no longer ready or compatible. Check for updates again.");
                verifying = false;
                blocker = InstallBlocker(work);
                if (blocker.Length > 0)
                    throw new InvalidOperationException(blocker);
                var urls = new List<string> { RepositoryUrl + "#" + tag };
                if (LazyModeInstalled())
                    urls.Add(RepositoryUrl + "?path=/LazyMode~#" + tag);
                state.pendingVersion = tag.Substring(1);
                Save();
                installRequest = Client.AddAndRemove(urls.ToArray(), Array.Empty<string>());
                EditorApplication.update += PollInstall;
            }
            catch (Exception exception)
            {
                if (!ArchitectorReleaseAssets.IsReloading)
                {
                    state.pendingVersion = null;
                    state.error = "Could not update Architector: " + exception.Message;
                    Save();
                }
            }
            finally
            {
                verifying = false;
                if (!ArchitectorReleaseAssets.IsReloading)
                    Changed?.Invoke();
            }
        }

        public static bool ReleaseReady(JToken release, string serverFile, bool includePrereleases)
        {
            if (release == null || (bool?)release["draft"] != false || (!includePrereleases && (bool?)release["prerelease"] != false))
                return false;
            var assets = release["assets"] as JArray;
            return assets != null && new[] { serverFile, "checksums.txt" }
                .All(name => assets.Any(asset => (string)asset["name"] == name && (string)asset["state"] == "uploaded"));
        }

        public static string ChangelogBetween(string text, string installed, string target)
        {
            if (!ArchitectorUpdateVersion.TryParse(installed, out var before) || !ArchitectorUpdateVersion.TryParse(target, out var after))
                throw new InvalidDataException("Cannot select changelog entries for an invalid version.");
            var result = new StringBuilder();
            var include = false;
            foreach (var line in text.Replace("\r", string.Empty).Split('\n'))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    var heading = line.Substring(3).Split(' ')[0].Trim('[', ']');
                    include = ArchitectorUpdateVersion.TryParse(heading, out var version)
                        && version.CompareTo(before) > 0 && version.CompareTo(after) <= 0;
                }
                if (include)
                    result.AppendLine(line);
            }
            return result.Length > 0 ? result.ToString().TrimEnd() : "No versioned changelog entries were published for this update.";
        }

        private static async Task<bool> ValidateVersion(string tag)
        {
            var packages = LazyModeInstalled() ? new[] { "package.json", "LazyMode~/package.json" } : new[] { "package.json" };
            foreach (var path in packages)
            {
                var manifest = JObject.Parse(await ArchitectorReleaseAssets.GetText(RawUrl(tag, path)));
                var expectedName = path == "package.json" ? PackageName : LazyModeName;
                if ((string)manifest["name"] != expectedName || (string)manifest["version"] != tag.Substring(1))
                    throw new InvalidDataException($"The package manifest does not match release {tag}.");
                if (!ArchitectorUpdateVersion.SupportsUnity((string)manifest["unity"], (string)manifest["unityRelease"], Application.unityVersion))
                    return false;
            }
            ArchitectorReleaseAssets.ReadChecksum(
                await ArchitectorReleaseAssets.GetText(ArchitectorReleaseAssets.Url(tag, "checksums.txt")),
                ArchitectorReleaseAssets.ServerFileName);
            return true;
        }

        private static async Task<JArray> GetPages(string url)
        {
            var result = new JArray();
            for (var page = 1; ; page++)
            {
                var items = JArray.Parse(await ArchitectorReleaseAssets.GetText(url + "?per_page=100&page=" + page));
                foreach (var item in items)
                    result.Add(item);
                if (items.Count < 100)
                    return result;
            }
        }

        private static string RawUrl(string tag, string path) =>
            $"https://raw.githubusercontent.com/oleksiydemidenko/architector/{Uri.EscapeDataString(tag)}/{path}";

        private static bool LazyModeInstalled() => PackageInfo.GetAllRegisteredPackages().Any(package => package.name == LazyModeName);

        private static bool OfficialGitPackage(PackageInfo package, string name, string query)
        {
            var expected = name + "@" + RepositoryUrl + query;
            return package.source == PackageSource.Git && (package.packageId == expected
                || package.packageId.StartsWith(expected + "#", StringComparison.Ordinal));
        }

        private static ArchitectorUpdateState NewState() => new()
        {
            installedVersion = ArchitectorPackagePaths.Version,
            unityVersion = Application.unityVersion,
            serverAsset = ArchitectorReleaseAssets.ServerFileName,
            includePrereleases = IncludePrereleases
        };

        private static ArchitectorUpdateState Restore()
        {
            ArchitectorUpdateState saved;
            try
            {
                saved = JsonUtility.FromJson<ArchitectorUpdateState>(EditorPrefs.GetString(CacheKey, "{}"));
            }
            catch (ArgumentException)
            {
                return NewState();
            }
            var current = NewState();
            if (saved == null)
                return current;
            if (!string.IsNullOrEmpty(saved.pendingVersion) && saved.pendingVersion == current.installedVersion)
            {
                var lazy = PackageInfo.GetAllRegisteredPackages().FirstOrDefault(package => package.name == LazyModeName);
                current.notice = lazy != null && lazy.version != current.installedVersion
                    ? "Architector was updated, but Lazy Mode has a different version. Check Package Manager."
                    : "Update installed. Open Integrations to install the matching MCP server and reconnect your clients. Commit the package manifest and lock file to share this version with the team.";
                EditorPrefs.SetString(CacheKey, JsonUtility.ToJson(current));
                return current;
            }
            return saved.installedVersion == current.installedVersion && saved.unityVersion == current.unityVersion
                && saved.serverAsset == current.serverAsset && saved.includePrereleases == current.includePrereleases ? saved : current;
        }

        private static void Save() => EditorPrefs.SetString(CacheKey, JsonUtility.ToJson(State));

        private static void PollInstall()
        {
            if (!installRequest.IsCompleted)
                return;
            EditorApplication.update -= PollInstall;
            if (installRequest.Status == StatusCode.Failure)
            {
                state.pendingVersion = null;
                state.error = installRequest.Error.message;
                Save();
            }
            else
            {
                state.notice = "Unity is applying the update and reloading assemblies. Afterward, install the matching server in Integrations and commit the package manifest and lock file.";
                Save();
            }
            installRequest = null;
            Changed?.Invoke();
        }
    }
}
