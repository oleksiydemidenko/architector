using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public static class Program
{
    private const int ReferenceSamples = 3;

    public static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? Directory.GetCurrentDirectory());
        var jsonPath = args.FirstOrDefault(arg => arg.StartsWith("--json="))?["--json=".Length..];
        var architecture = Architecture.Load(Path.Combine(root, "Architector"));
        var scoped = architecture.Layers.Values.Where(layer => layer.Folders.Count + layer.Namespaces.Count > 0).ToArray();
        if (scoped.Length == 0)
        {
            Console.WriteLine("No Architector layer declares a code scope; nothing to check.");
            return 0;
        }

        var sources = SourceFiles(root);
        var trees = sources.Select(file => CSharpSyntaxTree.ParseText(
                File.ReadAllText(file),
                new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: Defines(root)),
                Path.GetRelativePath(root, file).Replace('\\', '/')))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "Architector.DependencyEvidence",
            trees,
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var owners = new OwnerResolver(scoped);
        var evidence = new Dictionary<(string Source, string Target), Evidence>();
        foreach (var tree in trees.Where(tree => owners.MayOwn(tree)))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var declaration = name.Ancestors().FirstOrDefault(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                if (declaration == null || model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol sourceType)
                    continue;
                var sourceLayer = owners.Owner(sourceType);
                if (sourceLayer == null)
                    continue;
                var info = model.GetSymbolInfo(name);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                var targetType = (symbol as INamedTypeSymbol ?? symbol?.ContainingType)?.OriginalDefinition;
                if (targetType == null || !targetType.Locations.Any(location => location.IsInSource))
                    continue;
                var targetLayer = owners.Owner(targetType);
                if (targetLayer == null || ReferenceEquals(targetLayer, sourceLayer))
                    continue;
                var key = (sourceLayer.Id, targetLayer.Id);
                if (!evidence.TryGetValue(key, out var item))
                    evidence[key] = item = new Evidence();
                item.Add($"{sourceType.Name} -> {targetType.Name} ({tree.FilePath}:{name.GetLocation().GetLineSpan().StartLinePosition.Line + 1})");
            }
        }

        var violations = evidence
            .Where(pair => !architecture.AllowedDependencies.Contains(pair.Key))
            .OrderBy(pair => architecture.Path(pair.Key.Source), StringComparer.Ordinal)
            .ThenBy(pair => architecture.Path(pair.Key.Target), StringComparer.Ordinal)
            .ToArray();
        var scopedIds = scoped.Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        var unevidenced = architecture.AllowedDependencies
            .Where(pair => scopedIds.Contains(pair.Source) && scopedIds.Contains(pair.Target) && !evidence.ContainsKey(pair))
            .OrderBy(pair => architecture.Path(pair.Source), StringComparer.Ordinal)
            .ThenBy(pair => architecture.Path(pair.Target), StringComparer.Ordinal)
            .ToArray();

        var report = new StringBuilder();
        report.AppendLine($"Architector dependency evidence: {scoped.Length} scoped layers, {evidence.Count} observed layer dependencies.");
        foreach (var ambiguity in owners.Ambiguities.OrderBy(value => value, StringComparer.Ordinal))
            report.AppendLine($"warning: {ambiguity}");
        if (args.Contains("--observed"))
        {
            report.AppendLine("Observed layer dependencies:");
            foreach (var pair in evidence
                         .OrderBy(pair => architecture.Path(pair.Key.Source), StringComparer.Ordinal)
                         .ThenBy(pair => architecture.Path(pair.Key.Target), StringComparer.Ordinal))
            {
                report.AppendLine($"  {architecture.Path(pair.Key.Source)} -> {architecture.Path(pair.Key.Target)} ({pair.Value.Count} references)");
                foreach (var sample in pair.Value.Samples)
                    report.AppendLine($"    {sample}");
            }
        }
        if (violations.Length > 0)
        {
            report.AppendLine("Code dependencies without a permitting dependency relationship:");
            foreach (var violation in violations)
            {
                report.AppendLine($"  {architecture.Path(violation.Key.Source)} -> {architecture.Path(violation.Key.Target)} ({violation.Value.Count} references)");
                foreach (var sample in violation.Value.Samples)
                    report.AppendLine($"    {sample}");
            }
        }
        var cycles = evidence.Keys
            .Where(pair => string.CompareOrdinal(pair.Source, pair.Target) < 0 && evidence.ContainsKey((pair.Target, pair.Source)))
            .Select(pair => string.CompareOrdinal(architecture.Path(pair.Source), architecture.Path(pair.Target)) <= 0
                ? (pair.Source, pair.Target)
                : (pair.Target, pair.Source))
            .OrderBy(pair => architecture.Path(pair.Item1), StringComparer.Ordinal)
            .ToArray();
        if (cycles.Length > 0)
        {
            report.AppendLine("Layers that depend on each other (move the shared type to the layer that owns the contract):");
            foreach (var (first, second) in cycles)
            {
                report.AppendLine($"  {architecture.Path(first)} <-> {architecture.Path(second)}");
                report.AppendLine($"    {evidence[(first, second)].Samples.First()}");
                report.AppendLine($"    {evidence[(second, first)].Samples.First()}");
            }
        }
        if (unevidenced.Length > 0)
        {
            report.AppendLine("Declared dependencies without code evidence (information):");
            foreach (var pair in unevidenced)
                report.AppendLine($"  {architecture.Path(pair.Source)} -> {architecture.Path(pair.Target)}");
        }
        report.AppendLine(violations.Length == 0 ? "Result: no undeclared layer dependencies." : $"Result: {violations.Length} undeclared layer dependencies.");
        Console.Write(report.ToString());

        if (!string.IsNullOrEmpty(jsonPath))
        {
            var json = new
            {
                scopedLayers = scoped.Length,
                violations = violations.Select(pair => new
                {
                    sourceLayerId = pair.Key.Source,
                    sourceLayer = architecture.Path(pair.Key.Source),
                    targetLayerId = pair.Key.Target,
                    targetLayer = architecture.Path(pair.Key.Target),
                    references = pair.Value.Count,
                    samples = pair.Value.Samples
                }),
                unevidenced = unevidenced.Select(pair => new
                {
                    sourceLayerId = pair.Source,
                    sourceLayer = architecture.Path(pair.Source),
                    targetLayerId = pair.Target,
                    targetLayer = architecture.Path(pair.Target)
                }),
                layerCycles = cycles.Select(pair => new { first = architecture.Path(pair.Item1), second = architecture.Path(pair.Item2) }),
                warnings = owners.Ambiguities.OrderBy(value => value, StringComparer.Ordinal)
            };
            File.WriteAllText(Path.GetFullPath(jsonPath), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        }
        return violations.Length == 0 ? 0 : 1;
    }

    private static string[] SourceFiles(string root)
    {
        var assets = Path.Combine(root, "Assets");
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

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        return ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private sealed class Evidence
    {
        public int Count { get; private set; }
        public List<string> Samples { get; } = new();

        public void Add(string sample)
        {
            Count++;
            if (Samples.Count < ReferenceSamples && !Samples.Contains(sample))
                Samples.Add(sample);
        }
    }

    private sealed class Layer
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string ParentGroupId = string.Empty;
        public List<string> Folders = new();
        public List<string> Namespaces = new();
    }

    private sealed class Architecture
    {
        private readonly Dictionary<string, (string Name, string Parent)> groups = new(StringComparer.Ordinal);

        public Dictionary<string, Layer> Layers { get; } = new(StringComparer.Ordinal);
        public HashSet<(string Source, string Target)> AllowedDependencies { get; } = new();

        public static Architecture Load(string dataRoot)
        {
            var architecture = new Architecture();
            foreach (var document in Documents(dataRoot, "Groups", ".group.json"))
                architecture.groups[Text(document, "id")] = (Text(document, "name"), Text(document, "parentGroupId"));
            foreach (var document in Documents(dataRoot, "Layers", ".layer.json"))
            {
                var layer = new Layer
                {
                    Id = Text(document, "layerId"),
                    Name = Text(document, "name"),
                    ParentGroupId = Text(document, "parentGroupId")
                };
                if (document.TryGetProperty("scope", out var scope))
                {
                    layer.Folders = Strings(scope, "folders").Select(value => value.Replace('\\', '/').TrimEnd('/')).ToList();
                    layer.Namespaces = Strings(scope, "namespaces").ToList();
                }
                architecture.Layers[layer.Id] = layer;
            }
            foreach (var document in Documents(dataRoot, "Links", ".link.json"))
            {
                if (!document.TryGetProperty("relationships", out var relationships))
                    continue;
                foreach (var relationship in relationships.EnumerateArray())
                {
                    if (Text(relationship, "kind") == "dependency")
                        architecture.AllowedDependencies.Add((Text(relationship, "sourceLayerId"), Text(relationship, "targetLayerId")));
                }
            }
            return architecture;
        }

        public string Path(string layerId)
        {
            if (!Layers.TryGetValue(layerId, out var layer))
                return layerId;
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

        private static string Text(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static IEnumerable<string> Strings(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? string.Empty)
                : Array.Empty<string>();
        }
    }

    private sealed class OwnerResolver
    {
        private readonly IReadOnlyList<Layer> layers;
        private readonly Dictionary<INamedTypeSymbol, Layer?> owners = new(SymbolEqualityComparer.Default);

        public OwnerResolver(IReadOnlyList<Layer> layers)
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

        public Layer? Owner(INamedTypeSymbol type)
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
            Layer? owner = null;
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
