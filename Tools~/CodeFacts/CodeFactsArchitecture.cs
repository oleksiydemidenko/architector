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
        return architecture;
    }

    public string Path(string layerId) => Layers.TryGetValue(layerId, out var layer) ? layer.Path : layerId;

    public FactsLayer? FindLayer(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        var value = reference.Trim().Trim('/');
        if (Layers.TryGetValue(value, out var byId))
            return byId;
        var byPath = Layers.Values.Where(layer => string.Equals(layer.Path, value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byPath.Length == 1)
            return byPath[0];
        var byName = Layers.Values.Where(layer => string.Equals(layer.Name, value, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byName.Length == 1 ? byName[0] : null;
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
            var space = FactsArchitecture.Text(type, "typeNamespace");
            node.TypeName = name.Length == 0 ? string.Empty : space.Length == 0 ? name : space + "." + name;
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
            extractors.InjectionAttributes.UnionWith(FactsArchitecture.Strings(root, "injectionAttributes").Select(AttributeName));
            extractors.SerializedAttributes.UnionWith(FactsArchitecture.Strings(root, "serializedAttributes").Select(AttributeName));
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
