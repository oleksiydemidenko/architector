using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Architector.Editor.Agents
{
    [InitializeOnLoad]
    public static class ArchitectorServerMaintenance
    {
        private static bool queued = true;
        private static bool requested;
        private static string Key => "Architector.ServerMaintenance." + Application.dataPath;

        static ArchitectorServerMaintenance()
        {
            if (Application.isBatchMode)
                return;
            Events.registeredPackages += OnPackagesRegistered;
            ArchitectorPackageUpdates.ServerMaintenanceRequested += Schedule;
            EditorApplication.update += Tick;
            ArchitectorIntegrationUpdates.Register(ArchitectorPackageUpdateProvider.Instance);
        }

        private static void OnPackagesRegistered(PackageRegistrationEventArgs changes) => Schedule();
        private static void Schedule() { queued = true; requested = true; EditorPrefs.DeleteKey(Key + ".FailedVersion"); }

        private static void Tick()
        {
            if (!queued || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode
                || ArchitectorPackageUpdates.IsBusy || ArchitectorIntegrationUpdates.IsBusy)
                return;
            if (ArchitectorPackageUpdates.SourceWarning.Length > 0)
            {
                queued = false;
                return;
            }
            if (!ArchitectorSession.TryGet(out var session, out _) || session.AiWork.IsActive)
                return;
            queued = false;
            if (EditorPrefs.GetString(Key + ".FailedVersion") == ArchitectorMcpInstaller.Version)
                return;
            if (!requested && SessionState.GetBool(Key + ".Checked", false)
                && EditorPrefs.GetString(Key + ".Version") == ArchitectorMcpInstaller.Version
                && Verified(ArchitectorMcpInstaller.BinaryPath))
                return;
            requested = false;
            ArchitectorPackageUpdates.TrackFollowUp(InstallAsync(false));
        }

        public static async Task<string> InstallAsync(bool force)
        {
            var version = ArchitectorMcpInstaller.Version;
            var previous = EditorPrefs.GetString(Key + ".Version", string.Empty);
            try
            {
                if (previous == version || string.IsNullOrEmpty(previous))
                    previous = ArchitectorServerCache.PreviousVersion(ArchitectorServerCache.Root, version);
                ArchitectorServerCache.RecordProject(ArchitectorServerCache.Root, ArchitectorMcpInstaller.ProjectRoot, version, previous);
                var references = new List<string>();
                var clients = new Dictionary<ArchitectorMcpClient, string>();
                foreach (var client in ArchitectorMcpClient.All)
                {
                    var state = await Task.Run(() => ArchitectorMcpInstaller.CheckClient(client, ArchitectorMcpInstaller.BinaryPath, ArchitectorMcpInstaller.ProjectRoot));
                    references.Add(state.Details);
                    if (state.State == ArchitectorMcpClientStates.OtherVersion)
                    {
                        var binary = ArchitectorMcpInstaller.ManagedRegistrationBinary(state.Details, ArchitectorMcpInstaller.ProjectRoot, ArchitectorServerCache.Root);
                        if (binary != null)
                            clients.Add(client, binary);
                    }
                }
                if (force || !Verified(ArchitectorMcpInstaller.BinaryPath))
                    await ArchitectorReleaseAssets.InstallServer("v" + version, ArchitectorMcpInstaller.BinaryPath);
                var notes = new List<string>();
                foreach (var client in clients)
                {
                    var error = await Task.Run(() => ArchitectorMcpInstaller.RetargetClient(client.Key, client.Value,
                        ArchitectorMcpInstaller.BinaryPath, ArchitectorMcpInstaller.ProjectRoot));
                    notes.Add(error == null ? client.Key.DisplayName + " registration updated. Restart that client to use the new server."
                        : client.Key.DisplayName + ": " + error);
                }
                EditorPrefs.SetString(Key + ".Version", version);
                SessionState.SetBool(Key + ".Checked", true);
                EditorPrefs.DeleteKey(Key + ".FailedVersion");
                if (ArchitectorPackageUpdates.AutoCleanup)
                {
                    try
                    {
                        references.AddRange(ArchitectorServerCache.RunningBinaries());
                        var warnings = ArchitectorServerCache.Cleanup(ArchitectorServerCache.Root, new[] { version, previous }, references, out var removed);
                        notes.Add("Auto cleanup removed " + removed + " unused server version(s).");
                        notes.AddRange(warnings);
                    }
                    catch (Exception exception)
                    {
                        notes.Add("Cleanup skipped: " + exception.Message);
                    }
                }
                ArchitectorPackageUpdates.ReportServerMaintenance(null, "Matching MCP server is installed. " + string.Join(" ", notes));
                return null;
            }
            catch (Exception exception)
            {
                if (!ArchitectorReleaseAssets.IsReloading)
                {
                    EditorPrefs.SetString(Key + ".FailedVersion", version);
                    ArchitectorPackageUpdates.ReportServerMaintenance(exception.Message,
                        "Server setup did not finish. The previous version was retained. Retry server setup when ready.");
                }
                return exception.Message;
            }
        }

        public static bool Verified(string binary)
        {
            if (!File.Exists(binary) || !File.Exists(binary + ".sha256"))
                return false;
            try { ArchitectorReleaseAssets.VerifyFile(binary, File.ReadAllText(binary + ".sha256").Trim()); return true; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
        }
    }
}
