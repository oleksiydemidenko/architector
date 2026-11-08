using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Architector.CodeFacts;

// One Roslyn pass over Assets: every top-level type with its owning layer, and every reference from a type
// to another first-party type with file and line. DependencyEvidence counts a subset of these references
// with exactly its old rule, so its totals do not change.
public sealed class FactsModel
{
    private static readonly HashSet<string> UnityEntryBases = new(StringComparer.Ordinal)
    {
        "MonoBehaviour", "ScriptableObject", "EditorWindow", "Editor", "StateMachineBehaviour", "ScriptedImporter", "AssetPostprocessor"
    };

    private static readonly HashSet<string> UnityEntryAttributes = new(StringComparer.Ordinal)
    {
        "RuntimeInitializeOnLoadMethod", "InitializeOnLoad", "InitializeOnLoadMethod", "MenuItem", "CreateAssetMenu", "DidReloadScripts"
    };

    public FactsArchitecture Architecture { get; private set; } = new();
    public IReadOnlyList<FactsLayer> ScopedLayers { get; private set; } = Array.Empty<FactsLayer>();
    public List<FactsReference> References { get; } = new();
    public List<FactsType> Types { get; } = new();
    public Dictionary<string, FactsType> TypesByName { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> EvidenceWarnings { get; private set; } = Array.Empty<string>();
    public int SourceFiles { get; private set; }
    public long BuildMilliseconds { get; private set; }

    public static FactsModel Build(string root, IEnumerable<MetadataReference> references)
    {
        var watch = Stopwatch.StartNew();
        var model = new FactsModel { Architecture = FactsArchitecture.Load(Path.Combine(root, "Architector")) };
        model.ScopedLayers = model.Architecture.Layers.Values.Where(layer => layer.Scoped).ToArray();
        if (model.ScopedLayers.Count == 0)
            return model;

        var sources = SourceFilePaths(root);
        model.SourceFiles = sources.Length;
        var defines = Defines(root);
        var trees = sources.Select(file => CSharpSyntaxTree.ParseText(
                File.ReadAllText(file),
                new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: defines),
                Path.GetRelativePath(root, file).Replace('\\', '/')))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "Architector.DependencyEvidence",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Two resolvers give the same owners; only the first records warnings, and it is called exactly where
        // DependencyEvidence calls it, so the warning list stays identical.
        var evidenceOwners = new OwnerResolver(model.ScopedLayers);
        var owners = new OwnerResolver(model.ScopedLayers);
        foreach (var tree in trees)
        {
            var semantic = compilation.GetSemanticModel(tree);
            model.CollectTypes(tree, semantic, owners);
            var mayOwn = evidenceOwners.MayOwn(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var declaration = name.Ancestors().FirstOrDefault(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                if (declaration == null || semantic.GetDeclaredSymbol(declaration) is not INamedTypeSymbol sourceType)
                    continue;
                var sourceLayer = mayOwn ? evidenceOwners.Owner(sourceType) : owners.Owner(sourceType);
                var info = semantic.GetSymbolInfo(name);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                var targetType = (symbol as INamedTypeSymbol ?? symbol?.ContainingType)?.OriginalDefinition;
                if (targetType == null || !targetType.Locations.Any(location => location.IsInSource))
                    continue;
                var targetLayer = mayOwn && sourceLayer != null ? evidenceOwners.Owner(targetType) : owners.Owner(targetType);
                var sourceTop = Outermost(sourceType);
                var targetTop = Outermost(targetType);
                if (SymbolEqualityComparer.Default.Equals(sourceTop, targetTop))
                    continue;
                model.References.Add(new FactsReference(
                    sourceType.Name,
                    targetType.Name,
                    FullName(sourceTop),
                    FullName(targetTop),
                    sourceLayer?.Id,
                    targetLayer?.Id,
                    tree.FilePath,
                    name.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    mayOwn && sourceLayer != null && targetLayer != null && !ReferenceEquals(sourceLayer, targetLayer)));
            }
        }
        model.EvidenceWarnings = evidenceOwners.Ambiguities.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        model.LinkTypes();
        model.BuildMilliseconds = watch.ElapsedMilliseconds;
        return model;
    }

    // Graph questions (layer_dump, lookup) need no code analysis.
    public static FactsModel ArchitectureOnly(string root)
    {
        var model = new FactsModel { Architecture = FactsArchitecture.Load(Path.Combine(root, "Architector")) };
        model.ScopedLayers = model.Architecture.Layers.Values.Where(layer => layer.Scoped).ToArray();
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

    private static string[] Defines(string root)
    {
        var project = Path.Combine(root, "Assembly-CSharp.csproj");
        return File.Exists(project)
            ? XDocument.Load(project).Descendants().First(node => node.Name.LocalName == "DefineConstants").Value.Split(';')
            : new[] { "UNITY_EDITOR", "UNITY_2021_2_OR_NEWER", "UNITY_6000_3_OR_NEWER" };
    }

    private void CollectTypes(SyntaxTree tree, SemanticModel semantic, OwnerResolver owners)
    {
        foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
                     .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                     .Where(node => node.Parent is not BaseTypeDeclarationSyntax))
        {
            if (semantic.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
                continue;
            var name = FullName(symbol);
            var line = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (TypesByName.TryGetValue(name, out var existing))
            {
                existing.Files.Add($"{tree.FilePath}:{line}");
                continue;
            }
            var type = new FactsType(symbol, name, owners.Owner(symbol)?.Id, $"{tree.FilePath}:{line}");
            Types.Add(type);
            TypesByName[name] = type;
        }
    }

    private void LinkTypes()
    {
        foreach (var type in Types)
        {
            for (var baseType = type.Symbol.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (UnityEntryBases.Contains(baseType.Name))
                    type.Rules.Add("unity_entry");
                if (TypesByName.TryGetValue(FullName(Outermost(baseType.OriginalDefinition)), out var parent))
                    parent.DerivedBy.Add(type);
            }
            foreach (var contract in type.Symbol.AllInterfaces)
                if (TypesByName.TryGetValue(FullName(Outermost(contract.OriginalDefinition)), out var parent))
                    parent.DerivedBy.Add(type);
            var attributes = type.Symbol.GetAttributes()
                .Concat(type.Symbol.GetMembers().SelectMany(member => member.GetAttributes()))
                .Select(attribute => (attribute.AttributeClass?.Name ?? string.Empty).Replace("Attribute", string.Empty));
            if (attributes.Any(UnityEntryAttributes.Contains))
                type.Rules.Add("unity_entry");
        }
        foreach (var node in Architecture.Layers.Values.SelectMany(layer => layer.Nodes.Select(node => (layer, node))))
            if (node.node.TypeName.Length > 0 && TypesByName.TryGetValue(node.node.TypeName, out var linked))
                linked.Nodes.Add(node.node.Id);
    }

    internal static INamedTypeSymbol Outermost(INamedTypeSymbol type)
    {
        while (type.ContainingType != null)
            type = type.ContainingType;
        return type;
    }

    // Namespace and metadata name, as Architector stores a linked type ("Space.Name`1").
    internal static string FullName(INamedTypeSymbol type)
    {
        var space = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : string.Empty;
        return space + type.MetadataName;
    }

    private sealed class OwnerResolver
    {
        private readonly IReadOnlyList<FactsLayer> layers;
        private readonly Dictionary<INamedTypeSymbol, FactsLayer?> owners = new(SymbolEqualityComparer.Default);

        public OwnerResolver(IReadOnlyList<FactsLayer> layers)
        {
            this.layers = layers;
        }

        public HashSet<string> Ambiguities { get; } = new(StringComparer.Ordinal);

        public bool MayOwn(SyntaxTree tree)
        {
            if (layers.Any(layer => layer.Folders.Any(folder => InFolder(tree.FilePath, folder))))
                return true;
            var namespaces = tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
                .Select(declaration => declaration.Name.ToString())
                .ToArray();
            return layers.Any(layer => layer.Namespaces.Any(scope => namespaces.Any(name => InNamespace(name, scope) || InNamespace(scope, name))));
        }

        public FactsLayer? Owner(INamedTypeSymbol type)
        {
            while (type.ContainingType != null)
                type = type.ContainingType;
            if (owners.TryGetValue(type, out var cached))
                return cached;

            var file = type.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath ?? string.Empty;
            var name = type.ContainingNamespace?.IsGlobalNamespace == false ? type.ContainingNamespace.ToDisplayString() : string.Empty;
            var matches = layers
                .SelectMany(layer => layer.Folders.Where(folder => InFolder(file, folder)).Select(folder => (layer, length: folder.Length))
                    .Concat(layer.Namespaces.Where(scope => InNamespace(name, scope)).Select(scope => (layer, length: scope.Length))))
                .GroupBy(match => match.layer)
                .Select(group => (layer: group.Key, length: group.Max(match => match.length)))
                .OrderByDescending(match => match.length)
                .ToArray();
            FactsLayer? owner = null;
            if (matches.Length == 1 || matches.Length > 1 && matches[0].length > matches[1].length)
                owner = matches[0].layer;
            else if (matches.Length > 1)
                Ambiguities.Add($"type {type.ToDisplayString()} matches the code scope of several layers equally ({string.Join(", ", matches.Where(match => match.length == matches[0].length).Select(match => match.layer.Name))}); it is ignored.");
            owners[type] = owner;
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

public sealed class FactsType
{
    public FactsType(INamedTypeSymbol symbol, string fullName, string? ownerLayerId, string at)
    {
        Symbol = symbol;
        FullName = fullName;
        OwnerLayerId = ownerLayerId;
        Files.Add(at);
    }

    public INamedTypeSymbol Symbol { get; }
    public string FullName { get; }
    public string Name => Symbol.Name;
    public string? OwnerLayerId { get; }
    public List<string> Files { get; } = new();
    public string At => Files[0];
    public HashSet<FactsType> DerivedBy { get; } = new();
    public HashSet<string> Rules { get; } = new(StringComparer.Ordinal);
    public List<string> Nodes { get; } = new();

    public string Kind => Symbol.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Struct => Symbol.IsRecord ? "record struct" : "struct",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => Symbol.IsRecord ? "record" : "class"
    };

    public string? Base => Symbol.BaseType is { SpecialType: SpecialType.None } baseType ? baseType.ToDisplayString() : null;

    public IEnumerable<string> Interfaces => Symbol.Interfaces.Select(contract => contract.ToDisplayString());

    public bool IsExtensionPoint =>
        Symbol.DeclaredAccessibility == Accessibility.Public
        && (Symbol.TypeKind == TypeKind.Interface || Symbol.IsAbstract && Symbol.TypeKind == TypeKind.Class)
        && DerivedBy.Count > 0;

    public IEnumerable<ISymbol> PublicMembers => Symbol.GetMembers()
        .Where(member => member.DeclaredAccessibility == Accessibility.Public && !member.IsImplicitlyDeclared)
        .Where(member => member is not IMethodSymbol method
                         || method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion);
}
