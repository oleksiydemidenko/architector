using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Architector.CodeFacts;

// Absence rules over the asset facts: what must be there and is not. A startedBy rule in Architector/code-facts.json:
//
//   "rules": { "startedBy": [ {
//       "name": "handlers are started",
//       "component": "Handler",                       // components deriving from (or implementing) this type ...
//       "in": "Starter",                             // ... in a prefab or scene that has a component deriving from this ...
//       "lists": [ "Starter._handlers",              // ... must be in each list: "<Lister>[/<ManagedClass>].<field>"
//                  "PhaseHost/StartList._handlers" ],
//       "filledAtRuntime": [ "Assets/Runtime/**" ] } ] }   // optional: files whose lists code fills; not checked
//
// A prefab variant counts as "in" when the prefab it derives from is. Checked per file: the file's own components and,
// one level deep, the components of the prefab instances it holds (a nested prefab, or the base of a variant) whose
// prefab is not "in" by itself (those are checked in their own file). A component is in a list when a list of the
// file holds it, when an override of the file adds it to a list of a source prefab's component (the managed class of a
// rid is read from the prefab that declares it), or, for an instance's component, when a list of its own prefab holds
// it. A component that derives from a list's lister type is exempt from that list. Disabled components and components
// on inactive objects (themselves, a parent, or an instance override) are skipped and reported as skipped.
public static class FactsRules
{
    public sealed record ListSpec(string Text, string Lister, string Managed, string Field)
    {
        public string Key => Managed.Length > 0 ? $"{Managed}.{Field}" : Field;
    }

    public sealed record Rule(string Name, string Component, string In, List<ListSpec> Lists, List<string> FilledAtRuntime)
    {
        public List<Regex> RuntimePatterns { get; } = FilledAtRuntime.Select(FactsExtractors.Glob).ToList();
    }

    public sealed record Finding(string Rule, string File, string Object, string Component, string FileId, List<string> Missing, string Via, string TypeKey);

    public sealed record Skip(string Rule, string File, string Object, string Component, string Reason, string TypeKey = "");

    public sealed class Result
    {
        public List<Rule> Rules { get; } = new();
        public List<string> Errors { get; } = new();
        public List<Finding> Findings { get; } = new();
        public List<Skip> Skipped { get; } = new();
        public int FilesChecked { get; set; }
        public int ComponentsChecked { get; set; }
        public HashSet<string> RuleComponentTypes { get; } = new(StringComparer.Ordinal);
    }

    public static bool HasRules(string root)
    {
        var (rules, errors) = Load(root);
        return rules.Count > 0 || errors.Count > 0;
    }

    public static (List<Rule> Rules, List<string> Errors) Load(string root)
    {
        var rules = new List<Rule>();
        var errors = new List<string>();
        var path = Path.Combine(root, "Architector", "code-facts.json");
        if (!File.Exists(path))
            return (rules, errors);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("rules", out var section) || section.ValueKind != JsonValueKind.Object
                || !section.TryGetProperty("startedBy", out var startedBy))
                return (rules, errors);
            if (startedBy.ValueKind != JsonValueKind.Array)
            {
                errors.Add("rules.startedBy must be an array of rules.");
                return (rules, errors);
            }
            var position = 0;
            foreach (var item in startedBy.EnumerateArray())
            {
                position++;
                string Text(string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
                List<string> Texts(string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString() ?? string.Empty).Where(entry => entry.Length > 0).ToList()
                    : new List<string>();
                var name = Text("name").Length > 0 ? Text("name") : $"startedBy #{position}";
                var lists = new List<ListSpec>();
                foreach (var text in Texts("lists"))
                {
                    var dot = text.LastIndexOf('.');
                    var owner = dot > 0 ? text[..dot] : string.Empty;
                    var slash = owner.IndexOf('/');
                    var spec = new ListSpec(text, slash >= 0 ? owner[..slash] : owner, slash >= 0 ? owner[(slash + 1)..] : string.Empty, dot > 0 ? text[(dot + 1)..] : string.Empty);
                    if (spec.Lister.Length == 0 || spec.Field.Length == 0)
                        errors.Add($"Rule '{name}': list '{text}' is not '<Lister>[/<ManagedClass>].<field>'.");
                    else
                        lists.Add(spec);
                }
                if (Text("component").Length == 0 || Text("in").Length == 0 || lists.Count == 0)
                {
                    errors.Add($"Rule '{name}' needs component, in and at least one list.");
                    continue;
                }
                rules.Add(new Rule(name, Text("component"), Text("in"), lists, Texts("filledAtRuntime")));
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            errors.Add("Architector/code-facts.json cannot be read: " + exception.Message);
        }
        return (rules, errors);
    }

