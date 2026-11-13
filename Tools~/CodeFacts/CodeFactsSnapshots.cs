using System.Text.Json.Nodes;

namespace Architector.CodeFacts;

// Facts snapshots under Library/Architector/snapshots and the differences between two of them, or between one and the
// current code: after a tool update or a code change an agent checks only what changed instead of every layer.
// A snapshot keeps, per type, its owner layer, wiring status, behaviour values and public members, and per layer pair
// the dependency evidence with its type pairs. Line numbers are left out, so moved code is not a change.
public static class FactsSnapshots
{
    private const int Format = 1;

    public static string Folder(string root) => Path.Combine(root, "Library", "Architector", "snapshots");

    public static JsonObject Save(FactsModel model, JsonNode? arguments, string root)
    {
        if (model.Index == null)
            return FactsQueries.Failure("no_code_scope", "No layer has a code scope, so there are no code facts to save.");
        var name = FactsQueries.Text(arguments, "name");
        if (name.Length == 0)
            name = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            return FactsQueries.Failure("invalid_name", $"Snapshot name '{name}' is not a valid file name.");
        var snapshot = Capture(model, root);
        Directory.CreateDirectory(Folder(root));
        var path = Path.Combine(Folder(root), name + ".json");
        File.WriteAllText(path, snapshot.ToJsonString(FactsQueries.Compact));
        var result = FactsQueries.Success();
        result["snapshot"] = name;
        result["file"] = path.Replace('\\', '/');
        result["types"] = (snapshot["types"] as JsonObject)?.Count ?? 0;
        result["pairs"] = (snapshot["pairs"] as JsonObject)?.Count ?? 0;
        return result;
    }

    public static JsonObject Diff(FactsModel model, JsonNode? arguments, string root)
    {
        var first = FactsQueries.Text(arguments, "snapshot");
        if (first.Length == 0)
            return Missing(root, "Give snapshot (a name from facts snapshot, or a file path), and optionally against (another one; default: the current code).");
        var before = Load(root, first);
        if (before == null)
            return Missing(root, $"No snapshot '{first}'.");
        var second = FactsQueries.Text(arguments, "against");
        JsonObject? after;
        if (second.Length > 0)
        {
            after = Load(root, second);
            if (after == null)
                return Missing(root, $"No snapshot '{second}'.");
        }
        else
        {
            if (model.Index == null)
                return FactsQueries.Failure("no_code_scope", "No layer has a code scope, so there are no current code facts to compare.");
            after = Capture(model, root);
        }

        var changes = new List<JsonObject>();
        var beforeTypes = before["types"] as JsonObject ?? new JsonObject();
        var afterTypes = after["types"] as JsonObject ?? new JsonObject();
        foreach (var (type, value) in afterTypes.Where(pair => !beforeTypes.ContainsKey(pair.Key)))
            changes.Add(new JsonObject { ["kind"] = "type_added", ["type"] = type, ["owner"] = value?["owner"]?.DeepClone(), ["wiring"] = value?["wiring"]?.DeepClone() });
        foreach (var (type, value) in beforeTypes.Where(pair => !afterTypes.ContainsKey(pair.Key)))
            changes.Add(new JsonObject { ["kind"] = "type_removed", ["type"] = type, ["owner"] = value?["owner"]?.DeepClone() });
        foreach (var (type, old) in beforeTypes)
        {
            if (!afterTypes.TryGetPropertyValue(type, out var current) || old == null || current == null)
                continue;
            string? Text(JsonNode node, string name) => node[name]?.GetValue<string>();
            if (Text(old, "owner") != Text(current, "owner"))
                changes.Add(new JsonObject { ["kind"] = "owner_changed", ["type"] = type, ["from"] = Text(old, "owner"), ["to"] = Text(current, "owner") });
            if (Text(old, "wiring") != Text(current, "wiring"))
                changes.Add(new JsonObject { ["kind"] = "wiring_changed", ["type"] = type, ["from"] = Text(old, "wiring"), ["to"] = Text(current, "wiring") });
            var sections = new JsonObject();
            var oldBehaviour = old["behaviour"] as JsonObject ?? new JsonObject();
            var newBehaviour = current["behaviour"] as JsonObject ?? new JsonObject();
            foreach (var section in oldBehaviour.Select(pair => pair.Key).Union(newBehaviour.Select(pair => pair.Key)).OrderBy(name => name, StringComparer.Ordinal))
            {
                var (added, removed) = Compare(Strings(oldBehaviour[section]), Strings(newBehaviour[section]));
                if (added.Count + removed.Count > 0)
                    sections[section] = new JsonObject { ["added"] = Values(added), ["removed"] = Values(removed) };
            }
            if (sections.Count > 0)
                changes.Add(new JsonObject { ["kind"] = "behaviour_changed", ["type"] = type, ["sections"] = sections });
            var (membersAdded, membersRemoved) = Compare(Strings(old["members"]), Strings(current["members"]));
            if (membersAdded.Count + membersRemoved.Count > 0)
                changes.Add(new JsonObject { ["kind"] = "members_changed", ["type"] = type, ["added"] = Values(membersAdded), ["removed"] = Values(membersRemoved) });
        }
        var beforePairs = before["pairs"] as JsonObject ?? new JsonObject();
        var afterPairs = after["pairs"] as JsonObject ?? new JsonObject();
        foreach (var pair in beforePairs.Select(item => item.Key).Union(afterPairs.Select(item => item.Key)).OrderBy(name => name, StringComparer.Ordinal))
        {
            var old = beforePairs[pair];
            var current = afterPairs[pair];
            var oldCount = old?["references"]?.GetValue<int>() ?? 0;
            var newCount = current?["references"]?.GetValue<int>() ?? 0;
            var (typePairsAdded, typePairsRemoved) = Compare(Keys(old?["type_pairs"]), Keys(current?["type_pairs"]));
            if (oldCount == newCount && typePairsAdded.Count + typePairsRemoved.Count == 0)
                continue;
            changes.Add(new JsonObject
            {
                ["kind"] = "evidence_changed",
                ["pair"] = pair,
                ["from"] = oldCount,
                ["to"] = newCount,
                ["new_type_pairs"] = Values(typePairsAdded),
                ["lost_type_pairs"] = Values(typePairsRemoved)
            });
        }

        var result = FactsQueries.Success();
        result["snapshot"] = first;
        result["against"] = second.Length > 0 ? second : "current";
        var byKind = new JsonObject();
        foreach (var group in changes.GroupBy(change => (string)change["kind"]!).OrderBy(group => group.Key, StringComparer.Ordinal))
            byKind[group.Key] = group.Count();
        result["by_kind"] = byKind;
        FactsQueries.Page(result, "changes", changes, arguments, 100);
        return result;
    }

