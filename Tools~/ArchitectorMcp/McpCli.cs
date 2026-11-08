using System.Text.Json.Nodes;
using Architector.CodeFacts;

// The architector command line: the same binary as the MCP server, the same handlers, the same JSON replies.
// Agents that can run shell commands use it without loading any tool schema; clients without a shell keep MCP.
public static partial class McpServer
{
    // Exit codes: 0 success, 1 internal error, 2 usage error, 4 setup missing (no Unity project or no Architector data),
    // 6 the operation answered with success=false, 7 the Unity Editor is not reachable (safe to retry once it is open).
    private const int ExitOk = 0;
    private const int ExitInternal = 1;
    private const int ExitUsage = 2;
    private const int ExitSetup = 4;
    private const int ExitFailed = 6;
    private const int ExitEditor = 7;

    private sealed record CliCommand(string Name, string Usage, string Summary, bool NeedsEditor, string[] Options);

    private static readonly CliCommand[] CliCommands =
    {
        new("facts evidence", "facts evidence [--layer L] [--pair \"A -> B\"] [--status undeclared|cycle|unevidenced] [--include references|owners|intra]",
            "Every reference between layers, grouped by pair with declared, undeclared, cycle and unevidenced status; the same totals as DependencyEvidence. Also the active rules and rule suggestions.",
            false, new[] { "layer", "pair", "status", "include", "offset", "limit", "output" }),
        new("facts layer", "facts layer <layer> [--type T]",
            "A layer's files and types, references to and from other layers with type pairs, and node candidates with file:line. --type gives one type's public members and project-rule facts.",
            false, new[] { "type", "offset", "limit", "candidates", "output" }),
        new("facts what-if", "facts what-if --move <Type|Assets/file.cs|Assets/folder>=<Layer> [--move ...]",
            "What moving types, files or folders to other layers would change: undeclared pairs and cycles added and removed, relationships that would lose their evidence. Changes nothing.",
            false, new[] { "move", "output" }),
        new("facts dump", "facts dump <layer>", "A layer's nodes and relationships with ids, without layout, paged.", false, new[] { "offset", "limit", "output" }),
        new("facts lookup", "facts lookup <Layer/Path>[::Node Title[::Member]]...", "Layer, node and member ids by name.", false, Array.Empty<string>()),
        new("query", "query <action> [--<parameter> <value>]...",
            "Any architector_query action through the open Unity Editor, for example: query layer_graph --target Core/Input --detail summary.",
            true, new[] { "any architector_query parameter" }),
        new("edit", "edit <action> [--plan plan.json] [--<parameter> <value>]...",
            "Any architector_edit action through the open Unity Editor, for example: edit dry_run_plan --plan plan.json. --plan takes a file or JSON text.",
            true, new[] { "plan", "any architector_edit parameter" }),
        new("status", "status", "The project, its Architector data and whether the Unity Editor is reachable.", false, Array.Empty<string>()),
        new("doctor", "doctor", "Checks everything the tools need and says what to fix: project, data, code index, Editor, package, agent skills.", false, Array.Empty<string>()),
        new("commands", "commands", "Every command as JSON, for tools.", false, Array.Empty<string>()),
        new("skill install", "skill install <claude|codex|all> [--force]",
            "Installs the package's architector-authoring skill into the project for an agent (.claude/skills or .agents/skills). A copy not installed by Architector is kept unless --force.",
            false, new[] { "force" }),
        new("skill sync", "skill sync", "Updates every skill copy installed by Architector to the package version.", false, Array.Empty<string>())
    };

    private static readonly string[] CliWords = { "facts", "query", "edit", "status", "doctor", "commands", "skill", "help", "--help", "-h" };

    private static bool IsCliCommand(string word) => CliWords.Contains(word);

    private static bool pretty;

