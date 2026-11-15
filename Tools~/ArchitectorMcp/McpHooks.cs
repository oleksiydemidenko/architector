using System.Text.Json.Nodes;
using Architector.CodeFacts;

// Optional Claude Code hooks in the project's .claude/settings.local.json (personal, not committed): after an Edit or
// Write of a .cs, .prefab, .unity or .asset file the check runs for that file, and when the agent stops it runs for the
// files git sees as changed. Both are silent when clean. skill install claude --hooks adds them, skill hooks remove takes
// them out again, skill sync points them at the current binary.
public static partial class McpServer
{
    private const string HookMarker = "check --hook";

    private static string HookSettingsPath(string project) => Path.Combine(project, ".claude", "settings.local.json");

    private static string Quote(string path) => "\"" + path.Replace('\\', '/') + "\"";

    private static string HookCommand(string mode) =>
        $"{Quote(Environment.ProcessPath ?? "architector-mcp")} {HookMarker} {mode} --project {Quote(root!)}";

    private static bool IsOurHook(JsonNode? hook) =>
        ((string?)hook?["command"])?.Contains(HookMarker, StringComparison.Ordinal) == true
        && ((string?)hook?["command"])?.Contains("architector-mcp", StringComparison.OrdinalIgnoreCase) == true;

    private static JsonObject ReadSettings(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(path), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new InvalidOperationException($"{path} is not a JSON object.");
    }

    private static void WriteSettings(string path, JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, settings.ToJsonString(FactsQueries.Indented) + "\n");
    }

    // Removes Architector's hook entries and the groups and events they leave empty; returns how many were removed.
    private static int RemoveOurHooks(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks)
            return 0;
        var removed = 0;
        foreach (var eventName in hooks.Select(pair => pair.Key).ToArray())
        {
            if (hooks[eventName] is not JsonArray groups)
                continue;
            foreach (var group in groups.OfType<JsonObject>().ToArray())
            {
                if (group["hooks"] is not JsonArray entries)
                    continue;
                foreach (var entry in entries.Where(IsOurHook).ToArray())
                {
                    entries.Remove(entry);
                    removed++;
                }
                if (entries.Count == 0)
                    groups.Remove(group);
            }
            if (groups.Count == 0)
                hooks.Remove(eventName);
        }
        if (hooks.Count == 0)
            settings.Remove("hooks");
        return removed;
    }

    private static void AddOurHooks(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks)
            settings["hooks"] = hooks = new JsonObject();
        void Add(string eventName, string? matcher, string mode, int timeout)
        {
            if (hooks[eventName] is not JsonArray groups)
                hooks[eventName] = groups = new JsonArray();
            var group = new JsonObject();
            if (matcher != null)
                group["matcher"] = matcher;
            group["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = HookCommand(mode), ["timeout"] = timeout });
            groups.Add(group);
        }
        Add("PostToolUse", "Edit|Write|MultiEdit", "post", 30);
        Add("Stop", null, "stop", 60);
    }

    private static bool HooksInstalled(string project)
    {
        try
        {
            var settings = ReadSettings(HookSettingsPath(project));
            return settings["hooks"] is JsonObject hooks && hooks.Any(pair => pair.Value is JsonArray groups
                && groups.OfType<JsonObject>().Any(group => group["hooks"] is JsonArray entries && entries.Any(IsOurHook)));
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // install: replace Architector's hooks by the current ones; remove: take them out; sync: renew them only when present.
    private static JsonObject UpdateHooks(string mode)
    {
        var path = HookSettingsPath(root!);
        var settings = ReadSettings(path);
        var removed = RemoveOurHooks(settings);
        if (mode == "remove" || mode == "sync" && removed == 0)
        {
            if (removed > 0)
            {
                if (settings.Count == 0 && File.Exists(path))
                    File.Delete(path);
                else
                    WriteSettings(path, settings);
            }
            return new JsonObject { ["settings"] = path.Replace('\\', '/'), ["status"] = removed > 0 ? "removed" : "not installed" };
        }
        AddOurHooks(settings);
        WriteSettings(path, settings);
        return new JsonObject
        {
            ["settings"] = path.Replace('\\', '/'),
            ["status"] = removed > 0 ? "updated" : "installed",
            ["post_tool_use"] = HookCommand("post"),
            ["stop"] = HookCommand("stop"),
            ["turn_off"] = "architector-mcp skill hooks remove, or delete the two entries with 'check --hook' from that file."
        };
    }
}
