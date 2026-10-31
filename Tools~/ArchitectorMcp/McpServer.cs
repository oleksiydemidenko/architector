using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

public static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static string root = "";

    public static int Main(string[] args)
    {
        root = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? Directory.GetCurrentDirectory());
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true, NewLine = "\n" };
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0)
                continue;
            var message = JsonNode.Parse(line)!;
            var id = message["id"]?.DeepClone();
            if (id == null)
                continue;
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
            var result = Handle((string?)message["method"] ?? "", message["params"]);
            if (result != null)
                response["result"] = result;
            else
                response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
            output.WriteLine(response.ToJsonString());
        }
        return 0;
    }

    private static JsonNode? Handle(string method, JsonNode? parameters)
    {
        return method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = (string?)parameters?["protocolVersion"] ?? ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "architector", ["version"] = "0.1.2" }
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
            Tool("architector_route", "From a file path or type name to its Architector layer, digest lines and the documents worth reading. Works without Unity.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["queries"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["limit"] = new JsonObject { ["type"] = "integer" }
                    },
                    ["required"] = new JsonArray("queries")
                }),
            Tool("architector_query", "Read Architector data from the open Unity Editor. Pass action and its parameters. Actions: capabilities, work_status, project_summary, group_contents, layer_graph, approval_status, baseline_diff, conformance, find, type_search, type_contract, validate. Parameters: target, query, limit, include_layout.", Open()),
            Tool("architector_edit", "Change the architecture in the open Unity Editor. Pass action and its parameters. Actions: begin_work, resume_work, end_work, approve_layers, revoke_approvals, revert_to_approved, dry_run, apply, dry_run_plan, apply_plan, validate_external, reload_external. Call architector_query capabilities for the contract.", Open()));
    }

    private static JsonObject Tool(string name, string description, JsonObject schema)
    {
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Open()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["action"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("action"),
            ["additionalProperties"] = true
        };
    }

    private static JsonNode Call(string name, JsonNode? arguments)
    {
        return name switch
        {
            "architector_route" => Text(Route(arguments), false),
            "architector_query" => Bridge("query", arguments),
            "architector_edit" => Bridge("edit", arguments),
            _ => Text("Unknown tool: " + name, true)
        };
    }

    private static string Route(JsonNode? arguments)
    {
        var queries = arguments?["queries"]?.AsArray().Select(item => (string)item!).ToList() ?? new List<string>();
        var routeArguments = new List<string> { root };
        routeArguments.AddRange(queries);
        if (arguments?["limit"] != null)
            routeArguments.Add("--limit=" + (int)arguments["limit"]!);
        var console = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        Program.Main(routeArguments.ToArray());
        Console.SetOut(console);
        return captured.ToString();
    }

    private static JsonNode Bridge(string tool, JsonNode? arguments)
    {
        var state = Path.Combine(root, "Library", "Architector", "agent-bridge.json");
        const string notRunning = "The Unity Editor is not running for this project, or it is compiling. Open the project and try again. architector_route works without it.";
        if (!File.Exists(state))
            return Text(notRunning, true);
        try
        {
            var bridge = JsonNode.Parse(File.ReadAllText(state))!;
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
