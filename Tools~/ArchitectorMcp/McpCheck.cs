using System.Diagnostics;
using System.Text.Json.Nodes;
using Architector.CodeFacts;

// check: the project's absence rules for changed files only, quiet when clean. It never waits for a code analysis: a
// stale saved index answers (a refresh starts in the background), and a clone without one says the index is building.
// Hooks run it after an edit (--hook post, the edited file) and when the agent stops (--hook stop, git's changed files).
public static partial class McpServer
{
    private const int ExitFindings = 3;
    private static readonly string[] CheckedExtensions = { ".cs", ".prefab", ".unity", ".asset" };

    // A hook run that had nothing to do (a file type the rules never see, a repeated stop) is not logged.
    private static bool hookSkipped;

    // A hook never fails the agent's turn for a setup problem; it stays quiet.
    private static int HookQuiet(Dictionary<string, List<string>> options, Func<int> otherwise)
    {
        if (!options.ContainsKey("hook"))
            return otherwise();
        hookSkipped = true;
        return ExitOk;
    }

    private static int CliCheck(List<string> positional, Dictionary<string, List<string>> options, JsonNode? hookInput)
    {
        var hook = options.TryGetValue("hook", out var hookValues) ? hookValues.Last() : null;
        var json = options.ContainsKey("json");
        var files = positional.Skip(1).ToList();
        if (hook == "post")
        {
            var edited = (string?)hookInput?["tool_input"]?["file_path"] ?? (string?)hookInput?["tool_input"]?["path"];
            if (edited == null || !CheckedExtensions.Any(extension => edited.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                hookSkipped = true;
                return ExitOk;
            }
            files.Add(edited);
        }
        else if (hook == "stop")
        {
            // A Stop hook that already sent the agent back stays quiet the second time, so the agent can always stop.
            if ((bool?)hookInput?["stop_hook_active"] == true)
            {
                hookSkipped = true;
                return ExitOk;
            }
        }
        if (hook == "stop" || options.ContainsKey("git"))
        {
            var changed = GitChangedFiles(root!);
            files.AddRange(changed ?? new List<string>());
            if (hook == "stop" && !files.Any(file => CheckedExtensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))))
            {
                hookSkipped = true;
                return ExitOk;
            }
        }
        var whole = files.Count == 0 && hook == null && !options.ContainsKey("git");
        var (index, state) = IndexForCheck(spawn: hook != "post");
        var reply = FactsRules.Check(index, state, files, root!, whole);
        var status = (string?)reply["status"];
        if (json)
            return Emit(reply, status == "findings" ? ExitFindings : ExitOk);
        if (status != "findings")
        {
            // Quiet when clean; a person running it by hand learns why nothing was checked.
            if (hook == null && status is "index_building" or "no_rules")
                Console.Out.WriteLine("Architector: " + (string?)reply["message"]);
            if (hook == null && reply["rule_errors"] is JsonArray ruleErrors)
                foreach (var error in ruleErrors)
                    Console.Out.WriteLine("Architector rule error: " + (string?)error);
            return ExitOk;
        }
        var lines = new List<string> { $"Architector rules: {(int?)reply["findings_total"]} finding(s)." };
        foreach (var finding in reply["findings"]!.AsArray())
            lines.Add("- " + FactsRules.FindingLine(finding!));
        lines.Add("Add each component to the named lists in the prefab (Inspector), or, when code fills those lists, add the file to the rule's filledAtRuntime in Architector/code-facts.json.");
        if (hook != null)
        {
            // Exit code 2 hands stderr to the agent (after an edit) or keeps it working (at stop).
            Console.Error.WriteLine(string.Join("\n", lines));
            return 2;
        }
        Console.Out.WriteLine(string.Join("\n", lines));
        return ExitFindings;
    }

    // The saved index, current or not; a missing or stale one is rebuilt by a detached process so this call never waits.
    private static (CodeIndex? Index, string State) IndexForCheck(bool spawn)
    {
        var index = CodeIndex.TryLoad(CodeIndex.SnapshotPath(root!, "net100"), null);
        var state = index == null ? "building" : index.Signature == CodeIndex.ComputeSignature(root!) ? "fresh" : "stale";
        if (index == null || state == "stale" && spawn)
            StartIndexBuild();
        return (index, state);
    }

    private static string IndexLock => Path.Combine(root!, "Library", "Architector", "index-build.lock");

    private static void StartIndexBuild()
    {
        try
        {
            if (File.Exists(IndexLock) && DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexLock) < TimeSpan.FromMinutes(10))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(IndexLock)!);
            File.WriteAllText(IndexLock, DateTime.UtcNow.ToString("o"));
            var binary = Environment.ProcessPath;
            if (binary == null)
                return;
            // Started without the caller's console handles, so a hook runner waiting for this process's output does
            // not wait for the analysis too.
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(binary) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root! }
                : new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = root! };
            if (OperatingSystem.IsWindows())
                foreach (var argument in new[] { "facts", "index", "--project", root! })
                    start.ArgumentList.Add(argument);
            else
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("nohup \"$0\" facts index --project \"$1\" >/dev/null 2>&1 &");
                start.ArgumentList.Add(binary);
                start.ArgumentList.Add(root!);
            }
            Process.Start(start)?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static int CliIndex()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var index = CodeIndex.LoadOrBuild(root!, "net100", () => Basic.Reference.Assemblies.Net100.References.All);
            return Emit(new JsonObject { ["success"] = true, ["types"] = index.Types.Count, ["from_disk"] = index.FromDisk, ["ms"] = watch.ElapsedMilliseconds }, ExitOk);
        }
        finally
        {
            try
            {
                File.Delete(IndexLock);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // Files git sees as changed (staged, unstaged and untracked), relative to the project; null without git.
    private static List<string>? GitChangedFiles(string project)
    {
        var result = new List<string>();
        foreach (var arguments in new[] { new[] { "diff", "--name-only", "--relative", "HEAD" }, new[] { "ls-files", "--others", "--exclude-standard" } })
        {
            try
            {
                var start = new ProcessStartInfo("git") { WorkingDirectory = project, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in arguments)
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start);
                if (process == null)
                    return null;
                var output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    return arguments[0] == "diff" ? null : result;
                result.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return null;
            }
        }
        return result;
    }
}
