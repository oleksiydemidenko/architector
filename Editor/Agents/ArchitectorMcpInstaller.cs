using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;

namespace Architector.Editor.Agents
{
    public static class ArchitectorMcpClientStates
    {
        public const string NotConnected = "NotConnected";
        public const string Connected = "Connected";
        public const string OtherVersion = "OtherVersion";
        public const string CliNotFound = "CliNotFound";
        public const string Failed = "Failed";
    }

    public readonly struct ArchitectorMcpClientState
    {
        public ArchitectorMcpClientState(string state, string details)
        {
            State = state;
            Details = details ?? string.Empty;
        }

        public string State { get; }
        public string Details { get; }
    }

    public sealed class ArchitectorMcpClient
    {
        public static readonly ArchitectorMcpClient[] All =
        {
            new("Claude Code", "claude", "mcp add --scope local architector -- \"{0}\" --project \"{1}\"", "mcp remove --scope local architector"),
            new("Codex", "codex", "mcp add architector -- \"{0}\" --project \"{1}\"", "mcp remove architector")
        };

        private ArchitectorMcpClient(string displayName, string command, string addArguments, string removeArguments)
        {
            DisplayName = displayName;
            Command = command;
            AddArguments = addArguments;
            RemoveArguments = removeArguments;
        }

        public string DisplayName { get; }
        public string Command { get; }
        public string AddArguments { get; }
        public string RemoveArguments { get; }
    }

    public static class ArchitectorMcpInstaller
    {
        private const string ReleaseRoot = "https://github.com/oleksiydemidenko/architector/releases/download";
        private const int CliTimeoutMilliseconds = 30000;

        public static string Version => ArchitectorPackagePaths.Version;
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
                var error = request.result == UnityWebRequest.Result.Success
                    ? null
                    : request.responseCode == 404
                        ? $"The server for version {Version} is not published yet."
                        : request.error;
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

        public static ArchitectorMcpClientState CheckClient(ArchitectorMcpClient client, string binary, string root)
        {
            var cli = FindCli(client.Command);
            if (cli == null)
                return new ArchitectorMcpClientState(ArchitectorMcpClientStates.CliNotFound, $"'{client.Command}' was not found.");
            var (success, output) = RunCli(cli, "mcp get architector", root);
            if (!success)
                return new ArchitectorMcpClientState(ArchitectorMcpClientStates.NotConnected, output);
            return output.IndexOf(binary, StringComparison.OrdinalIgnoreCase) >= 0
                ? new ArchitectorMcpClientState(ArchitectorMcpClientStates.Connected, output)
                : new ArchitectorMcpClientState(ArchitectorMcpClientStates.OtherVersion, output);
        }

        public static ArchitectorMcpClientState ConnectClient(ArchitectorMcpClient client, string binary, string root)
        {
            var cli = FindCli(client.Command);
            if (cli == null)
                return new ArchitectorMcpClientState(ArchitectorMcpClientStates.CliNotFound, $"'{client.Command}' was not found.");
            RunCli(cli, client.RemoveArguments, root);
            var (success, output) = RunCli(cli, string.Format(client.AddArguments, binary, root), root);
            return success
                ? CheckClient(client, binary, root)
                : new ArchitectorMcpClientState(ArchitectorMcpClientStates.Failed, output);
        }

        public static ArchitectorMcpClientState DisconnectClient(ArchitectorMcpClient client, string binary, string root)
        {
            var cli = FindCli(client.Command);
            if (cli == null)
                return new ArchitectorMcpClientState(ArchitectorMcpClientStates.CliNotFound, $"'{client.Command}' was not found.");
            var (success, output) = RunCli(cli, client.RemoveArguments, root);
            return success
                ? CheckClient(client, binary, root)
                : new ArchitectorMcpClientState(ArchitectorMcpClientStates.Failed, output);
        }

        private static string FindCli(string command)
        {
            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator)
                .Where(folder => folder.Length > 0)
                .Concat(new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                    Path.Combine(home, ".local", "bin"),
                    Path.Combine(home, ".claude", "local"),
                    "/usr/local/bin",
                    "/opt/homebrew/bin"
                });
            var names = windows ? new[] { command + ".exe", command + ".cmd" } : new[] { command };
            foreach (var folder in folders)
            {
                foreach (var name in names)
                {
                    var path = Path.Combine(folder.Trim('"'), name);
                    if (File.Exists(path))
                        return path;
                }
            }

            var extensions = Path.Combine(home, ".vscode", "extensions");
            if (!Directory.Exists(extensions))
                return null;
            return Directory.GetDirectories(extensions, $"anthropic.{command}-code-*")
                .OrderByDescending(folder => folder, StringComparer.Ordinal)
                .SelectMany(folder => names.Select(name => Path.Combine(folder, "resources", "native-binary", name)))
                .FirstOrDefault(File.Exists);
        }

        private static (bool success, string output) RunCli(string cli, string arguments, string workingDirectory)
        {
            var batch = cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo
            {
                FileName = batch ? "cmd.exe" : cli,
                Arguments = batch ? $"/s /c \"\"{cli}\" {arguments}\"" : arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                using var process = Process.Start(start);
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(CliTimeoutMilliseconds))
                {
                    process.Kill();
                    return (false, $"'{cli}' did not finish in time.");
                }
                return (process.ExitCode == 0, (output + error.Result).Trim());
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return (false, exception.Message);
            }
        }

        public static string ClaudeCommand => $"claude mcp add --scope local architector -- \"{BinaryPath}\" --project \"{ProjectRoot}\"";

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
