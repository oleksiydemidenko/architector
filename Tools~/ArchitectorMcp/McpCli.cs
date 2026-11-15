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
        new("facts layer", "facts layer <layer> [--type T [--section reads|writes|uses|subscribes|unsubscribes|assigns|lifecycle|recurring|wiring|base_of]]",
            "A layer's files and types, references to and from other layers with type pairs, node candidates with file:line, and the wiring status of every type (wired, test_only, base_only, created_in_code, created_by_reflection, unwired, not_tracked). --type gives one type's public members with their member_ref for plans, project-rule facts, behaviour facts (what it reads, writes, subscribes to, assigns elsewhere, its Unity callbacks, per-frame work outside Update) and wiring places; --section pages one of them. A layer is its id, full path or a unique trailing part of the path.",
            false, new[] { "type", "section", "offset", "limit", "candidates", "output" }),
        new("facts audit", "facts audit [<layer>] [--min-uses N]",
            "Contradictions between the graph and the code facts, each with node, type and evidence: nodes on unwired, test-only or created-in-code types, often used or extension types without a node (--min-uses, default 20), dependency relationships without evidence or drawn on types that hold none of the references, nodes without relationships and without a standalone mark.",
            false, new[] { "min-uses", "offset", "limit", "output" }),
        new("facts snapshot", "facts snapshot [--name N]",
            "Saves the code facts (owner, wiring status, behaviour and members per type; evidence per layer pair) to Library/Architector/snapshots/<N or time>.json.",
            false, new[] { "name" }),
        new("facts diff", "facts diff <snapshot> [<other snapshot>]",
            "What changed between two snapshots, or between a snapshot and the current code: types added and removed, owner, wiring, behaviour and member changes, evidence per layer pair.",
            false, new[] { "offset", "limit", "output" }),
        new("facts what-if", "facts what-if --move <Type|Assets/file.cs|Assets/folder>=<Layer> [--move ...]",
            "What moving types, files or folders to other layers would change: undeclared pairs and cycles added and removed, relationships that would lose their evidence. Changes nothing.",
            false, new[] { "move", "output" }),
        new("route", "route <file|type>... [--limit N] [--json]",
            "From a file path or type name to its layer (by code scope, then by linked node), the digest lines to read, connected layers, the documents worth reading and, for the node it names, the types that write and read its state (generated from the code facts). The text of architector_route; --json gives the same as structured JSON.",
            false, new[] { "limit", "json" }),
        new("digest", "digest [<layer|group>...] [--form index|compact|full|list] [--json]",
            "The approved architecture. Without layers it only lists the approved layers by group with node counts (cheap). A layer (path or unique trailing part) gives its full section with members, base types and relationships; a group gives its layers in the compact form (node, type, first sentence of its role, the types that write and read its state from the code facts, capped with a total). --form compact without layers gives every layer compactly (large on big graphs), --form full the whole digest, --form list the layer names. The text of architector_digest; --json wraps it.",
            false, new[] { "form", "json" }),
        new("slice", "slice <type|file.cs> [--json]",
            "Who writes and reads a type and where that runs, in about 1,500 tokens: the prefabs that hold it, then per holder the writers and readers with their placement (prefab, the field of the holder that loads that prefab, start lists) and their use sites inside methods that ticks, callbacks, events or Unity run, with how each is run. For a component type: its placement and start lists, how its methods run, lifecycle effects (Activate, Deactivate, SetActive) and what it writes with the other writers and readers. Prints text; --json wraps it.",
            false, new[] { "json" }),
        new("facts index", "facts index", "Builds the code index now, or confirms it is current (check starts this in the background when the index is missing or stale).", false, Array.Empty<string>()),
        new("check", "check [<file>...] [--git] [--json]",
            "The project's absence rules (rules.startedBy in Architector/code-facts.json) for changed files only: a prefab or scene is checked with its variants and the files that nest it, a C# file through the prefabs and scenes that place its script. --git adds the files git sees as changed; without files and --git the whole project. Prints one line per finding and nothing when clean (exit 0; 3 with findings). Never waits for a code analysis: with a stale index it answers from the saved one, on a fresh clone it says the index is building. --hook post|stop is the mode the optional Claude Code hooks use.",
            false, new[] { "git", "json", "hook" }),
        new("facts dump", "facts dump <layer>", "A layer's nodes and relationships with ids, without layout, paged.", false, new[] { "offset", "limit", "output" }),
        new("facts lookup", "facts lookup <Layer/Path>[::Node Title[::Member]]...", "Layer, node and member ids by name.", false, Array.Empty<string>()),
        new("query", "query <action> [--<parameter> <value>]...",
            "Any architector_query action through the open Unity Editor, for example: query layer_graph --target Core/Input --detail summary. "
            + "The Editor answers while the integration Architector CLI is on. While Unity runs but its bridge does not answer (compile, domain reload), it retries for up to 30 s (--wait <seconds>, 0 turns it off).",
            true, new[] { "wait", "any architector_query parameter" }),
        new("edit", "edit <action> [--plan plan.json] [--<parameter> <value>]...",
            "Any architector_edit action through the open Unity Editor, for example: edit dry_run_plan --plan plan.json. --plan takes a file or JSON text. "
            + "approve_layers needs --approve_changes true for layers changed since their last approval. implements and extends are labels on a dependency relationship (kind dependency, label implements), not relationship kinds. "
            + "dry_run_plan lists permissions_lost: layer pairs the plan leaves without any dependency, with their code_references. set_standalone --target <layer> --node <node> --reason <why> marks a linked node that has no relationship on purpose. "
            + "A list parameter takes a repeated flag (--layers A --layers B) or a comma list (--layers A,B). A node responsibility has at most 240 characters. "
            + "Waits for a compiling or reloading Editor like query (--wait); an edit is never sent twice.",
            true, new[] { "plan", "approve_changes", "work_token", "expected_revision", "wait", "any architector_edit parameter" }),
        new("status", "status", "The project, its Architector data, whether Unity runs and its bridge is reachable, which clients the bridge serves (editor_serves), the saved switches of Architector CLI and Architector MCP server, and how query and edit wait for the Editor (editor_wait).", false, Array.Empty<string>()),
        new("doctor", "doctor", "Checks everything the tools need and says what to fix: project, data, code index, Editor, package, agent skills.", false, Array.Empty<string>()),
        new("commands", "commands", "Every command as JSON, for tools.", false, Array.Empty<string>()),
        new("skill install", "skill install <claude|codex|all> [--force] [--hooks]",
            "Installs the package's architector-authoring skill into the project for an agent (.claude/skills or .agents/skills). A copy not installed by Architector is kept unless --force. --hooks also adds two optional Claude Code hooks to .claude/settings.local.json: check after an Edit or Write of a .cs, .prefab, .unity or .asset file, and check of git's changed files when the agent stops; both are silent when clean.",
            false, new[] { "force", "hooks" }),
        new("skill hooks", "skill hooks [remove]", "Shows whether the optional Claude Code hooks are installed; remove takes them out of .claude/settings.local.json.", false, Array.Empty<string>()),
        new("skill sync", "skill sync", "Updates every skill copy installed by Architector to the package version, and points installed hooks at this binary.", false, Array.Empty<string>())
    };

    private static readonly string[] CliWords = { "facts", "route", "digest", "slice", "check", "query", "edit", "status", "doctor", "commands", "skill", "help", "--help", "-h" };

    private static bool IsCliCommand(string word) => CliWords.Contains(word);

    // A command line call has a command word among its positional arguments, wherever the options stand
    // ("--project X status" as well as "status --project X"). A positional word that is neither a command nor an
    // existing folder is a mistyped command, answered as one, so the binary never starts the silent server for it.
    private static bool IsCliInvocation(string[] args)
    {
        if (args.Length > 0 && IsCliCommand(args[0]))
            return true;
        var (positional, _) = ParseArguments(args);
        if (positional.Count == 0)
            return false;
        return IsCliCommand(positional[0]) || !Directory.Exists(positional[0]);
    }

    private static bool pretty;

    private static int RunCli(string[] args)
    {
        // Replies are UTF-8 on every platform: the Windows console code page would turn names and the digest's dashes
        // into question marks.
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
        }
        var (positional, options) = ParseArguments(args);
        pretty = options.ContainsKey("pretty");
        if (positional.Count == 0 || positional[0] is "help" or "--help" or "-h" || options.ContainsKey("help"))
            return Help(positional.Where(word => word is not ("help" or "--help" or "-h")).ToList());
        var project = options.TryGetValue("project", out var projectValues) ? projectValues.Last() : null;
        // A hook gets the event as JSON on standard input: the edited file, the session id, whether a stop hook runs again.
        JsonNode? hookInput = null;
        if (options.ContainsKey("hook") && Console.IsInputRedirected)
        {
            try
            {
                hookInput = JsonNode.Parse(Console.In.ReadToEnd());
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
            {
            }
        }
        if (project == null && (string?)hookInput?["cwd"] is { } hookFolder)
            project = hookFolder;
        root = FindProject(project == null ? Array.Empty<string>() : new[] { "--project", project });
        var counting = new CountingWriter(Console.Out);
        Console.SetOut(counting);
        var watch = StartCall();
        callAction = string.Join(" ", positional.Take(positional[0] is "facts" or "skill" && positional.Count > 1 ? 2 : 1));
        var code = RunCliCommand(positional, options, hookInput);
        if (positional[0] is not ("commands" or "doctor" or "status") && !(options.ContainsKey("hook") && code == ExitOk && counting.Count == 0 && callAction == "check" && hookSkipped))
        {
            var arguments = new JsonObject();
            var rest = positional.Skip(callAction.Split(' ').Length).ToArray();
            if (rest.Length > 0)
                arguments["args"] = Nodes(rest.Select(item => (JsonNode?)item));
            foreach (var (name, values) in options.Where(pair => pair.Key is not ("project" or "pretty")))
                arguments[name] = values.Last();
            LogCall(options.ContainsKey("hook") ? "hook" : "cli", callAction, arguments, counting.Count, watch.ElapsedMilliseconds,
                (string?)hookInput?["session_id"]);
        }
        Console.Out.Flush();
        return code;
    }

    private static int RunCliCommand(List<string> positional, Dictionary<string, List<string>> options, JsonNode? hookInput)
    {
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
                case "route":
                    return CliRoute(positional, options);
                case "digest":
                    return CliDigest(positional, options);
                case "query":
                case "edit":
                    return CliBridge(positional[0], positional, options);
                case "skill":
                    return Skill(positional, options);
                case "slice":
                    return CliSlice(positional, options);
                case "check":
                    return HasData() || FactsRules.HasRules(root) ? CliCheck(positional, options, hookInput) : HookQuiet(options, () => Fail("no_architecture", NoData, ExitSetup));
            }
            return Fail("unknown_command", $"Unknown command '{positional[0]}'. Commands: {string.Join(", ", CliCommands.Select(command => command.Name))}.", ExitUsage);
        }
        catch (Exception exception)
        {
            // Every failure is reported as JSON with exit code 1; the command line never ends without output.
            return Fail("internal_error", $"{exception.GetType().Name}: {exception.Message}", ExitInternal);
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
                Copy("type", "section", "offset", "limit", "candidates", "output");
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
            case "audit":
                action = "facts_audit";
                if (positional.Count > 2)
                    arguments["target"] = positional[2];
                if (options.TryGetValue("min-uses", out var minUses) && int.TryParse(minUses.Last(), out var minimum))
                    arguments["min_uses"] = minimum;
                Copy("offset", "limit", "output");
                break;
            case "snapshot":
                action = "facts_snapshot";
                Copy("name");
                break;
            case "diff":
                action = "facts_diff";
                if (positional.Count < 3)
                    return Fail("usage", "Usage: architector-mcp facts diff <snapshot> [<other snapshot>]", ExitUsage);
                arguments["snapshot"] = positional[2];
                if (positional.Count > 3)
                    arguments["against"] = positional[3];
                Copy("offset", "limit", "output");
                break;
            case "index":
                return CliIndex();
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
                return Fail("usage", "facts takes evidence, layer, audit, snapshot, diff, what-if, dump, lookup or index. architector-mcp help facts lists their options.", ExitUsage);
        }
        var cache = new FactsCache(root!, "net100", () => Basic.Reference.Assemblies.Net100.References.All);
        var model = FactsQueries.ArchitectureOnlyActions.Contains(action) ? cache.Architecture() : cache.Get();
        var answer = FactsQueries.Answer(model, action, arguments, root!);
        return Emit(answer, answer["success"]?.GetValue<bool>() == true ? ExitOk : ExitFailed);
    }

    private static int CliSlice(List<string> positional, Dictionary<string, List<string>> options)
    {
        if (!HasData())
            return Fail("no_architecture", NoData, ExitSetup);
        if (positional.Count < 2)
            return Fail("usage", "Usage: architector-mcp slice <type|file.cs> [--json]", ExitUsage);
        var cache = new FactsCache(root!, "net100", () => Basic.Reference.Assemblies.Net100.References.All);
        var answer = FactsQueries.Answer(cache.Get(), "slice", new JsonObject { ["target"] = positional[1] }, root!);
        var ok = answer["success"]?.GetValue<bool>() == true;
        if (options.ContainsKey("json") || !ok)
            return Emit(answer, ok ? ExitOk : ExitFailed);
        Console.Out.Write((string?)answer["text"]);
        return ExitOk;
    }

    // route and digest print the text of their MCP tools, which agents read directly; --json wraps it (route: the
    // structured routes) so scripts still get JSON with success.
    private static int CliRoute(List<string> positional, Dictionary<string, List<string>> options)
    {
        if (!HasData())
            return Fail("no_architecture", NoData, ExitSetup);
        if (positional.Count < 2)
            return Fail("usage", "Usage: architector-mcp route <file|type>... [--limit N] [--json]", ExitUsage);
        int? limit = null;
        if (options.TryGetValue("limit", out var limitValues))
        {
            if (!int.TryParse(limitValues.Last(), out var parsed) || parsed < 1)
                return Fail("usage", "--limit takes a positive number.", ExitUsage);
            limit = parsed;
        }
        var json = options.ContainsKey("json");
        var text = RouteText(positional.Skip(1), limit, json, out var allFound);
        if (json)
            return Emit(new JsonObject { ["success"] = allFound, ["routes"] = JsonNode.Parse(text) }, allFound ? ExitOk : ExitFailed);
        Console.Out.Write(text);
        return allFound ? ExitOk : ExitFailed;
    }

    private static int CliDigest(List<string> positional, Dictionary<string, List<string>> options)
    {
        if (!HasData())
            return Fail("no_architecture", NoData, ExitSetup);
        var arguments = new JsonObject { ["layers"] = Nodes(positional.Skip(1).Select(layer => (JsonNode?)layer)) };
        if (options.TryGetValue("form", out var form))
            arguments["form"] = form.Last();
        else if (options.ContainsKey("full"))
            arguments["form"] = "full";
        var text = DigestText(arguments, out var error);
        if (options.ContainsKey("json"))
            return error ? Fail("digest", text, ExitFailed) : Emit(new JsonObject { ["success"] = true, ["text"] = text }, ExitOk);
        if (error)
            return Fail("digest", text, ExitFailed);
        Console.Out.WriteLine(text);
        return ExitOk;
    }

    // query and edit wait this long for a Unity Editor that runs for the project but whose bridge does not answer yet
    // (compile, domain reload); --wait <seconds> changes it, 0 turns it off.
    private const int DefaultEditorWaitSeconds = 30;
    private const int EditorPollMilliseconds = 500;

    private const string CliNotRunning = "The Unity Editor is not running for this project. Open the project in Unity and try again. facts, route, digest, slice, check and status work without it.";
    private const string CliOff = "The integration 'Architector CLI' is off for this project. Turn it on in Window > Architector > Settings > AI > Connections.";

    private static int CliBridge(string tool, List<string> positional, Dictionary<string, List<string>> options)
    {
        if (positional.Count < 2)
            return Fail("usage", $"Usage: architector-mcp {tool} <action> [--<parameter> <value>]... [--wait <seconds>]", ExitUsage);
        var wait = DefaultEditorWaitSeconds;
        if (options.TryGetValue("wait", out var waitValues) && (!int.TryParse(waitValues.Last(), out wait) || wait < 0))
            return Fail("usage", "--wait takes whole seconds, 0 or more.", ExitUsage);
        var arguments = new JsonObject { ["action"] = positional[1] };
        foreach (var (name, values) in options)
        {
            if (name is "project" or "pretty" or "wait")
                continue;
            // A repeated flag (--layers A --layers B) is a list.
            if (values.Count > 1 && name != "plan")
            {
                arguments[name.Replace('-', '_')] = Nodes(values.Select(item => (JsonNode?)item));
                continue;
            }
            var value = values.Last();
            if (name == "plan" && File.Exists(value))
                value = File.ReadAllText(value);
            arguments[name.Replace('-', '_')] = ParseValue(value);
        }
        var answer = BridgeAnswer(tool, arguments, CliClient, out var sent);
        // Without a bridge to ask, the saved switch tells at once that waiting cannot help.
        if (answer == null && !sent && !IntegrationOn(CliId))
            return Fail("integration_disabled", CliOff, ExitFailed);
        // A query is safe to send again; an edit only when it never reached the Editor.
        bool MayRetry() => !sent || tool == "query";
        if (answer == null && MayRetry() && wait > 0 && UnityRunning())
        {
            Console.Error.WriteLine($"architector-mcp: Unity runs for this project, but its bridge does not answer yet (compile or domain reload). Waiting up to {wait} s.");
            var deadline = DateTime.UtcNow.AddSeconds(wait);
            while (answer == null && MayRetry() && DateTime.UtcNow < deadline && UnityRunning())
            {
                Thread.Sleep(EditorPollMilliseconds);
                answer = BridgeAnswer(tool, arguments, CliClient, out sent);
            }
        }
        if (answer == null)
        {
            var message = !MayRetry()
                ? "The Editor closed the connection after it got the edit, so the edit may have run. Check query work_status or the revision before you send it again."
                : UnityRunning()
                    ? $"Unity runs for this project, but its bridge did not answer within {wait} s. It may still compile or reload, or wait for a dialog; try again, or pass a longer --wait <seconds>."
                    : CliNotRunning;
            return Fail("editor_unreachable", message, ExitEditor);
        }
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
                // Without the project files the code facts see no engine or package base class: MonoBehaviours with such a
                // base stay not_tracked instead of unwired, and attributes resolve by name only.
                var projectFiles = File.Exists(Path.Combine(root, "Assembly-CSharp.csproj"));
                Check("project_files", scoped == 0 || projectFiles,
                    projectFiles
                        ? "Assembly-CSharp.csproj found; code facts see engine and package base classes."
                        : "No Assembly-CSharp.csproj: code facts cannot see engine and package base classes, so dead MonoBehaviours stay not_tracked and some references are missed. Generate the project files once (Unity: Preferences > External Tools > Regenerate project files, or Assets > Open C# Project with an IDE selected); they stay local and out of version control.",
                    false);
            }
            if (HasData())
                Check("cli_integration", IntegrationOn(CliId), IntegrationOn(CliId)
                    ? "Architector CLI is on: the Editor answers query and edit of the command line."
                    : CliOff, false);
            Check("editor", EditorReachable(), EditorReachable() ? "Reachable; query and edit work." : UnityRunning()
                ? "Unity runs, but its bridge does not answer: it compiles or reloads, or both built-in integrations are off. query and edit wait for it."
                : "Not reachable: open the project in Unity for query and edit. facts, status and doctor work without it.", false);
            var package = FindPackage(root);
            Check("package", package != null, package == null ? "The Architector package was not found in Packages, Library/PackageCache or the manifest." : $"{package.Value.Version} at {package.Value.Path}", false);
            Check("hooks", true, HooksInstalled(root)
                ? "Claude Code hooks installed in .claude/settings.local.json (check after edits and at stop); skill hooks remove takes them out."
                : "Optional Claude Code hooks are not installed; skill install claude --hooks adds them.", false);
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

    private const string PackageWorkflowLink = "../../Documentation~/Workflow.md";
    private const string InstalledWorkflowLink = "Workflow.md";

    private static int Skill(List<string> positional, Dictionary<string, List<string>> options)
    {
        var sub = positional.Count > 1 ? positional[1] : string.Empty;
        if (sub == "hooks")
        {
            if (positional.Count > 2 && positional[2] != "remove")
                return Fail("usage", "Usage: architector-mcp skill hooks [remove]", ExitUsage);
            if (positional.Count > 2)
                return Emit(new JsonObject { ["success"] = true, ["hooks"] = UpdateHooks("remove") }, ExitOk);
            return Emit(new JsonObject { ["success"] = true, ["installed"] = HooksInstalled(root!), ["settings"] = HookSettingsPath(root!).Replace('\\', '/') }, ExitOk);
        }
        if (sub is not ("install" or "sync"))
            return Fail("usage", "Usage: architector-mcp skill install <claude|codex|all> [--force] [--hooks] | skill hooks [remove] | skill sync", ExitUsage);
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
                    if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        File.WriteAllText(target, File.ReadAllText(file).Replace(PackageWorkflowLink, InstalledWorkflowLink));
                    else
                        File.Copy(file, target, true);
                }
                // The skill links the package's Workflow by a path that only resolves inside the package, so the
                // Workflow is installed next to the skill and the link points there.
                var workflow = Path.Combine(package.Value.Path, "Documentation~", "Workflow.md");
                if (File.Exists(workflow))
                    File.WriteAllText(Path.Combine(folder, InstalledWorkflowLink),
                        File.ReadAllText(workflow).Replace("../Skills~/" + SkillName + "/SKILL.md", "SKILL.md"));
                File.WriteAllText(stamp, package.Value.Version);
                status = ours ? "updated" : "installed";
            }
            results.Add((JsonNode)new JsonObject { ["agent"] = name, ["folder"] = folder.Replace('\\', '/'), ["status"] = status });
        }
        var conflict = results.Any(item => ((string?)item?["status"])?.StartsWith("conflict", StringComparison.Ordinal) == true);
        var reply = new JsonObject { ["success"] = !conflict, ["version"] = package.Value.Version, ["skills"] = results };
        if (sub == "install" && options.ContainsKey("hooks"))
            reply["hooks"] = agent is "claude" or "all" ? UpdateHooks("install") : new JsonObject { ["status"] = "hooks are for Claude Code; use skill install claude --hooks" };
        else if (sub == "sync")
            reply["hooks"] = UpdateHooks("sync");
        return Emit(reply, conflict ? ExitFailed : ExitOk);
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
        text.AppendLine($"query and edit: the Editor answers the command line while the integration Architector CLI is on (Window > Architector > Settings > AI > Connections). While Unity runs for the project but its bridge does not answer (compile, domain reload), they retry for up to {DefaultEditorWaitSeconds} s, then exit 7; --wait <seconds> changes it, 0 turns it off. An edit that reached the Editor is never sent twice.");
        text.AppendLine("Paged replies take --offset and --limit and report next_offset; --output file writes the full answer under Library/Architector/facts.");
        text.AppendLine("Every reply is JSON with success, except route and digest, which print the text of their MCP tools, and check, which prints findings only (--json gives JSON). Exit codes: 0 success, 1 internal error, 2 usage, 3 check findings, 4 setup missing, 6 success=false (also integration_disabled), 7 Unity Editor not reachable (after the wait; retry when it is open).");
        text.AppendLine("Every call is logged to Library/Architector/calls.jsonl (time, caller, action, arguments, reply size, duration); ARCHITECTOR_CALL_LOG=0 turns that off.");
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
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) && name is not ("pretty" or "force" or "help" or "json" or "full" or "git" or "hooks"))
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
        // A value that looks like JSON is parsed as JSON; text that only starts with a bracket ("[InitializeOnLoad] hook")
        // stays text.
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(value);
            }
            catch (System.Text.Json.JsonException)
            {
                return value;
            }
        }
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