    private static JsonObject Capture(FactsModel model, string root)
    {
        var architecture = model.Architecture;
        var statuses = FactsQueries.WiringStatuses(model, AssetWiring.Load(root));
        var types = new JsonObject();
        foreach (var type in model.Types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (types.ContainsKey(type.FullName))
                continue;
            var behaviour = new JsonObject();
            foreach (var (section, entries) in type.Behaviour.Where(pair => FactsQueries.BehaviourSections.Contains(pair.Key)))
                behaviour[section] = Values(entries.Select(entry => entry.Value).OrderBy(value => value, StringComparer.Ordinal).ToList());
            types[type.FullName] = new JsonObject
            {
                ["owner"] = type.OwnerLayerId == null ? null : architecture.Path(type.OwnerLayerId),
                ["wiring"] = statuses[type.Key].Status,
                ["behaviour"] = behaviour,
                ["members"] = Values(type.Members.Select(member => member.Display).OrderBy(value => value, StringComparer.Ordinal).ToList())
            };
        }
        var pairs = new JsonObject();
        foreach (var (pair, references) in FactsQueries.Pairs(model.References.Where(reference => reference.CountsAsEvidence))
                     .OrderBy(item => architecture.Path(item.Key.Source), StringComparer.Ordinal)
                     .ThenBy(item => architecture.Path(item.Key.Target), StringComparer.Ordinal))
        {
            var typePairs = new JsonObject();
            foreach (var group in references.GroupBy(reference => $"{reference.SourceType} -> {reference.TargetType}").OrderBy(group => group.Key, StringComparer.Ordinal))
                typePairs[group.Key] = group.Count();
            pairs[$"{architecture.Path(pair.Source)} -> {architecture.Path(pair.Target)}"] = new JsonObject
            {
                ["references"] = references.Count,
                ["type_pairs"] = typePairs
            };
        }
        return new JsonObject
        {
            ["format"] = Format,
            ["created"] = DateTime.UtcNow.ToString("O"),
            ["types"] = types,
            ["pairs"] = pairs
        };
    }

    private static JsonObject? Load(string root, string reference)
    {
        var candidates = new[]
        {
            reference,
            Path.Combine(Folder(root), reference),
            Path.Combine(Folder(root), reference + ".json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path == null)
            return null;
        var snapshot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        return snapshot?["format"]?.GetValue<int>() == Format ? snapshot : null;
    }

    private static JsonObject Missing(string root, string message)
    {
        var failure = FactsQueries.Failure("snapshot_not_found", message);
        failure["snapshots"] = FactsQueries.Array(Directory.Exists(Folder(root))
            ? Directory.GetFiles(Folder(root), "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(name => name, StringComparer.Ordinal).Select(name => (JsonNode?)name)
            : Enumerable.Empty<JsonNode?>());
        return failure;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToList() : new List<string>();

    private static List<string> Keys(JsonNode? node)
    {
        var keys = new List<string>();
        if (node is JsonObject map)
            foreach (var (key, count) in map)
                keys.AddRange(Enumerable.Repeat(key, count?.GetValue<int>() ?? 1));
        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    // Multiset difference: values present more often after are added, fewer often removed.
    private static (List<string> Added, List<string> Removed) Compare(List<string> before, List<string> after)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in before)
            counts[value] = counts.TryGetValue(value, out var count) ? count - 1 : -1;
        foreach (var value in after)
            counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
        var added = counts.Where(pair => pair.Value > 0).SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value)).OrderBy(value => value, StringComparer.Ordinal).ToList();
        var removed = counts.Where(pair => pair.Value < 0).SelectMany(pair => Enumerable.Repeat(pair.Key, -pair.Value)).OrderBy(value => value, StringComparer.Ordinal).ToList();
        return (added, removed);
    }

    private static JsonArray Values(IEnumerable<string> values) => FactsQueries.Array(values.Select(value => (JsonNode?)value));
}
