using Microsoft.CodeAnalysis;

namespace Architector.CodeFacts;

// The code index seen through the current graph: owners from layer scopes, which references count as dependency
// evidence (exactly DependencyEvidence's rule), its scope warnings, and the nodes linked to each type. Applying the
// graph takes a fraction of a second, so graph edits never repeat the Roslyn analysis.
public sealed class FactsModel
{
    public FactsArchitecture Architecture { get; private set; } = new();
    public IReadOnlyList<FactsLayer> ScopedLayers { get; private set; } = Array.Empty<FactsLayer>();
    public List<FactsReference> References { get; } = new();
    public List<FactsType> Types { get; } = new();
    public Dictionary<string, FactsType> TypesByName { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> EvidenceWarnings { get; private set; } = Array.Empty<string>();
    public FactsExtractors Extractors { get; private set; } = new();
    public IReadOnlyList<FactsRuleSuggestion> RuleSuggestions { get; private set; } = Array.Empty<FactsRuleSuggestion>();
    public CodeIndex? Index { get; private set; }

    // Analyses the code (or reuses the saved index) and applies the graph. referenceSet names the metadata references,
    // because the binding they give is part of the index: "tpa" for the SDK runtime, "net100" for the embedded set.
    public static FactsModel Build(string root, string referenceSet, Func<IEnumerable<MetadataReference>> references)
    {
        var architecture = FactsArchitecture.Load(Path.Combine(root, "Architector"));
        if (!architecture.Layers.Values.Any(layer => layer.Scoped))
            return Apply(root, architecture, null);
        return Apply(root, architecture, CodeIndex.LoadOrBuild(root, referenceSet, references));
    }

    // Graph questions (layer_dump, lookup) need no code analysis.
    public static FactsModel ArchitectureOnly(string root) =>
        Apply(root, FactsArchitecture.Load(Path.Combine(root, "Architector")), null);

    public static FactsModel Apply(string root, FactsArchitecture architecture, CodeIndex? index)
    {
        var model = new FactsModel
        {
            Architecture = architecture,
            Extractors = FactsExtractors.Load(Path.Combine(root, "Architector", "code-facts.json")),
            Index = index
        };
        model.ScopedLayers = architecture.Layers.Values.Where(layer => layer.Scoped).ToArray();
        if (index == null || model.ScopedLayers.Count == 0)
            return model;

        var types = index.Types.ToDictionary(type => type.Key, StringComparer.Ordinal);
        var namespacesByFile = index.Files.ToDictionary(file => file.Path, file => file.Namespaces, StringComparer.Ordinal);
        var owners = new OwnerResolver(model.ScopedLayers, types);
        var evidenceOwners = new OwnerResolver(model.ScopedLayers, types);
        var mayOwn = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool MayOwn(string file)
        {
            if (!mayOwn.TryGetValue(file, out var value))
                mayOwn[file] = value = owners.MayOwn(file, namespacesByFile.TryGetValue(file, out var spaces) ? spaces : new List<string>());
            return value;
        }

        // DependencyEvidence resolves the owner of every type with a name in a file a layer may own, and the owner of
        // every target referenced from an owned type there; the same calls give the same scope warnings.
        foreach (var type in index.Types)
            foreach (var file in type.NamedIn)
                if (MayOwn(file))
                {
                    evidenceOwners.Owner(type.Key);
                    break;
                }
        foreach (var reference in index.References)
        {
            var source = owners.Owner(reference.SourceKey);
            var target = owners.Owner(reference.TargetKey);
            var counts = MayOwn(reference.File) && source != null;
            if (counts)
                evidenceOwners.Owner(reference.TargetKey);
            model.References.Add(new FactsReference(
                reference.SourceName,
                reference.TargetName,
                types.TryGetValue(reference.SourceKey, out var sourceType) ? sourceType.FullName : reference.SourceKey,
                types.TryGetValue(reference.TargetKey, out var targetType) ? targetType.FullName : reference.TargetKey,
                source?.Id,
                target?.Id,
                reference.File,
                reference.Line,
                counts && target != null && !ReferenceEquals(source, target)));
        }
        model.EvidenceWarnings = evidenceOwners.Ambiguities.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        foreach (var data in index.Types)
        {
            var type = new FactsType(data, owners.Owner(data.Key)?.Id);
            model.Types.Add(type);
            model.TypesByName.TryAdd(data.FullName, type);
        }
        var byKey = model.Types.ToDictionary(type => type.Data.Key, StringComparer.Ordinal);
        foreach (var type in model.Types)
            foreach (var derived in type.Data.DerivedBy)
                if (byKey.TryGetValue(derived, out var derivedType))
                    type.DerivedOwnerLayers.Add(derivedType.OwnerLayerId ?? string.Empty);
        foreach (var node in architecture.Layers.Values.SelectMany(layer => layer.Nodes))
            if (node.TypeName.Length > 0 && model.TypesByName.TryGetValue(node.TypeName, out var linked))
                linked.Nodes.Add(node.Id);
        model.RuleSuggestions = index.Suggestions;
        return model;
    }

    public FactsType? FindType(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (TypesByName.TryGetValue(name.Trim(), out var exact))
            return exact;
        var byShort = Types.Where(type => string.Equals(type.Name, name.Trim(), StringComparison.Ordinal)).ToArray();
        return byShort.Length == 1 ? byShort[0] : null;
    }

    public static IEnumerable<MetadataReference> PlatformReferences()
    {
        return ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    public static string[] SourceFilePaths(string root)
    {
        var assets = Path.Combine(root, "Assets");
        if (!Directory.Exists(assets))
            return Array.Empty<string>();
        return Directory.GetFiles(assets, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(assets, file).Replace('\\', '/').Split('/')
                .Any(part => part.EndsWith('~') || part.StartsWith('.')))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class OwnerResolver
    {
        private readonly IReadOnlyList<FactsLayer> layers;
        private readonly IReadOnlyDictionary<string, IndexType> types;
        private readonly Dictionary<string, FactsLayer?> owners = new(StringComparer.Ordinal);

        public OwnerResolver(IReadOnlyList<FactsLayer> layers, IReadOnlyDictionary<string, IndexType> types)
        {
            this.layers = layers;
            this.types = types;
        }

        public HashSet<string> Ambiguities { get; } = new(StringComparer.Ordinal);

        public bool MayOwn(string file, IReadOnlyList<string> namespaces)
        {
            if (layers.Any(layer => layer.Folders.Any(folder => InFolder(file, folder))))
                return true;
            return layers.Any(layer => layer.Namespaces.Any(scope => namespaces.Any(name => InNamespace(name, scope) || InNamespace(scope, name))));
        }

        public FactsLayer? Owner(string key)
        {
            if (owners.TryGetValue(key, out var cached))
                return cached;
            if (!types.TryGetValue(key, out var type))
                return owners[key] = null;
            var matches = layers
                .SelectMany(layer => layer.Folders.Where(folder => InFolder(type.FirstFile, folder)).Select(folder => (layer, length: folder.Length))
                    .Concat(layer.Namespaces.Where(scope => InNamespace(type.Namespace, scope)).Select(scope => (layer, length: scope.Length))))
                .GroupBy(match => match.layer)
                .Select(group => (layer: group.Key, length: group.Max(match => match.length)))
                .OrderByDescending(match => match.length)
                .ToArray();
            FactsLayer? owner = null;
            if (matches.Length == 1 || matches.Length > 1 && matches[0].length > matches[1].length)
                owner = matches[0].layer;
            else if (matches.Length > 1)
                Ambiguities.Add($"type {type.Display} matches the code scope of several layers equally ({string.Join(", ", matches.Where(match => match.length == matches[0].length).Select(match => match.layer.Name))}); it is ignored.");
            owners[key] = owner;
            return owner;
        }

        private static bool InFolder(string file, string folder)
        {
            return string.Equals(file, folder, StringComparison.Ordinal)
                   || file.StartsWith(folder + "/", StringComparison.Ordinal);
        }

        private static bool InNamespace(string name, string scope)
        {
            return name == scope || name.StartsWith(scope + ".", StringComparison.Ordinal);
        }
    }
}

public sealed record FactsRuleSuggestion(string Rule, string Name, int Uses, string Example);

// One use of a first-party type inside another top-level type. CountsAsEvidence is DependencyEvidence's rule.
public sealed record FactsReference(
    string SourceName,
    string TargetName,
    string SourceType,
    string TargetType,
    string? SourceLayerId,
    string? TargetLayerId,
    string File,
    int Line,
    bool CountsAsEvidence)
{
    public string At => $"{File}:{Line}";
}

// A top-level type with its owner in the current graph.
public sealed class FactsType
{
    public FactsType(IndexType data, string? ownerLayerId)
    {
        Data = data;
        OwnerLayerId = ownerLayerId;
    }

    public IndexType Data { get; }
    public string FullName => Data.FullName;
    public string Name => Data.Name;
    public string? OwnerLayerId { get; }
    public List<string> Files => Data.Parts;
    public string At => Data.Parts.Count > 0 ? Data.Parts[0] : Data.FirstFile;
    public string Kind => Data.Kind;
    public string? Base => Data.Base.Length == 0 ? null : Data.Base;
    public IEnumerable<string> Interfaces => Data.Interfaces;
    public IReadOnlyList<IndexMember> Members => Data.Members;
    public SortedDictionary<string, SortedDictionary<string, string>> Extras => Data.Extras;
    public HashSet<string> Rules => Data.UnityEntry ? new HashSet<string>(StringComparer.Ordinal) { "unity_entry" } : new HashSet<string>(StringComparer.Ordinal);
    public HashSet<string> DerivedOwnerLayers { get; } = new(StringComparer.Ordinal);
    public List<string> Nodes { get; } = new();
    public bool IsExtensionPoint => Data.ExtensionCandidate && Data.DerivedBy.Count > 0;
}
