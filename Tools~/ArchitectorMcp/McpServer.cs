using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Architector.CodeFacts;

public static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private const string Instructions = "Architector holds the architecture of this Unity project as layers with contracts. Before changing an unfamiliar file or type, call architector_route and read only what it names. Use architector_digest for approved layers. Code facts work without Unity: before reading a layer's code call architector_query layer_facts; before moving a scope call scope_what_if; layer_dump and lookup give ids by name. Writing needs the open Unity Editor: use architector_edit with a work session (begin_work, dry_run_plan, apply_plan, end_work). Saving does not approve. Approving needs the user's confirmation in the Editor unless capabilities reports agent_may_approve.";
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
        root = FindProject(args);
        if (root != null)
        {
            facts = new FactsCache(root, () => Basic.Reference.Assemblies.Net100.References.All);
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
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = (string?)parameters?["protocolVersion"] ?? ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "architector", ["version"] = Version },
                ["instructions"] = Instructions
            },
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = Tools() },
            "tools/call" => Call((string?)parameters?["name"] ?? "", parameters?["arguments"]),
            _ => null
        };
    }

    private static JsonArray Tools()
    {
        return new JsonArray(
            Tool("architector_status", "Report the Unity project this server serves, whether it has architecture data and whether the Unity Editor is reachable. Works without Unity.", Schema(new JsonObject())),
            Tool("architector_route", "From a file path or type name to its Architector layer, digest lines and the documents worth reading. Works without Unity.",
                Schema(new JsonObject
                {
                    ["queries"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "File paths or type names." },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum documents per query, default 6." }
                }, "queries")),
            Tool("architector_digest", "Read the approved architecture. Without layer it lists the approved layers; with layer it returns that layer's section. Works without Unity.",
                Schema(new JsonObject { ["layer"] = new JsonObject { ["type"] = "string", ["description"] = "Layer path, for example Core/Player/Movement." } })),
            Tool("architector_query", "Read Architector data. code_evidence, layer_facts, scope_what_if, layer_dump and lookup answer without Unity from the code and the Architector files; the other actions need the open Unity Editor. Prefer the narrow actions node, relationships and dependency_path, or layer_graph with detail=summary, over a full layer_graph.", ActionSchema(
                new[] { "capabilities", "work_status", "project_summary", "group_contents", "layer_graph", "node", "relationships", "dependency_path", "setup_check", "approval_status", "baseline_diff", "conformance", "find", "type_search", "type_contract", "validate", "code_evidence", "layer_facts", "scope_what_if", "layer_dump", "lookup" },
                new JsonObject
                {
                    ["target"] = TextParameter("Layer or group ID or path; a type for type_contract."),
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
                    ["output"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("reply", "file"), ["description"] = "file writes the full answer under Library/Architector/facts and returns its path." }
                })),
            Tool("architector_edit", "Change the architecture in the open Unity Editor. Call architector_query with action capabilities for the plan format and rules.", ActionSchema(
                new[] { "begin_work", "resume_work", "end_work", "approve_layers", "revoke_approvals", "revert_to_approved", "dry_run", "apply", "dry_run_plan", "apply_plan", "arrange_layer", "arrange_changed_layers", "relink_node", "validate_external", "reload_external" },
                new JsonObject
                {
                    ["expected_revision"] = new JsonObject { ["type"] = "integer" },
                    ["work_token"] = TextParameter("Token returned by begin_work."),
                    ["work_label"] = TextParameter("What this work session does."),
                    ["plan"] = new JsonObject { ["type"] = "object", ["description"] = "A semantic plan, or {\"plan_token\": \"...\"} from dry_run_plan." },
                    ["layers"] = new JsonObject { ["type"] = "array", ["items"] = TextParameter("Layer ID.") },
                    ["approve_changes"] = new JsonObject { ["type"] = "boolean" },
                    ["target"] = TextParameter("Layer ID or path for arrange_layer and relink_node."),
                    ["node"] = TextParameter("Node ID for relink_node."),
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
            "architector_query" => FactsQueries.Actions.Contains((string?)arguments?["action"] ?? string.Empty) ? Facts(arguments) : Bridge("query", arguments),
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
            ["editor_connected"] = root != null && EditorReachable()
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
        catch (SocketException)
        {
            return false;
        }
    }

    private static string Route(JsonNode? arguments)
    {
        var routeArguments = new List<string> { root! };
        routeArguments.AddRange(arguments?["queries"]?.AsArray().Select(item => (string)item!) ?? Enumerable.Empty<string>());
        if (arguments?["limit"] != null)
            routeArguments.Add("--limit=" + (int)arguments["limit"]!);
        var console = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Program.Main(routeArguments.ToArray());
        }
        finally
        {
            Console.SetOut(console);
        }
        return captured.ToString();
    }

    private static JsonNode Digest(JsonNode? arguments)
    {
        var path = Path.Combine(DataRoot, "Architecture.digest.md");
        if (!File.Exists(path))
            return Text("No layer is approved yet, so there is no digest. Use architector_query for drafts.", true);
        var lines = File.ReadAllLines(path);
        var headings = lines.Select((line, index) => (Line: line, Index: index)).Where(entry => entry.Line.StartsWith("## ")).ToList();
        var layer = (string?)arguments?["layer"];
        if (string.IsNullOrEmpty(layer))
            return Text(string.Join("\n", headings.Select(entry => entry.Line[3..].Trim())), false);
        var position = headings.FindIndex(entry => string.Equals(entry.Line[3..].Trim(), layer, StringComparison.OrdinalIgnoreCase));
        if (position < 0)
            return Text("No approved layer is called " + layer + ". Call architector_digest without layer for the list.", true);
        var end = position + 1 < headings.Count ? headings[position + 1].Index : lines.Length;
        return Text(string.Join("\n", lines[headings[position].Index..end]).TrimEnd(), false);
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

    private static JsonNode Bridge(string tool, JsonNode? arguments)
    {
        const string notRunning = "The Unity Editor is not running for this project, or it is compiling. Open the project and try again. architector_route and architector_digest work without it.";
        if (!File.Exists(BridgeState))
            return Text(notRunning, true);
        try
        {
            var bridge = JsonNode.Parse(File.ReadAllText(BridgeState))!;
            using var client = new TcpClient("127.0.0.1", (int)bridge["port"]!);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Utf8) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(stream, Utf8);
            writer.WriteLine(new JsonObject { ["token"] = (string?)bridge["token"], ["tool"] = tool, ["parameters"] = arguments?.DeepClone() ?? new JsonObject() }.ToJsonString());
            var answer = reader.ReadLine();
            if (answer == null)
                return Text(notRunning, true);
            return Text(answer, !((bool?)JsonNode.Parse(answer)!["success"] ?? false));
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return Text(notRunning, true);
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
