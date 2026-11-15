using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Architector.CodeFacts;

public static partial class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private const string Instructions = "Architector holds the architecture of this Unity project as layers with contracts, and facts about the project that a code search does not show. Search the code for names and text; use Architector when the answer lives outside the C# text: where a component is placed and which serialized lists start it, and whether a type is wired at all (architector_query layer_facts with type: wiring); who writes or reads a state type, where that runs (tick, callback, event) and on which prefab instance (architector_query slice with target); whether edited prefabs, scenes or placed scripts break the project's asset rules (architector_query check with files); which layer owns a file and which layer dependencies the code evidences (code_evidence; scope_what_if for a planned move); the approved design of named layers (architector_digest with layers; a bare call only lists them). Code facts work without Unity; layer_dump and lookup give ids by name. Writing needs the open Unity Editor: use architector_edit with a work session (begin_work, dry_run_plan, apply_plan, end_work). Saving does not approve. Approving needs the user's confirmation in the Editor unless capabilities reports agent_may_approve. With a shell, the same binary is a command line: architector_status gives its path (cli); run it with help.";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static string? root;
    private static FactsCache? facts;

    public static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine(Version);
            return 0;
        }
        // With a command as its first argument the binary is the architector command line; otherwise an MCP server.
        if (IsCliInvocation(args))
            return RunCli(args);
        root = FindProject(args);
        if (root != null)
        {
            facts = new FactsCache(root, "net100", () => Basic.Reference.Assemblies.Net100.References.All);
            facts.Warm();
        }
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true, NewLine = "\n" };
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0 && Answer(line) is { } answer)
                output.WriteLine(answer.ToJsonString());
        }
        return 0;
    }

    private static string Version => typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static string? FindProject(string[] args)
    {
        var explicitPath = args.SkipWhile(arg => arg != "--project").Skip(1).FirstOrDefault()
            ?? args.FirstOrDefault(arg => !arg.StartsWith("--"))
            ?? Environment.GetEnvironmentVariable("ARCHITECTOR_PROJECT");
        for (var directory = new DirectoryInfo(explicitPath ?? Directory.GetCurrentDirectory()); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Assets")) && Directory.Exists(Path.Combine(directory.FullName, "ProjectSettings")))
                return directory.FullName;
        return null;
    }

    private static JsonObject? Answer(string line)
    {
        JsonNode message;
        try
        {
            message = JsonNode.Parse(line)!;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return Failure(null, -32700, "Parse error");
        }
        var id = message["id"]?.DeepClone();
        if (id == null)
            return null;
        try
        {
            var result = Handle((string?)message["method"] ?? "", message["params"]);
            return result == null
                ? Failure(id, -32601, "Method not found")
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (Exception exception)
        {
            return Failure(id, -32603, exception.Message);
        }
    }

    private static JsonObject Failure(JsonNode? id, int code, string text)
    {
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };
    }

    private static JsonNode? Handle(string method, JsonNode? parameters)
    {
        return method switch
        {
            "initialize" => Initialize(parameters) ?? new JsonObject
            {
                ["protocolVersion"] = (string?)parameters?["protocolVersion"] ?? ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "architector", ["version"] = Version },
                ["instructions"] = Instructions
            },
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = Tools() },
            "tools/call" => LoggedCall(parameters),
            _ => null
        };
    }

    private static JsonArray Tools()
    {
        return new JsonArray(
            Tool("architector_status", "Report the Unity project this server serves, whether it has architecture data and whether the Unity Editor is reachable. Works without Unity.", Schema(new JsonObject())),
            Tool("architector_route", "From a file path or type name to its Architector layer, digest lines and the documents worth reading; for the node it names, the types that write and read its state (from the code facts). Works without Unity.",
                Schema(new JsonObject
                {
                    ["queries"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "File paths or type names." },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum documents per query, default 6." }
                }, "queries")),
            Tool("architector_digest", "Read the approved architecture. Without layers it only lists the approved layers by group with their node counts (cheap); ask for content by name. A layer gives its full section (members, base types, relationships); a group gives its layers in the compact form (node, type, first sentence of its role, and the types that write and read its state, generated from the code facts). form compact without layers gives every layer compactly, which is large on big graphs; form full the whole digest; list only layer names. Works without Unity.",
                Schema(new JsonObject
                {
                    ["layer"] = new JsonObject { ["type"] = "string", ["description"] = "Layer path or a unique trailing part of it, for example Core/Player/Movement; a comma list names several." },
                    ["layers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Several layers." },
                    ["form"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("index", "compact", "full", "list"), ["description"] = "index (default without layers: groups, layers, node counts), full (default for layers), compact (default for groups), list (layer names only)." }
                })),
            Tool("architector_query", "Read Architector data. code_evidence, layer_facts, scope_what_if, layer_dump, lookup, facts_audit (graph against facts), facts_snapshot, facts_diff, slice (writers and readers of a type with the lines ticks and callbacks run and the prefab instance) and check (asset rules for changed files) answer without Unity from the code and the Architector files; the other actions need the open Unity Editor. Prefer the narrow actions node, relationships and dependency_path, or layer_graph with detail=summary, over a full layer_graph.", ActionSchema(
                new[] { "capabilities", "work_status", "project_summary", "group_contents", "layer_graph", "node", "relationships", "dependency_path", "setup_check", "approval_status", "baseline_diff", "conformance", "find", "type_search", "type_contract", "validate", "code_evidence", "layer_facts", "scope_what_if", "layer_dump", "lookup", "facts_audit", "facts_snapshot", "facts_diff", "check", "slice" },
                new JsonObject
                {
                    ["target"] = TextParameter("Layer or group ID or path; a type for type_contract; a type or C# file for slice."),
                    ["node"] = TextParameter("Node ID or title in the target layer."),
                    ["to"] = TextParameter("Destination layer for dependency_path."),
                    ["query"] = TextParameter("Search text."),
                    ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("in", "out", "both") },
                    ["detail"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("full", "summary") },
                    ["limit"] = new JsonObject { ["type"] = "integer" },
                    ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Use next_offset from the previous result." },
                    ["include_layout"] = new JsonObject { ["type"] = "boolean" },
                    ["layer"] = TextParameter("code_evidence: only pairs touching this layer."),
                    ["pair"] = TextParameter("code_evidence: one pair, \"Source/Layer -> Target/Layer\"."),
                    ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("undeclared", "cycle", "unevidenced") },
                    ["include"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("references", "owners", "intra"), ["description"] = "code_evidence: page full lists instead of pairs." },
                    ["type"] = TextParameter("layer_facts: one type of the layer with its public members."),
                    ["moves"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" }, ["description"] = "scope_what_if: [{\"type\" | \"file\" | \"folder\": \"...\", \"to\": \"Layer/Path\"}]." },
                    ["names"] = new JsonObject { ["type"] = "array", ["items"] = TextParameter("Layer/Path, Layer/Path::Node Title or Layer/Path::Node Title::Member."), ["description"] = "lookup: names to ids." },
                    ["output"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("reply", "file"), ["description"] = "file writes the full answer under Library/Architector/facts and returns its path." },
                    ["section"] = TextParameter("layer_facts with type: page one section (reads, writes, uses, subscribes, unsubscribes, assigns, lifecycle, recurring, wiring, base_of)."),
                    ["min_uses"] = new JsonObject { ["type"] = "integer", ["description"] = "facts_audit: uses by other layers that make a type central (default 20)." },
                    ["name"] = TextParameter("facts_snapshot: snapshot name (default: the time)."),
                    ["snapshot"] = TextParameter("facts_diff: a snapshot name or file."),
                    ["against"] = TextParameter("facts_diff: another snapshot; default the current code."),
                    ["files"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "check: changed files (project paths); none checks the whole project." }
                })),
            Tool("architector_edit", "Change the architecture in the open Unity Editor. Call architector_query with action capabilities for the plan format and rules.", ActionSchema(
                new[] { "begin_work", "resume_work", "end_work", "approve_layers", "revoke_approvals", "revert_to_approved", "dry_run", "apply", "dry_run_plan", "apply_plan", "arrange_layer", "arrange_changed_layers", "relink_node", "set_standalone", "validate_external", "reload_external" },
                new JsonObject
                {
                    ["expected_revision"] = new JsonObject { ["type"] = "integer" },
                    ["work_token"] = TextParameter("Token returned by begin_work."),
                    ["work_label"] = TextParameter("What this work session does."),
                    ["plan"] = new JsonObject { ["type"] = "object", ["description"] = "A semantic plan, or {\"plan_token\": \"...\"} from dry_run_plan." },
                    ["layers"] = new JsonObject { ["type"] = "array", ["items"] = TextParameter("Layer ID.") },
                    ["approve_changes"] = new JsonObject { ["type"] = "boolean", ["description"] = "approve_layers needs true when the layers changed since their last approval." },
                    ["target"] = TextParameter("Layer ID or path for arrange_layer, relink_node and set_standalone."),
                    ["node"] = TextParameter("Node ID for relink_node; node ID or title for set_standalone."),
                    ["reason"] = TextParameter("set_standalone: why the linked node has no relationship, from the code evidence (for example wired only in scenes). Empty clears the mark."),
                    ["type_key"] = TextParameter("Indexed type key for relink_node."),
                    ["nodes"] = new JsonObject { ["type"] = "array", ["items"] = TextParameter("Node ID.") },
                    ["centers"] = new JsonObject { ["type"] = "array", ["items"] = TextParameter("Node ID.") }
                })));
    }

    private static JsonObject Tool(string name, string description, JsonObject schema)
    {
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
            schema["required"] = new JsonArray(required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        return schema;
    }

    private static JsonObject ActionSchema(string[] actions, JsonObject properties)
    {
        properties["action"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(actions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray())
        };
        var schema = Schema(properties, "action");
        schema["additionalProperties"] = true;
        return schema;
    }

    private static JsonObject TextParameter(string description)
    {
        return new JsonObject { ["type"] = "string", ["description"] = description };
    }

    private static JsonNode LoggedCall(JsonNode? parameters)
    {
        var name = (string?)parameters?["name"] ?? "";
        var arguments = parameters?["arguments"];
        var watch = StartCall();
        var reply = Call(name, arguments);
        var characters = reply["content"] is JsonArray content ? content.Sum(item => ((string?)item?["text"])?.Length ?? 0) : 0;
        var action = (string?)arguments?["action"];
        var meta = parameters?["_meta"];
        LogCall("mcp", action == null ? name : $"{name} {action}", arguments, characters, watch.ElapsedMilliseconds,
            (string?)meta?["sessionId"] ?? (string?)meta?["session_id"] ?? clientSession);
        return reply;
    }

    // A session id the client sent at initialize, if any (no standard field; some clients put one in _meta).
    private static string? clientSession;

    private static JsonNode Call(string name, JsonNode? arguments)
    {
        if (name == "architector_status")
            return Text(Status(), false);
        if (root == null)
            return Text("No Unity project found. Start the server inside a Unity project or pass --project <path>.", true);
        return name switch
        {
            "architector_route" => HasData() ? Text(Route(arguments), false) : Text(NoData, true),
            "architector_digest" => Digest(arguments),
            "architector_query" => (string?)arguments?["action"] == "check" ? Check(arguments)
                : FactsQueries.Actions.Contains((string?)arguments?["action"] ?? string.Empty) ? Facts(arguments) : Bridge("query", arguments),
            "architector_edit" => Bridge("edit", arguments),
            _ => Text("Unknown tool: " + name, true)
        };
    }

    private const string NoData = "This project has no Architector data yet. Install the package in Unity and open the project once.";
    private static string DataRoot => Path.Combine(root!, "Architector");
    private static string BridgeState => Path.Combine(root!, "Library", "Architector", "agent-bridge.json");
    private static bool HasData() => Directory.Exists(DataRoot);

    private static string Status()
    {
        var status = new JsonObject
        {
            ["version"] = Version,
            ["project"] = root,
            ["has_architecture"] = root != null && HasData(),
            ["has_digest"] = root != null && File.Exists(Path.Combine(DataRoot, "Architecture.digest.md")),
            ["editor_connected"] = root != null && EditorReachable(),
            ["unity_running"] = root != null && UnityRunning(),
            ["editor_serves"] = root == null ? null : BridgeClients(),
            ["integrations"] = root == null || !HasData() ? null : new JsonObject
            {
                [CliId] = IntegrationOn(CliId),
                [McpServerId] = IntegrationOn(McpServerId)
            },
            ["cli"] = Environment.ProcessPath?.Replace(Path.DirectorySeparatorChar, '/'),
            ["cli_help"] = "Agents with a shell can run this binary as a command line (help lists the commands); replies are the same JSON. "
                + "The Editor answers the command line while the integration Architector CLI is on, the MCP server while Architector MCP server is on (Window > Architector > Settings > AI > Connections).",
            ["editor_wait"] = $"Command line query and edit wait up to {DefaultEditorWaitSeconds} s (--wait <seconds>, 0 turns it off) while Unity runs for the project but its bridge does not answer (compile, domain reload); then exit code 7."
        };
        return status.ToJsonString();
    }

    private static bool EditorReachable()
    {
        if (!File.Exists(BridgeState))
            return false;
        try
        {
            using var client = new TcpClient("127.0.0.1", (int)JsonNode.Parse(File.ReadAllText(BridgeState))!["port"]!);
            return client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or IOException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static string Route(JsonNode? arguments)
    {
        var queries = arguments?["queries"]?.AsArray().Select(item => (string)item!) ?? Enumerable.Empty<string>();
        var limit = arguments?["limit"] == null ? (int?)null : (int)arguments["limit"]!;
        return RouteText(queries, limit, false, out _);
    }

    private static JsonNode Digest(JsonNode? arguments)
    {
        var text = DigestText(arguments, out var error);
        return Text(text, error);
    }

    private static JsonObject? Initialize(JsonNode? parameters)
    {
        clientSession = (string?)parameters?["_meta"]?["sessionId"] ?? (string?)parameters?["_meta"]?["session_id"];
        return null;
    }

    // check through MCP: the warm server answers from its index, a stale one while it refreshes in the background.
    private static JsonNode Check(JsonNode? arguments)
    {
        var files = arguments?["files"] switch
        {
            JsonArray list => list.Select(item => (string?)item ?? string.Empty).ToList(),
            JsonValue value when value.TryGetValue<string>(out var text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            _ => new List<string>()
        };
        var (index, state) = facts!.IndexForCheck();
        var answer = FactsRules.Check(index, state, files, root!, files.Count == 0);
        return Text(answer.ToJsonString(FactsQueries.Compact), false);
    }

    private static JsonNode Facts(JsonNode? arguments)
    {
        if (!HasData())
            return Text(NoData, true);
        var action = (string?)arguments?["action"] ?? string.Empty;
        var model = FactsQueries.ArchitectureOnlyActions.Contains(action) ? facts!.Architecture() : facts!.Get();
        var answer = FactsQueries.Answer(model, action, arguments, root!);
        return Text(answer.ToJsonString(FactsQueries.Compact), answer["success"]?.GetValue<bool>() != true);
    }

    private const string NotRunning = "The Unity Editor is not running for this project, or it is compiling. Open the project and try again. architector_route and architector_digest work without it.";

    // As the command line: the Editor when its bridge answers now, otherwise the Architector core on the files.
    private static JsonNode Bridge(string tool, JsonNode? arguments)
    {
        var parameters = arguments?.DeepClone() as JsonObject ?? new JsonObject();
        var answer = BridgeAnswer(tool, parameters, McpClient, out var reached, ClaimSeconds);
        JsonNode reply;
        if (answer != null && (string?)JsonNode.Parse(answer)!["code"] != "editor_timeout")
        {
            reply = JsonNode.Parse(answer)!;
            reply["source"] = "editor";
        }
        else if (answer == null && reached && tool == "edit")
            return Text("The Editor closed the connection after it got the edit, so the edit may have run. Check query work_status or the revision before you send it again.", true);
        else
            reply = LocalAnswer(tool, parameters, McpServerId, answer != null ? $"the Editor did not start the call in {ClaimSeconds} s" : "no Unity Editor answers now");
        return Text(reply.ToJsonString(), !((bool?)reply["success"] ?? false));
    }

    // The bridge answers each client only while its own integration is on: "mcp" needs Architector MCP server,
    // "cli" needs Architector CLI (Window > Architector > Settings > AI > Connections).
    private const string McpClient = "mcp";
    private const string CliClient = "cli";
    private const string McpServerId = "architector-mcp";
    private const string CliId = "architector-cli";

    // The Editor's raw reply, or null when no Editor serves this project. sent is false when the request never
    // reached the Editor (no state file, connection refused), so a retry cannot run it twice.
    private static string? BridgeAnswer(string tool, JsonNode? arguments, string clientName, out bool sent, int? claimSeconds = null)
    {
        sent = false;
        if (!File.Exists(BridgeState))
            return null;
        try
        {
            var bridge = JsonNode.Parse(File.ReadAllText(BridgeState))!;
            using var client = new TcpClient("127.0.0.1", (int)bridge["port"]!);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Utf8) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(stream, Utf8);
            sent = true;
            var request = new JsonObject { ["token"] = (string?)bridge["token"], ["client"] = clientName, ["tool"] = tool, ["parameters"] = arguments?.DeepClone() ?? new JsonObject() };
            if (claimSeconds != null)
                request["claim_seconds"] = claimSeconds.Value;
            writer.WriteLine(request.ToJsonString());
            var reply = reader.ReadLine();
            return tool == "edit" ? WithCodeReferences(reply) : reply;
        }
        catch (Exception exception) when (exception is SocketException or IOException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
        {
            // A state file the Editor is still writing, a stopped listener or a closed connection: no Editor answer.
            return null;
        }
    }

    // Unity holds Temp/UnityLockfile open while it has the project open, also while it compiles or reloads.
    private static bool UnityRunning()
    {
        var lockFile = Path.Combine(root!, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile))
            return false;
        try
        {
            using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // A built-in integration switch as saved in Architector/architector.settings.json; a missing record means on.
    private static bool IntegrationOn(string integrationId)
    {
        var settings = Path.Combine(DataRoot, "architector.settings.json");
        if (!File.Exists(settings))
            return true;
        try
        {
            var records = JsonNode.Parse(File.ReadAllText(settings))?["agent"]?["integrations"] as JsonArray;
            var record = records?.FirstOrDefault(item => (string?)item?["id"] == integrationId);
            return (bool?)record?["enabled"] ?? true;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or InvalidOperationException or FormatException)
        {
            return true;
        }
    }

    // The clients the running bridge serves, from its state file; an older Editor writes none.
    private static JsonArray? BridgeClients()
    {
        try
        {
            return File.Exists(BridgeState) ? JsonNode.Parse(File.ReadAllText(BridgeState))?["clients"]?.DeepClone() as JsonArray : null;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    // The Editor names the layer pairs a plan leaves without any dependency; the code facts add how many references
    // in code go from one to the other, so the agent sees what the deletion would make undeclared.
    private static string? WithCodeReferences(string? reply)
    {
        if (reply == null || !reply.Contains("\"permissions_lost\"", StringComparison.Ordinal) || root == null)
            return reply;
        try
        {
            var node = JsonNode.Parse(reply);
            if (node?["data"]?["permissions_lost"] is not JsonArray lost)
                return reply;
            var model = (facts ?? new FactsCache(root, "net100", () => Basic.Reference.Assemblies.Net100.References.All)).Get();
            foreach (var item in lost.OfType<JsonObject>())
            {
                var source = (string?)item["source_layer_id"];
                var target = (string?)item["target_layer_id"];
                item["code_references"] = model.References.Count(reference => reference.CountsAsEvidence
                                                                             && reference.SourceLayerId == source
                                                                             && reference.TargetLayerId == target);
            }
            return node.ToJsonString();
        }
        catch (Exception)
        {
            // Without code facts the Editor's warning still stands, only without the count.
            return reply;
        }
    }

    private static JsonObject Text(string text, bool isError)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };
    }
}
