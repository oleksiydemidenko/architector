using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Architector.CodeFacts;

// The Architector files as the code facts need them: layer paths, code scopes, nodes with their linked types,
// relationships inside a layer and between layers. Read straight from disk, so no Unity Editor is needed.
public sealed class FactsArchitecture
{
    private readonly Dictionary<string, (string Name, string Parent)> groups = new(StringComparer.Ordinal);

    public Dictionary<string, FactsLayer> Layers { get; } = new(StringComparer.Ordinal);
    public List<FactsRelationship> CrossRelationships { get; } = new();
    public HashSet<(string Source, string Target)> AllowedDependencies { get; } = new();

    public static FactsArchitecture Load(string dataRoot)
    {
        var architecture = new FactsArchitecture();
        foreach (var document in Documents(dataRoot, "Groups", ".group.json"))
            architecture.groups[Text(document, "id")] = (Text(document, "name"), Text(document, "parentGroupId"));
        foreach (var document in Documents(dataRoot, "Layers", ".layer.json"))
        {
            var layer = new FactsLayer
            {
                Id = Text(document, "layerId"),
                Name = Text(document, "name"),
                Purpose = Text(document, "purpose"),
                ParentGroupId = Text(document, "parentGroupId")
            };
            if (document.TryGetProperty("scope", out var scope))
            {
                layer.Folders = Strings(scope, "folders").Select(value => value.Replace('\\', '/').TrimEnd('/')).ToList();
                layer.Namespaces = Strings(scope, "namespaces").ToList();
            }
            if (document.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var node in nodes.EnumerateArray())
                    layer.Nodes.Add(FactsNode.Read(node));
            if (document.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
                foreach (var edge in edges.EnumerateArray())
                    layer.Edges.Add(FactsRelationship.Read(edge));
            architecture.Layers[layer.Id] = layer;
        }
        foreach (var document in Documents(dataRoot, "Links", ".link.json"))
        {
            if (!document.TryGetProperty("relationships", out var relationships) || relationships.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in relationships.EnumerateArray())
            {
                var relationship = FactsRelationship.Read(item);
                architecture.CrossRelationships.Add(relationship);
                if (relationship.Kind == "dependency")
                    architecture.AllowedDependencies.Add((relationship.SourceLayerId, relationship.TargetLayerId));
            }
        }
        foreach (var layer in architecture.Layers.Values)
            layer.Path = architecture.BuildPath(layer);
        var settings = System.IO.Path.Combine(dataRoot, "architector.settings.json");
        if (File.Exists(settings))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settings));
            if (document.RootElement.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Object
                && rules.TryGetProperty("standaloneNodes", out var standalone) && standalone.ValueKind == JsonValueKind.Array)
                foreach (var item in standalone.EnumerateArray())
                    architecture.StandaloneNodes.Add(Text(item, "nodeId"));
        }
        return architecture;
    }

    // Nodes marked as having no relationship on purpose (shared settings, rules.standaloneNodes).
    public HashSet<string> StandaloneNodes { get; } = new(StringComparer.Ordinal);

    public string Path(string layerId) => Layers.TryGetValue(layerId, out var layer) ? layer.Path : layerId;

    public FactsLayer? FindLayer(string? reference)
    {
        var matches = MatchLayers(reference);
        return matches.Length == 1 ? matches[0] : null;
    }

    // A layer id, its full Explorer path, or any trailing part of the path ("Group/Layer" for "Root/Group/Layer"; a
    // layer name is the shortest such part). The Unity gateway applies the same rule (ArchitectorEntryPaths.MatchLayers).
    public FactsLayer[] MatchLayers(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return System.Array.Empty<FactsLayer>();
        var value = string.Join("/", reference.Trim().Trim('/').Split('/').Select(segment => segment.Trim()));
        if (Layers.TryGetValue(value, out var byId))
            return new[] { byId };
        var exact = Layers.Values.Where(layer => string.Equals(layer.Path, value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0)
            return exact;
        return Layers.Values
            .Where(layer => layer.Path.EndsWith("/" + value, StringComparison.OrdinalIgnoreCase))
            .OrderBy(layer => layer.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public FactsNode? FindNode(string nodeId)
    {
        foreach (var layer in Layers.Values)
            foreach (var node in layer.Nodes)
                if (node.Id == nodeId)
                    return node;
        return null;
    }

    public FactsLayer? LayerOfNode(string nodeId) => Layers.Values.FirstOrDefault(layer => layer.Nodes.Any(node => node.Id == nodeId));

    private string BuildPath(FactsLayer layer)
    {
        var segments = new List<string> { layer.Name };
        var parent = layer.ParentGroupId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(parent) && visited.Add(parent) && groups.TryGetValue(parent, out var group))
        {
            segments.Add(group.Name);
            parent = group.Parent;
        }
        segments.Reverse();
        return string.Join("/", segments);
    }

    private static IEnumerable<JsonElement> Documents(string dataRoot, string directory, string suffix)
    {
        var folder = System.IO.Path.Combine(dataRoot, directory);
        if (!Directory.Exists(folder))
            yield break;
        foreach (var file in Directory.GetFiles(folder, "*" + suffix).OrderBy(file => file, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            yield return document.RootElement.Clone();
        }
    }

    internal static string Text(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    internal static IEnumerable<string> Strings(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? string.Empty)
            : Array.Empty<string>();
    }
}

public sealed class FactsLayer
{
    public string Id = string.Empty;
    public string Name = string.Empty;
    public string Path = string.Empty;
    public string Purpose = string.Empty;
    public string ParentGroupId = string.Empty;
    public List<string> Folders = new();
    public List<string> Namespaces = new();
    public List<FactsNode> Nodes { get; } = new();
    public List<FactsRelationship> Edges { get; } = new();
    public bool Scoped => Folders.Count + Namespaces.Count > 0;
}

public sealed class FactsNode
{
    public string Id = string.Empty;
    public string Title = string.Empty;
    public string Kind = string.Empty;
    public string Responsibility = string.Empty;
    public string TypeMode = string.Empty;
    public string TypeName = string.Empty;
    public List<FactsMember> Members { get; } = new();

    public bool IsPortal => Kind == "layerPortal";

    public static FactsNode Read(JsonElement element)
    {
        var node = new FactsNode
        {
            Id = FactsArchitecture.Text(element, "id"),
            Title = FactsArchitecture.Text(element, "title"),
            Kind = FactsArchitecture.Text(element, "kind"),
            Responsibility = FactsArchitecture.Text(element, "responsibility")
        };
        if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Object)
        {
            node.TypeMode = FactsArchitecture.Text(type, "mode");
            var name = FactsArchitecture.Text(type, "metadataName");
            if (name.Length == 0)
                name = FactsArchitecture.Text(type, "name");
            // Unity stores metadataName as the full name (Ns.Type); a short metadataName gets the namespace in front.
            var space = FactsArchitecture.Text(type, "typeNamespace");
            node.TypeName = name.Length == 0 ? string.Empty
                : space.Length == 0 || name.StartsWith(space + ".", StringComparison.Ordinal) ? name
                : space + "." + name;
        }
        if (element.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
            foreach (var member in members.EnumerateArray())
                node.Members.Add(new FactsMember(
                    FactsArchitecture.Text(member, "id"),
                    FactsArchitecture.Text(member, "kind"),
                    FactsArchitecture.Text(member, "declaration"),
                    FactsArchitecture.Text(member, "symbolKey")));
        return node;
    }
}

public sealed record FactsMember(string Id, string Kind, string Declaration, string SymbolKey);

// Architector/code-facts.json, optional:
// { "presets": ["unity", "vcontainer"], "genericCalls": ["Get"], "injectionAttributes": ["Inject"], "serializedAttributes": [] }
// genericCalls: generic method names whose type arguments are reported per type (Get<Container, Parameter>).
// injectionAttributes: attributes whose constructor or method parameters, fields or properties are reported as injected.
// serializedAttributes: attributes whose field or property types are reported as wired in the Inspector.
// presets add common conventions; without the file, or without "presets", the "unity" preset is on. "presets": [] turns it off.
// "suggestions": { "exclude": ["Assets/Plugins/**"] } leaves uses in those folders out of the rule suggestions.
// A genericCalls entry may be an object, { "name": "Get", "access": "read" | "write" }, for the behaviour facts; without
// it the method name decides (Get, Read, Has, Try, Is, Find read; Set, Write, Add, Remove, Change, Apply, Clear write).
// A non-generic overload of a genericCalls method (Add(instance) next to Add<T>()) is listed the same way.
// "memberCalls": [{ "name": "Set", "on": "ParameterBase", "access": "write" }] lists non-generic calls on receivers of
// that type or its subclasses. "recurringCalls": ["Schedule"] names calls that register per-frame work.
// "wiring": { "testPlaces": ["**/Sandbox/**", "**/* OLD.prefab"] } marks test, sample and old-copy assets (globs over
// full paths; "testFolders" is the older name of the same list).
// These rules only add facts per type; they never change the dependency evidence.
public sealed class FactsExtractors
{
    public static readonly IReadOnlyDictionary<string, (string[] Calls, string[] Injection, string[] Serialized)> Presets =
        new Dictionary<string, (string[], string[], string[])>(StringComparer.Ordinal)
        {
            ["unity"] = (
                new[]
                {
                    "GetComponent", "GetComponents", "GetComponentInChildren", "GetComponentsInChildren", "GetComponentInParent",
                    "GetComponentsInParent", "TryGetComponent", "AddComponent", "FindAnyObjectByType", "FindFirstObjectByType",
                    "FindObjectsByType", "FindObjectOfType", "FindObjectsOfType", "CreateInstance", "Load", "LoadAll"
                },
                System.Array.Empty<string>(),
                new[] { "SerializeField", "SerializeReference" }),
            ["zenject"] = (
                new[] { "Bind", "BindInterfacesTo", "BindInterfacesAndSelfTo", "BindFactory", "Resolve", "TryResolve", "ResolveAll", "InstantiateComponent" },
                new[] { "Inject", "InjectOptional", "InjectLocal" },
                System.Array.Empty<string>()),
            ["vcontainer"] = (
                new[] { "Register", "RegisterComponent", "RegisterComponentInHierarchy", "RegisterInstance", "RegisterEntryPoint", "Resolve" },
                new[] { "Inject" },
                System.Array.Empty<string>()),
            ["addressables"] = (
                new[] { "LoadAssetAsync", "LoadAssetsAsync", "InstantiateAsync" },
                System.Array.Empty<string>(),
                System.Array.Empty<string>())
        };

    public HashSet<string> GenericCalls { get; } = new(StringComparer.Ordinal);
    public HashSet<string> InjectionAttributes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> SerializedAttributes { get; } = new(StringComparer.Ordinal);
    public List<string> ActivePresets { get; } = new();
    public List<string> Problems { get; } = new();
    public List<string> SuggestionExclude { get; } = new();
    public Dictionary<string, string> CallAccess { get; } = new(StringComparer.Ordinal);
    // "memberCalls": [{ "name": "Set", "on": "ParameterBase", "access": "write" }]: non-generic calls on a receiver whose
    // type is or derives from "on" (by type name), listed in the behaviour facts only.
    public Dictionary<string, List<(string? On, string Access)>> MemberCalls { get; } = new(StringComparer.Ordinal);
    // "recurringCalls": ["Schedule"]: calls that register per-frame work (tween schedulers, player-loop hooks).
    public HashSet<string> RecurringCalls { get; } = new(StringComparer.Ordinal);
    // "wiring": { "testFolders": [...] }: assets under these folders count as test or sample uses.
    public static readonly string[] DefaultTestFolders = { "**/Tests/**", "**/Test/**", "**/Samples/**", "**/Examples/**" };
    public List<string> TestFolders { get; } = new(DefaultTestFolders);
    private List<Regex> testFolderPatterns = DefaultTestFolders.Select(Glob).ToList();

    public bool InTestFolder(string file) => testFolderPatterns.Any(pattern => pattern.IsMatch(file));

    private static readonly string[] ReadPrefixes = { "Get", "Read", "Has", "Try", "Is", "Find", "Load", "Resolve", "Fetch" };
    private static readonly string[] WritePrefixes = { "Set", "Write", "Add", "Remove", "Change", "Apply", "Clear", "Register", "Bind", "Create", "Instantiate" };

    // Whether a generic call reads or writes, for the behaviour facts: the project's own setting first, then the name.
    public string Access(string callName)
    {
        if (CallAccess.TryGetValue(callName, out var access))
            return access;
        if (ReadPrefixes.Any(prefix => callName.StartsWith(prefix, StringComparison.Ordinal)))
            return "read";
        return WritePrefixes.Any(prefix => callName.StartsWith(prefix, StringComparison.Ordinal)) ? "write" : "use";
    }
    private readonly List<Regex> suggestionExcludePatterns = new();

    // A place is "path:line"; it is excluded when its path matches one of suggestions.exclude.
    public bool ExcludedFromSuggestions(string place)
    {
        if (suggestionExcludePatterns.Count == 0)
            return false;
        var separator = place.LastIndexOf(':');
        var file = separator > 0 ? place[..separator] : place;
        return suggestionExcludePatterns.Any(pattern => pattern.IsMatch(file));
    }

    // Git-style globs: ** crosses folders, * and ? stay inside one.
    public static Regex Glob(string glob)
    {
        var pattern = new StringBuilder("^");
        var value = glob.Trim().Replace('\\', '/');
        for (var index = 0; index < value.Length; index++)
        {
            var symbol = value[index];
            if (symbol == '*' && index + 1 < value.Length && value[index + 1] == '*')
            {
                index++;
                if (index + 1 < value.Length && value[index + 1] == '/')
                {
                    index++;
                    pattern.Append("(?:.*/)?");
                }
                else
                    pattern.Append(".*");
            }
            else if (symbol == '*')
                pattern.Append("[^/]*");
            else if (symbol == '?')
                pattern.Append("[^/]");
            else
                pattern.Append(Regex.Escape(symbol.ToString()));
        }
        return new Regex(pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    public bool Any => GenericCalls.Count + InjectionAttributes.Count + SerializedAttributes.Count > 0;

    public static FactsExtractors Load(string path)
    {
        var extractors = new FactsExtractors();
        var presets = new[] { "unity" }.AsEnumerable();
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("presets", out _))
                presets = FactsArchitecture.Strings(root, "presets").ToArray();
            extractors.GenericCalls.UnionWith(FactsArchitecture.Strings(root, "genericCalls"));
            if (root.TryGetProperty("genericCalls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                foreach (var call in calls.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
                {
                    var name = FactsArchitecture.Text(call, "name");
                    if (name.Length == 0)
                        continue;
                    extractors.GenericCalls.Add(name);
                    var access = FactsArchitecture.Text(call, "access");
                    if (access is "read" or "write")
                        extractors.CallAccess[name] = access;
                    else if (access.Length > 0)
                        extractors.Problems.Add($"genericCalls entry '{name}' has access '{access}'; use read or write.");
                }
            if (root.TryGetProperty("memberCalls", out var memberCalls) && memberCalls.ValueKind == JsonValueKind.Array)
                foreach (var call in memberCalls.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
                {
                    var (name, on, access) = (FactsArchitecture.Text(call, "name"), FactsArchitecture.Text(call, "on"), FactsArchitecture.Text(call, "access"));
                    if (name.Length == 0 || on.Length == 0)
                    {
                        extractors.Problems.Add("A memberCalls entry needs name and on (the receiver's type or base type name).");
                        continue;
                    }
                    if (access.Length > 0 && access is not ("read" or "write" or "use"))
                        extractors.Problems.Add($"memberCalls entry '{name}' has access '{access}'; use read, write or use.");
                    if (!extractors.MemberCalls.TryGetValue(name, out var rules))
                        extractors.MemberCalls[name] = rules = new List<(string? On, string Access)>();
                    rules.Add((on, access.Length == 0 ? extractors.Access(name) : access));
                }
            extractors.RecurringCalls.UnionWith(FactsArchitecture.Strings(root, "recurringCalls"));
            extractors.InjectionAttributes.UnionWith(FactsArchitecture.Strings(root, "injectionAttributes").Select(AttributeName));
            extractors.SerializedAttributes.UnionWith(FactsArchitecture.Strings(root, "serializedAttributes").Select(AttributeName));
            // testPlaces is the clearer name of testFolders; both take globs over full paths, so file patterns such as
            // "**/* OLD.prefab" work as well as folders. Together they replace the defaults.
            if (root.TryGetProperty("wiring", out var wiring) && wiring.ValueKind == JsonValueKind.Object
                && (wiring.TryGetProperty("testFolders", out _) || wiring.TryGetProperty("testPlaces", out _)))
            {
                extractors.TestFolders.Clear();
                extractors.TestFolders.AddRange(FactsArchitecture.Strings(wiring, "testFolders").Concat(FactsArchitecture.Strings(wiring, "testPlaces")).Distinct(StringComparer.Ordinal));
                extractors.testFolderPatterns = extractors.TestFolders.Select(Glob).ToList();
            }
            if (root.TryGetProperty("suggestions", out var suggestions) && suggestions.ValueKind == JsonValueKind.Object)
                extractors.SuggestionExclude.AddRange(FactsArchitecture.Strings(suggestions, "exclude"));
            extractors.suggestionExcludePatterns.AddRange(extractors.SuggestionExclude.Select(Glob));
        }
        foreach (var preset in presets)
        {
            if (!Presets.TryGetValue(preset, out var rules))
            {
                extractors.Problems.Add($"Unknown preset '{preset}' in code-facts.json. Known: {string.Join(", ", Presets.Keys)}.");
                continue;
            }
            extractors.ActivePresets.Add(preset);
            extractors.GenericCalls.UnionWith(rules.Calls);
            extractors.InjectionAttributes.UnionWith(rules.Injection);
            extractors.SerializedAttributes.UnionWith(rules.Serialized);
        }
        return extractors;
    }

    public static string AttributeName(string name) =>
        name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
}

public sealed class FactsRelationship
{
    public string Id = string.Empty;
    public string SourceLayerId = string.Empty;
    public string SourceNodeId = string.Empty;
    public string TargetLayerId = string.Empty;
    public string TargetNodeId = string.Empty;
    public string Kind = string.Empty;
    public string Label = string.Empty;

    public static FactsRelationship Read(JsonElement element)
    {
        return new FactsRelationship
        {
            Id = FactsArchitecture.Text(element, "id"),
            SourceLayerId = FactsArchitecture.Text(element, "sourceLayerId"),
            SourceNodeId = FactsArchitecture.Text(element, "sourceNodeId"),
            TargetLayerId = FactsArchitecture.Text(element, "targetLayerId"),
            TargetNodeId = FactsArchitecture.Text(element, "targetNodeId"),
            Kind = FactsArchitecture.Text(element, "kind"),
            Label = FactsArchitecture.Text(element, "label")
        };
    }
}