    private static int RunCli(string[] args)
    {
        var (positional, options) = ParseArguments(args);
        pretty = options.ContainsKey("pretty");
        if (positional.Count == 0 || positional[0] is "help" or "--help" or "-h" || options.ContainsKey("help"))
            return Help(positional.Where(word => word is not ("help" or "--help" or "-h")).ToList());
        var project = options.TryGetValue("project", out var projectValues) ? projectValues.Last() : null;
        root = FindProject(project == null ? Array.Empty<string>() : new[] { "--project", project });
        try
        {
            switch (positional[0])
            {
                case "commands":
                    return Emit(new JsonObject
                    {
                        ["success"] = true,
                        ["version"] = Version,
                        ["commands"] = Nodes(CliCommands.Select(command => (JsonNode?)new JsonObject
                        {
                            ["command"] = command.Name,
                            ["usage"] = "architector-mcp " + command.Usage,
                            ["summary"] = command.Summary,
                            ["needs_editor"] = command.NeedsEditor,
                            ["options"] = Nodes(command.Options.Select(option => (JsonNode?)option))
                        }))
                    }, ExitOk);
                case "doctor":
                    return Doctor();
            }
            if (root == null)
                return Fail("no_project", "No Unity project here. Run inside a Unity project or pass --project <path>.", ExitSetup);
            switch (positional[0])
            {
                case "status":
                    return Emit(JsonNode.Parse(Status())!, ExitOk);
                case "facts":
                    return CliFacts(positional, options);
                case "query":
                case "edit":
                    return CliBridge(positional[0], positional, options);
                case "skill":
                    return Skill(positional, options);
            }
            return Fail("unknown_command", $"Unknown command '{positional[0]}'. Commands: {string.Join(", ", CliCommands.Select(command => command.Name))}.", ExitUsage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return Fail("internal_error", exception.Message, ExitInternal);
        }
    }

    private static int CliFacts(List<string> positional, Dictionary<string, List<string>> options)
    {
        if (!HasData())
            return Fail("no_architecture", NoData, ExitSetup);
        var sub = positional.Count > 1 ? positional[1] : string.Empty;
        var arguments = new JsonObject();
        void Copy(params string[] names)
        {
            foreach (var name in names)
                if (options.TryGetValue(name, out var values))
                    arguments[name] = int.TryParse(values.Last(), out var number) ? number : values.Last();
        }
        string action;
        switch (sub)
        {
            case "evidence":
                action = "code_evidence";
                Copy("layer", "pair", "status", "include", "offset", "limit", "output");
                break;
            case "layer":
                action = "layer_facts";
                if (positional.Count < 3)
                    return Fail("usage", "Usage: architector-mcp facts layer <layer> [--type T]", ExitUsage);
                arguments["target"] = positional[2];
                Copy("type", "offset", "limit", "candidates", "output");
                break;
            case "dump":
                action = "layer_dump";
                if (positional.Count < 3)
                    return Fail("usage", "Usage: architector-mcp facts dump <layer>", ExitUsage);
                arguments["target"] = positional[2];
                Copy("offset", "limit", "output");
                break;
            case "lookup":
                action = "lookup";
                arguments["names"] = Nodes(positional.Skip(2).Select(name => (JsonNode?)name));
                break;
            case "what-if":
                action = "scope_what_if";
                var moves = new JsonArray();
                foreach (var move in options.TryGetValue("move", out var values) ? values : new List<string>())
                {
                    var separator = move.LastIndexOf('=');
                    if (separator <= 0)
                        return Fail("usage", $"--move takes <Type|Assets/file.cs|Assets/folder>=<Layer>, not '{move}'.", ExitUsage);
                    var subject = move[..separator].Trim();
                    var kind = subject.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "file" : subject.Contains('/') ? "folder" : "type";
                    moves.Add((JsonNode)new JsonObject { [kind] = subject, ["to"] = move[(separator + 1)..].Trim() });
                }
                arguments["moves"] = moves;
                Copy("output");
                break;
            default:
                return Fail("usage", "facts takes evidence, layer, what-if, dump or lookup. architector-mcp help facts lists their options.", ExitUsage);
        }
        var cache = new FactsCache(root!, "net100", () => Basic.Reference.Assemblies.Net100.References.All);
        var model = FactsQueries.ArchitectureOnlyActions.Contains(action) ? cache.Architecture() : cache.Get();
        var answer = FactsQueries.Answer(model, action, arguments, root!);
        return Emit(answer, answer["success"]?.GetValue<bool>() == true ? ExitOk : ExitFailed);
    }

    private static int CliBridge(string tool, List<string> positional, Dictionary<string, List<string>> options)
    {
        if (positional.Count < 2)
            return Fail("usage", $"Usage: architector-mcp {tool} <action> [--<parameter> <value>]...", ExitUsage);
        var arguments = new JsonObject { ["action"] = positional[1] };
        foreach (var (name, values) in options)
        {
            if (name is "project" or "pretty")
                continue;
            var value = values.Last();
            if (name == "plan" && File.Exists(value))
                value = File.ReadAllText(value);
            arguments[name.Replace('-', '_')] = ParseValue(value);
        }
        var answer = BridgeAnswer(tool, arguments);
        if (answer == null)
            return Fail("editor_unreachable", NotRunning, ExitEditor);
        var reply = JsonNode.Parse(answer)!;
        return Emit(reply, (bool?)reply["success"] == true ? ExitOk : ExitFailed);
    }

    private static int Doctor()
    {
        var checks = new JsonArray();
        var healthy = true;
        void Check(string name, bool ok, string detail, bool required = true)
        {
            checks.Add((JsonNode)new JsonObject { ["check"] = name, ["ok"] = ok, ["detail"] = detail });
            if (required && !ok)
                healthy = false;
        }
        Check("project", root != null, root ?? "No Unity project here; run inside one or pass --project <path>.");
        if (root != null)
        {
            if (!HasData())
                Check("architecture", false, NoData);
            else
            {
                var architecture = FactsArchitecture.Load(DataRoot);
                var scoped = architecture.Layers.Values.Count(layer => layer.Scoped);
                Check("architecture", true, $"{architecture.Layers.Count} layers, {scoped} with a code scope.");
                var state = CodeIndex.SnapshotState(root, "net100");
                Check("code_index", scoped == 0 || state != "missing",
                    scoped == 0 ? "No layer has a code scope, so code facts are empty."
                    : state switch
                    {
                        "fresh" => "Saved and current; code facts answer in under a second.",
                        "stale" => "Saved but the code changed; the next code facts call analyses again.",
                        _ => "Not built yet; the first code facts call analyses Assets (seconds to a minute)."
                    }, false);
            }
            Check("editor", EditorReachable(), EditorReachable() ? "Reachable; query and edit work." : "Not reachable: open the project in Unity for query and edit. facts, status and doctor work without it.", false);
            var package = FindPackage(root);
            Check("package", package != null, package == null ? "The Architector package was not found in Packages, Library/PackageCache or the manifest." : $"{package.Value.Version} at {package.Value.Path}", false);
            foreach (var (agent, folder) in SkillTargets(root))
            {
                var stamp = Path.Combine(folder, StampFile);
                var installed = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
                var detail = installed == null
                    ? Directory.Exists(folder) ? "A copy not installed by Architector; skill install --force replaces it." : $"Not installed; run: architector-mcp skill install {agent}"
                    : package != null && installed != package.Value.Version ? $"Version {installed}, package {package.Value.Version}; run: architector-mcp skill sync" : $"Version {installed}.";
                Check($"skill_{agent}", installed != null && (package == null || installed == package.Value.Version), detail, false);
            }
        }
        return Emit(new JsonObject { ["success"] = healthy, ["version"] = Version, ["checks"] = checks }, healthy ? ExitOk : ExitSetup);
    }

    private const string SkillName = "architector-authoring";
    private const string StampFile = ".architector-version";

    private static IEnumerable<(string Agent, string Folder)> SkillTargets(string project) => new[]
    {
        ("claude", Path.Combine(project, ".claude", "skills", SkillName)),
        ("codex", Path.Combine(project, ".agents", "skills", SkillName))
    };

    private static int Skill(List<string> positional, Dictionary<string, List<string>> options)
    {
        var sub = positional.Count > 1 ? positional[1] : string.Empty;
        if (sub is not ("install" or "sync"))
            return Fail("usage", "Usage: architector-mcp skill install <claude|codex|all> [--force] | skill sync", ExitUsage);
        var package = FindPackage(root!);
        if (package == null)
            return Fail("package_not_found", "The Architector package was not found in Packages, Library/PackageCache or the manifest.", ExitSetup);
        var source = Path.Combine(package.Value.Path, "Skills~", SkillName);
        if (!Directory.Exists(source))
            return Fail("skill_not_found", $"The package has no {SkillName} skill at {source}.", ExitSetup);
        var agent = positional.Count > 2 ? positional[2] : "all";
        var force = options.ContainsKey("force");
        var results = new JsonArray();
        foreach (var (name, folder) in SkillTargets(root!))
        {
            var stamp = Path.Combine(folder, StampFile);
            var ours = File.Exists(stamp);
            string status;
            if (sub == "sync" && !ours)
                continue;
            if (sub == "install" && agent != "all" && agent != name)
                continue;
            if (Directory.Exists(folder) && !ours && !force)
                status = "conflict: a copy not installed by Architector is kept; add --force to replace it";
            else if (ours && File.ReadAllText(stamp).Trim() == package.Value.Version)
                status = "up-to-date";
            else
            {
                Directory.CreateDirectory(folder);
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(file => !file.EndsWith(".meta", StringComparison.Ordinal)))
                {
                    var target = Path.Combine(folder, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
                File.WriteAllText(stamp, package.Value.Version);
                status = ours ? "updated" : "installed";
            }
            results.Add((JsonNode)new JsonObject { ["agent"] = name, ["folder"] = folder.Replace('\\', '/'), ["status"] = status });
        }
        var conflict = results.Any(item => ((string?)item?["status"])?.StartsWith("conflict", StringComparison.Ordinal) == true);
        return Emit(new JsonObject { ["success"] = !conflict, ["version"] = package.Value.Version, ["skills"] = results }, conflict ? ExitFailed : ExitOk);
    }

    // The installed Architector package: embedded in Packages, a file: path from the manifest, or the package cache.
    private static (string Path, string Version)? FindPackage(string project)
    {
        const string name = "com.oleksiydemidenko.architector";
        var candidates = new List<string> { Path.Combine(project, "Packages", name) };
        var manifest = Path.Combine(project, "Packages", "manifest.json");
        if (File.Exists(manifest) && JsonNode.Parse(File.ReadAllText(manifest))?["dependencies"]?[name] is JsonValue value
            && value.TryGetValue<string>(out var source) && source.StartsWith("file:", StringComparison.Ordinal))
            candidates.Add(Path.GetFullPath(Path.Combine(project, "Packages", source["file:".Length..])));
        var cache = Path.Combine(project, "Library", "PackageCache");
        if (Directory.Exists(cache))
            candidates.AddRange(Directory.GetDirectories(cache, name + "@*").OrderByDescending(Directory.GetLastWriteTimeUtc));
        foreach (var candidate in candidates)
        {
            var file = Path.Combine(candidate, "package.json");
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["version"] is JsonValue version && version.TryGetValue<string>(out var text))
                return (candidate.Replace('\\', '/'), text);
        }
        return null;
    }

    private static int Help(List<string> topic)
    {
        // "help facts layer x" narrows to "facts layer", then "facts", then everything.
        var prefix = string.Empty;
        var selected = CliCommands;
        for (var words = Math.Min(2, topic.Count); words > 0; words--)
        {
            var candidate = string.Join(" ", topic.Take(words));
            var matches = CliCommands.Where(command => command.Name == candidate || command.Name.StartsWith(candidate + " ", StringComparison.Ordinal)).ToArray();
            if (matches.Length > 0)
            {
                prefix = candidate;
                selected = matches;
                break;
            }
        }
        var text = new System.Text.StringBuilder();
        if (prefix.Length == 0)
        {
            text.AppendLine($"architector-mcp {Version}: Architector for agents and scripts. Without a command it is the MCP server.");
            text.AppendLine();
        }
        foreach (var command in selected)
        {
            text.AppendLine("architector-mcp " + command.Usage);
            text.AppendLine("  " + command.Summary + (command.NeedsEditor ? " Needs the open Unity Editor." : string.Empty));
            if (command.Options.Length > 0)
                text.AppendLine("  Options: " + string.Join(", ", command.Options.Select(option => option.Contains(' ') ? option : "--" + option)) + ".");
        }
        text.AppendLine();
        text.AppendLine("Global: --project <path> (default: the Unity project containing the current folder), --pretty (indented JSON).");
        text.AppendLine("Paged replies take --offset and --limit and report next_offset; --output file writes the full answer under Library/Architector/facts.");
        text.AppendLine("Every reply is JSON with success. Exit codes: 0 success, 1 internal error, 2 usage, 4 setup missing, 6 success=false, 7 Unity Editor not reachable (retry when it is open).");
        Console.Out.Write(text.ToString());
        return ExitOk;
    }

    private static (List<string> Positional, Dictionary<string, List<string>> Options) ParseArguments(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg == "--help" && index == 0)
            {
                positional.Add(arg);
                continue;
            }
            var name = arg[2..];
            string value;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) && name is not ("pretty" or "force" or "help"))
                value = args[++index];
            else
                value = "true";
            if (!options.TryGetValue(name, out var values))
                options[name] = values = new List<string>();
            values.Add(value);
        }
        return (positional, options);
    }

    private static JsonNode? ParseValue(string value)
    {
        if (int.TryParse(value, out var number))
            return number;
        if (bool.TryParse(value, out var flag))
            return flag;
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return JsonNode.Parse(value);
        return value;
    }

    private static JsonArray Nodes(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(item);
        return array;
    }

    private static int Emit(JsonNode reply, int exitCode)
    {
        Console.Out.WriteLine(reply.ToJsonString(pretty ? FactsQueries.Indented : FactsQueries.Compact));
        return exitCode;
    }

    private static int Fail(string code, string message, int exitCode) =>
        Emit(new JsonObject { ["success"] = false, ["code"] = code, ["message"] = message }, exitCode);
}
