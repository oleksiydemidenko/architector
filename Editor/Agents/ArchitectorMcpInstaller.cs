using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.Networking;

namespace Architector.Editor.Agents
{
    public static class ArchitectorMcpInstaller
    {
        private const string ReleaseRoot = "https://github.com/oleksiydemidenko/architector/releases/download";

        public static string Version => PackageInfo.FindForAssembly(typeof(ArchitectorMcpInstaller).Assembly)?.version ?? "0.0.0";
        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
        public static string BinaryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".architector", "mcp", Version, FileName);
        public static bool IsInstalled => File.Exists(BinaryPath);
        public static string DownloadUrl => $"{ReleaseRoot}/v{Version}/architector-mcp-{RuntimeId}{Extension}";
        private static string FileName => "architector-mcp" + Extension;
        private static string Extension => Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : string.Empty;

        private static string RuntimeId
        {
            get
            {
                var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                return Application.platform switch
                {
                    RuntimePlatform.WindowsEditor => "win-x64",
                    RuntimePlatform.OSXEditor => arm ? "osx-arm64" : "osx-x64",
                    _ => arm ? "linux-arm64" : "linux-x64"
                };
            }
        }

        public static void Install(Action<string> finished)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BinaryPath));
            var partial = BinaryPath + ".download";
            var request = UnityWebRequest.Get(DownloadUrl);
            request.downloadHandler = new DownloadHandlerFile(partial) { removeFileOnAbort = true };
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                var error = request.result == UnityWebRequest.Result.Success ? null : request.error;
                request.Dispose();
                if (error == null)
                    error = Finish(partial);
                finished(error);
            };
        }

        private static string Finish(string partial)
        {
            try
            {
                File.Delete(BinaryPath);
                File.Move(partial, BinaryPath);
                if (Application.platform != RuntimePlatform.WindowsEditor)
                    Process.Start("chmod", $"+x \"{BinaryPath}\"")?.WaitForExit();
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return exception.Message;
            }
        }

        public static string ClaudeCommand => $"claude mcp add architector -- \"{BinaryPath}\" --project \"{ProjectRoot}\"";

        public static string CodexConfig =>
            $"[mcp_servers.architector]\ncommand = {Toml(BinaryPath)}\nargs = [\"--project\", {Toml(ProjectRoot)}]";

        public static string JsonConfig =>
            "{\n    \"mcpServers\": {\n        \"architector\": {\n" +
            $"            \"command\": \"{Escape(BinaryPath)}\",\n" +
            $"            \"args\": [\"--project\", \"{Escape(ProjectRoot)}\"]\n" +
            "        }\n    }\n}";

        private static string Toml(string value) => "\"" + Escape(value) + "\"";
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
