using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;

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
        private const int CliTimeoutMilliseconds = 30000;

        public static string Version => ArchitectorPackagePaths.Version;
        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
        public static string BinaryPath => ArchitectorPackagePaths.ServerBinaryPath;
        public static bool IsInstalled => File.Exists(BinaryPath);
        public static string DownloadUrl => ArchitectorReleaseAssets.Url("v" + Version, ArchitectorReleaseAssets.ServerFileName);

        public static async void Install(Action<string> finished)
        {
            if (ArchitectorPackageUpdates.IsBusy || ArchitectorIntegrationUpdates.IsBusy)
            {
                finished("Wait for the current package or server operation to finish.");
                return;
            }
            var operation = ArchitectorServerMaintenance.InstallAsync(true);
            ArchitectorPackageUpdates.TrackFollowUp(operation);
            var error = await operation;
            if (!ArchitectorReleaseAssets.IsReloading)
                finished(error);
        }

        public static string ManagedRegistrationBinary(string details, string project, string cacheRoot)
        {
            var normalized = (details ?? string.Empty).Replace("\\\\", "\\").Replace('\\', '/');
            var command = Regex.Match(normalized, @"(?im)^\s*command:\s*(.+)$");
            var args = Regex.Match(normalized, @"(?im)^\s*args:\s*(.+)$");
            if (!command.Success || !args.Success
                || args.Groups[1].Value.Replace("\"", string.Empty).Trim() != "--project " + project.Replace('\\', '/')
                || Regex.IsMatch(normalized, @"(?im)^\s*(env|env_vars|cwd):\s*(?!-\s*$)\S"))
                return null;
            var binary = command.Groups[1].Value.Trim().Trim('"');
            var directory = Path.GetDirectoryName(binary);
            var parent = directory == null ? null : Path.GetDirectoryName(directory);
            return string.Equals(parent?.Replace('\\', '/'), cacheRoot.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && ArchitectorUpdateVersion.TryParse(Path.GetFileName(directory), out _)
                && (Path.GetFileName(binary) == "architector-mcp.exe" || Path.GetFileName(binary) == "architector-mcp") ? binary : null;
        }

        public static string RetargetClient(ArchitectorMcpClient client, string previous, string binary, string root)
        {
            var cli = FindCli(client.Command);
            if (cli == null)
                return "Command line tool is unavailable.";
            var removed = RunCli(cli, client.RemoveArguments, root);
            if (!removed.success)
                return removed.output;
            var added = RunCli(cli, string.Format(client.AddArguments, binary, root), root);
            var confirmed = added.success ? CheckClient(client, binary, root) : new ArchitectorMcpClientState(ArchitectorMcpClientStates.Failed, added.output);
            if (confirmed.State == ArchitectorMcpClientStates.Connected)
                return null;
            RunCli(cli, client.RemoveArguments, root);
            var restored = RunCli(cli, string.Format(client.AddArguments, previous, root), root);
            return confirmed.Details + (restored.success ? " Previous registration restored." : " Previous registration could not be restored: " + restored.output);
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
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(CliTimeoutMilliseconds))
                {
                    process.Kill();
                    return (false, $"'{cli}' did not finish in time.");
                }
                return (process.ExitCode == 0, (output.Result + error.Result).Trim());
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