    // The type hierarchy the rules need, from the code index: script GUID -> class, and "derives from or implements".
    public sealed class Types
    {
        private readonly Dictionary<string, IndexType> byFullName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<IndexType>> byName = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), bool> derives = new();
        public Dictionary<string, IndexType> ByScriptGuid { get; } = new(StringComparer.Ordinal);

        public Types(CodeIndex index, AssetWiring assets)
        {
            foreach (var type in index.Types)
            {
                byFullName.TryAdd(type.FullName, type);
                if (!byName.TryGetValue(type.Name, out var list))
                    byName[type.Name] = list = new List<IndexType>();
                list.Add(type);
            }
            foreach (var (file, type) in ScriptOwners(index.Types))
                if (assets.GuidByScript.TryGetValue(file, out var guid))
                    ByScriptGuid.TryAdd(guid, type);
        }

        public bool Known(string name) => byFullName.ContainsKey(name) || byName.ContainsKey(name) || byName.ContainsKey(Plain(name));

        public IndexType? OfGuid(string guid) => ByScriptGuid.GetValueOrDefault(guid);

        public bool Derives(IndexType? type, string name)
        {
            if (type == null)
                return false;
            if (derives.TryGetValue((type.Key, name), out var known))
                return known;
            var result = false;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current != null && visited.Add(current.Key); current = Find(current.Base))
                if (Matches(current.Name, current.FullName, name) || current.Interfaces.Any(contract => Matches(Plain(contract).Split('.').Last(), Plain(contract), name)))
                {
                    result = true;
                    break;
                }
            derives[(type.Key, name)] = result;
            return result;
        }

        private static bool Matches(string shortName, string fullName, string name) => shortName == name || fullName == name;

        private static string Plain(string name)
        {
            var generic = name.IndexOf('<');
            return generic >= 0 ? name[..generic] : name;
        }

        private IndexType? Find(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            var generic = name.IndexOf('<');
            var plain = Plain(name);
            if (generic >= 0)
            {
                var arity = name[generic..].Count(character => character == ',') + 1;
                if (byFullName.TryGetValue($"{plain}`{arity}", out var genericBase))
                    return genericBase;
            }
            if (byFullName.TryGetValue(plain, out var exact))
                return exact;
            var shortName = plain.Split('.').Last();
            return byName.TryGetValue(shortName, out var candidates) && candidates.Count == 1 ? candidates[0] : null;
        }

        // Unity names a MonoBehaviour by its file: in each C# file the class named after the file owns the file's GUID;
        // when none is, a class whose name differs only in letter case; when that fails too, the only placeable class.
        private static IEnumerable<(string File, IndexType Type)> ScriptOwners(IEnumerable<IndexType> types)
        {
            var byFile = new Dictionary<string, List<IndexType>>(StringComparer.Ordinal);
            foreach (var type in types)
                foreach (var part in type.Parts)
                {
                    var separator = part.LastIndexOf(':');
                    var file = separator > 0 ? part[..separator] : part;
                    if (!byFile.TryGetValue(file, out var list))
                        byFile[file] = list = new List<IndexType>();
                    if (!list.Contains(type))
                        list.Add(type);
                }
            foreach (var (file, candidates) in byFile)
            {
                var classes = candidates.Where(type => type is { Kind: "class", IsAbstract: false, IsGeneric: false }).ToArray();
                var stem = Path.GetFileNameWithoutExtension(file);
                var exact = classes.Where(type => string.Equals(type.Name, stem, StringComparison.Ordinal)).ToArray();
                var caseless = classes.Where(type => string.Equals(type.Name, stem, StringComparison.OrdinalIgnoreCase)).ToArray();
                var placeable = classes.Where(type => type.Placeable).ToArray();
                var owner = exact.Length == 1 ? exact[0]
                    : exact.Length == 0 && caseless.Length == 1 ? caseless[0]
                    : exact.Length == 0 && caseless.Length == 0 && placeable.Length == 1 ? placeable[0]
                    : null;
                if (owner != null)
                    yield return (file, owner);
            }
        }
    }

    // One component as the rule sees it in a file: its own, or one of a prefab instance's source components.
    private sealed record Candidate(AssetWiring.Component Component, string Object, List<AssetWiring.ListMembership> Lists, bool Active, bool Enabled, string Via,
        AssetWiring.FileEntry? Source);

    public static Result Run(CodeIndex index, AssetWiring assets, string root, IReadOnlyCollection<string>? only = null)
    {
        var result = new Result();
        var (rules, errors) = Load(root);
        result.Rules.AddRange(rules);
        result.Errors.AddRange(errors);
        if (rules.Count == 0)
            return result;
        var types = new Types(index, assets);
        foreach (var rule in rules)
        {
            foreach (var name in new[] { rule.Component, rule.In }.Concat(rule.Lists.Select(list => list.Lister)).Distinct())
                if (!types.Known(name))
                    result.Errors.Add($"Rule '{rule.Name}': no type named '{name}' in the code.");
            foreach (var type in index.Types.Where(type => types.Derives(type, rule.Component)))
                result.RuleComponentTypes.Add(type.Key);
        }
        var inCache = new Dictionary<(AssetWiring.FileEntry, Rule), bool>();
        bool IsIn(AssetWiring.FileEntry entry, Rule rule)
        {
            if (inCache.TryGetValue((entry, rule), out var known))
                return known;
            var found = false;
            var visited = new HashSet<AssetWiring.FileEntry>(ReferenceEqualityComparer.Instance);
            for (var current = entry; current != null && !found && visited.Add(current); current = current.Variant.Length > 0 ? assets.FileOf(current.Variant) : null)
                found = current.Components.Any(component => !component.Stripped && types.Derives(types.OfGuid(component.Guid), rule.In));
            inCache[(entry, rule)] = found;
            return found;
        }
        foreach (var (path, entry) in assets.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                continue;
            if (only != null && !only.Contains(path))
                continue;
            List<Candidate>? candidates = null;
            foreach (var rule in rules)
            {
                if (!IsIn(entry, rule))
                    continue;
                if (rule.RuntimePatterns.Any(pattern => pattern.IsMatch(path)))
                {
                    result.Skipped.Add(new Skip(rule.Name, path, string.Empty, string.Empty, "filledAtRuntime: the rule says code fills these lists"));
                    continue;
                }
                result.FilesChecked++;
                candidates ??= Candidates(entry, assets);
                foreach (var candidate in candidates)
                {
                    var type = types.OfGuid(candidate.Component.Guid);
                    if (!types.Derives(type, rule.Component))
                        continue;
                    // An instance of a prefab that is "in" by itself is checked in that prefab's file, with its own lists.
                    if (candidate.Source != null && IsIn(candidate.Source, rule))
                        continue;
                    result.ComponentsChecked++;
                    if (!candidate.Active || !candidate.Enabled)
                    {
                        result.Skipped.Add(new Skip(rule.Name, path, candidate.Object, type!.Name, candidate.Active ? "disabled component" : "inactive object", type.Key));
                        continue;
                    }
                    var missing = rule.Lists
                        .Where(list => !types.Derives(type, list.Lister))
                        .Where(list => !candidate.Lists.Any(membership => membership.Field == list.Key && types.Derives(types.OfGuid(membership.ListerGuid), list.Lister)))
                        .Select(list => list.Text)
                        .ToList();
                    if (missing.Count > 0)
                        result.Findings.Add(new Finding(rule.Name, path, candidate.Object, type!.Name, candidate.Component.FileId, missing, candidate.Via, type.Key));
                }
            }
        }
        return result;
    }

    private static List<Candidate> Candidates(AssetWiring.FileEntry entry, AssetWiring assets)
    {
        var candidates = new List<Candidate>();
        foreach (var component in entry.Components.Where(component => !component.Stripped))
            candidates.Add(new Candidate(component, component.Object, assets.ListsOf(entry, component), assets.ComponentActive(entry, component), component.Enabled, string.Empty, null));
        foreach (var instance in entry.Instances)
        {
            var source = assets.FileOf(instance.Source);
            if (source == null)
                continue;
            var sourcePath = assets.FileByGuid[instance.Source];
            foreach (var component in source.Components.Where(component => !component.Stripped))
            {
                var lists = assets.ListsOf(source, component);
                var key = $"{instance.Source}:{component.FileId}";
                foreach (var standIn in entry.Components.Where(item => item.Stripped && item.Instance == instance.FileId && item.Source == key))
                    foreach (var membership in assets.ListsOf(entry, standIn))
                        if (!lists.Contains(membership))
                            lists.Add(membership);
                var via = (instance.Parent == "0" ? "variant of " : "nested ") + sourcePath;
                candidates.Add(new Candidate(component, component.Object, lists,
                    assets.ComponentActive(source, component) && assets.InstanceShows(entry, instance, component.FileId),
                    component.Enabled && !instance.Disabled.Contains(component.FileId),
                    via, source));
            }
        }
        return candidates;
    }

    // check: rule findings for changed files only. Files are project paths (or absolute); a C# file stands for the prefabs
    // and scenes that place its script, a prefab for itself, its variants and the files that nest it.
    public static JsonObject Check(CodeIndex? index, string indexState, IEnumerable<string> changed, string root, bool whole = false)
    {
        var watch = Stopwatch.StartNew();
        var reply = new JsonObject { ["success"] = true };
        var (rules, errors) = Load(root);
        if (rules.Count == 0 && errors.Count == 0)
        {
            reply["status"] = "no_rules";
            reply["message"] = "Architector/code-facts.json has no rules.startedBy; nothing to check.";
            return reply;
        }
        if (index == null)
        {
            reply["status"] = "index_building";
            reply["message"] = "The code index is being built (first run on this clone); rules are checked once it is ready.";
            return reply;
        }
        var assets = AssetWiring.Load(root);
        var files = whole ? null : Affected(assets, changed, root);
        var result = Run(index, assets, root, files);
        reply["status"] = result.Findings.Count > 0 ? "findings" : "clean";
        reply["index"] = indexState;
        reply["files_checked"] = result.FilesChecked;
        reply["findings_total"] = result.Findings.Count;
        reply["findings"] = FindingsJson(result.Findings);
        if (result.Skipped.Count > 0)
            reply["skipped"] = result.Skipped.Count;
        if (result.Errors.Count > 0)
            reply["rule_errors"] = new JsonArray(result.Errors.Select(error => (JsonNode?)error).ToArray());
        reply["ms"] = watch.ElapsedMilliseconds;
        return reply;
    }

    public static HashSet<string> Affected(AssetWiring assets, IEnumerable<string> changed, string root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Guid, int Depth)>();
        foreach (var raw in changed)
        {
            var path = raw.Trim().Trim('"').Replace('\\', '/');
            if (path.Length == 0)
                continue;
            if (Path.IsPathRooted(path))
                path = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                path = path[..^".meta".Length];
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (!assets.GuidByScript.TryGetValue(path, out var script))
                    continue;
                foreach (var (file, entry) in assets.Files)
                    if (entry.Components.Any(component => component.Guid == script))
                        result.Add(file);
                continue;
            }
            if (!assets.Files.TryGetValue(path, out var changedEntry))
                continue;
            result.Add(path);
            if (changedEntry.Guid.Length > 0)
                queue.Enqueue((changedEntry.Guid, 0));
        }
        // Variants and files that nest a changed prefab see its changes too.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var (guid, depth) = queue.Dequeue();
            if (!seen.Add(guid) || depth > 4)
                continue;
            foreach (var (file, entry) in assets.Files)
                if (entry.Sources.Contains(guid) && result.Add(file) && entry.Guid.Length > 0)
                    queue.Enqueue((entry.Guid, depth + 1));
        }
        return result;
    }

    public static JsonArray FindingsJson(IEnumerable<Finding> findings) => new(findings.Select(finding =>
    {
        var item = new JsonObject
        {
            ["rule"] = finding.Rule,
            ["file"] = finding.File,
            ["object"] = finding.Object,
            ["component"] = finding.Component,
            ["missing"] = new JsonArray(finding.Missing.Select(list => (JsonNode?)list).ToArray())
        };
        if (finding.Via.Length > 0)
            item["via"] = finding.Via;
        return (JsonNode?)item;
    }).ToArray());

    // One line per finding, for hooks and terminals.
    public static string FindingLine(JsonNode finding)
    {
        var via = (string?)finding["via"];
        var missing = (finding["missing"] as JsonArray ?? new JsonArray()).Select(list => (string?)list);
        return $"{(string?)finding["file"]}: {(string?)finding["component"]} on '{(string?)finding["object"]}'{(via != null ? $" ({via})" : string.Empty)} "
               + $"is missing from {string.Join(", ", missing)} (rule '{(string?)finding["rule"]}')";
    }

    // The rules section of facts audit: every finding, and what was checked.
    public static JsonObject Summary(Result result)
    {
        var summary = new JsonObject
        {
            ["rules"] = new JsonArray(result.Rules.Select(rule => (JsonNode?)rule.Name).ToArray()),
            ["files_checked"] = result.FilesChecked,
            ["components_checked"] = result.ComponentsChecked,
            ["findings"] = result.Findings.Count,
            ["skipped"] = result.Skipped.Count
        };
        if (result.Skipped.Count > 0)
            summary["skipped_reasons"] = new JsonObject(result.Skipped.GroupBy(skip => skip.Reason).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new KeyValuePair<string, JsonNode?>(group.Key, group.Count())));
        if (result.Errors.Count > 0)
            summary["errors"] = new JsonArray(result.Errors.Select(error => (JsonNode?)error).ToArray());
        return summary;
    }
}
