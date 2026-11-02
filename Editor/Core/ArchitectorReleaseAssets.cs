using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Architector.Editor
{
    public static class ArchitectorReleaseAssets
    {
        private static readonly List<UnityWebRequest> requests = new();

        public static bool IsInstallingServer { get; private set; }
        public static bool IsReloading { get; private set; }
        public static event Action ServerInstallationChanged;

        static ArchitectorReleaseAssets()
        {
            AssemblyReloadEvents.beforeAssemblyReload += AbortRequests;
        }

        public static string ServerFileName
        {
            get
            {
                var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                var runtime = Application.platform switch
                {
                    RuntimePlatform.WindowsEditor => "win-x64.exe",
                    RuntimePlatform.OSXEditor => arm ? "osx-arm64" : "osx-x64",
                    RuntimePlatform.LinuxEditor => arm ? "linux-arm64" : "linux-x64",
                    _ => throw new NotSupportedException("This Editor platform has no Architector MCP server.")
                };
                return "architector-mcp-" + runtime;
            }
        }

        public static string Url(string tag, string fileName) =>
            $"https://github.com/oleksiydemidenko/architector/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";

        public static async Task<string> GetText(string url)
        {
            using var request = UnityWebRequest.Get(url);
            await Send(request);
            return request.downloadHandler.text;
        }

        private static async Task Download(string url, string path)
        {
            using var request = UnityWebRequest.Get(url);
            request.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
            await Send(request);
        }

        public static async Task InstallServer(string tag, string target)
        {
            if (IsInstallingServer)
                throw new InvalidOperationException("A server installation is already in progress.");
            IsInstallingServer = true;
            var partial = target + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var checksum = ReadChecksum(await GetText(Url(tag, "checksums.txt")), ServerFileName);
                await Download(Url(tag, ServerFileName), partial);
                VerifyFile(partial, checksum);
                if (Application.platform != RuntimePlatform.WindowsEditor)
                {
                    using var chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{partial}\"") { UseShellExecute = false });
                    chmod.WaitForExit();
                    if (chmod.ExitCode != 0)
                        throw new IOException("The downloaded server could not be marked executable.");
                }
                if (File.Exists(target))
                    File.Replace(partial, target, null);
                else
                    File.Move(partial, target);
            }
            finally
            {
                IsInstallingServer = false;
                File.Delete(partial);
                if (!IsReloading)
                    ServerInstallationChanged?.Invoke();
            }
        }

        public static string ReadChecksum(string text, string fileName)
        {
            string checksum = null;
            foreach (var line in text.Split('\n'))
            {
                var match = Regex.Match(line.TrimEnd('\r'), @"^([a-fA-F0-9]{64})[ \t]+\*?(.+)$");
                if (!match.Success || !string.Equals(match.Groups[2].Value, fileName, StringComparison.Ordinal))
                    continue;
                if (checksum != null)
                    throw new InvalidDataException($"The checksum for {fileName} is listed more than once.");
                checksum = match.Groups[1].Value;
            }
            return checksum ?? throw new InvalidDataException($"checksums.txt has no SHA-256 checksum for {fileName}.");
        }

        public static void VerifyFile(string path, string checksum)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            if (!string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded server failed its SHA-256 check. It was not installed.");
        }

        private static async Task Send(UnityWebRequest request)
        {
            request.timeout = 120;
            request.SetRequestHeader("User-Agent", "Architector-Unity-Editor");
            requests.Add(request);
            try
            {
                var completed = new TaskCompletionSource<bool>();
                request.SendWebRequest().completed += _ => completed.TrySetResult(true);
                await completed.Task;
                if (request.result != UnityWebRequest.Result.Success)
                    throw new IOException($"HTTP {request.responseCode}: {request.error}");
            }
            finally
            {
                requests.Remove(request);
            }
        }

        private static void AbortRequests()
        {
            IsReloading = true;
            foreach (var request in requests.ToArray())
                request.Abort();
        }
    }
}
