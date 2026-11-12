using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Architector.CodeFacts;

// Everything Roslyn learns from Assets that does not depend on the graph: files with their namespaces, top-level types
// with their facts, and every use of a first-party type inside another top-level type. Layer scopes, owners and evidence
// are applied on top by FactsModel in a fraction of a second, so editing the graph never repeats the analysis.
// The index is saved under Library/Architector and reused while no C# file, csproj or code-facts.json changed.
public sealed class CodeIndex
{
    private const int FormatVersion = 4;

    public string Signature { get; private set; } = string.Empty;
    public string ReferenceSet { get; private set; } = string.Empty;
    public List<IndexFile> Files { get; } = new();
    public List<IndexType> Types { get; } = new();
    public List<IndexReference> References { get; } = new();
    public List<FactsRuleSuggestion> Suggestions { get; } = new();
    // Engine and package assemblies taken from Unity's project files, and types whose base class the analysis still
    // cannot see (their inherited members are unknown, so names inside them may be missed, never misbound).
    public int UnityReferenceCount { get; set; }
    public int UnresolvedBaseTypes { get; set; }
    public int ProjectReferencesCompiled { get; set; }
    public int ProjectReferencesFromSources { get; set; }
    public List<string> MissingAssemblies { get; } = new();
    public List<(string Type, string Base, string At, int Count)> UnseenBaseExamples { get; } = new();
    // Methods that list types with GetTypes, keep subclasses of a project base and create them with Activator.
    public List<(string BaseKey, string At)> ReflectionSites { get; } = new();
    public long BuildMilliseconds { get; private set; }
    public bool FromDisk { get; private set; }

    public static string SnapshotPath(string root, string referenceSet) =>
        Path.Combine(root, "Library", "Architector", $"code-index-{referenceSet}.bin");

    // Reuses the saved index when its signature still matches, otherwise analyses the code and saves the result.
    public static CodeIndex LoadOrBuild(string root, string referenceSet, Func<IEnumerable<MetadataReference>> references)
    {
        var signature = ComputeSignature(root);
        var path = SnapshotPath(root, referenceSet);
        var saved = TryLoad(path, signature);
        if (saved != null)
            return saved;
        var index = Build(root, referenceSet, references(), signature);
        try
        {
            index.Save(path);
        }
        catch (IOException)
        {
            // Another process may be writing the same snapshot; the next call saves it.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return index;
    }

    public static string ComputeSignature(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string file)
        {
            var info = new FileInfo(file);
            hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(root, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n"));
        }
        hash.AppendData(Encoding.UTF8.GetBytes($"format {FormatVersion}\n"));
        foreach (var file in FactsModel.SourceFilePaths(root))
            Add(file);
        foreach (var extra in new[] { Path.Combine(root, "Architector", "code-facts.json") }
                     .Concat(Directory.Exists(root) ? Directory.GetFiles(root, "*.csproj").OrderBy(path => path, StringComparer.Ordinal) : Enumerable.Empty<string>()))
            if (File.Exists(extra))
                Add(extra);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static CodeIndex Build(string root, string referenceSet, IEnumerable<MetadataReference> references, string signature)
    {
        var watch = Stopwatch.StartNew();
        var index = new CodeIndex { Signature = signature, ReferenceSet = referenceSet };
        var extractors = FactsExtractors.Load(Path.Combine(root, "Architector", "code-facts.json"));
        var defines = Defines(root);
        var trees = FactsModel.SourceFilePaths(root).Select(file => CSharpSyntaxTree.ParseText(
                File.ReadAllText(file),
                new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: defines),
                Path.GetRelativePath(root, file).Replace('\\', '/')))
            .ToArray();
        var platform = references.ToArray();
        var unityReferences = UnityReferences(root, platform);
        index.UnityReferenceCount = unityReferences.HintReferences;
        index.ProjectReferencesCompiled = unityReferences.Compiled;
        index.ProjectReferencesFromSources = unityReferences.FromSources;
        index.MissingAssemblies.AddRange(unityReferences.Missing.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal));
        var compilation = CSharpCompilation.Create(
            "Architector.DependencyEvidence",
            trees,
            platform.Concat(unityReferences.References),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var byKey = new Dictionary<string, IndexType>(StringComparer.Ordinal);
        var symbols = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        var extraction = new Extraction(extractors);
        string KeyOf(INamedTypeSymbol type)
        {
            var file = type.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath ?? string.Empty;
            return FullName(type) + "@" + file;
        }
        IndexType TypeOf(INamedTypeSymbol type)
        {
            var key = KeyOf(type);
            if (byKey.TryGetValue(key, out var existing))
                return existing;
            var created = new IndexType
            {
                Key = key,
                FullName = FullName(type),
                Name = type.Name,
                Display = type.ToDisplayString(),
                Namespace = type.ContainingNamespace?.IsGlobalNamespace == false ? type.ContainingNamespace.ToDisplayString() : string.Empty,
                FirstFile = type.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath ?? string.Empty
            };
            byKey[key] = created;
            symbols[key] = type;
            index.Types.Add(created);
            return created;
        }

        foreach (var tree in trees)
        {
            var semantic = compilation.GetSemanticModel(tree);
            var rootNode = tree.GetRoot();
            index.Files.Add(new IndexFile
            {
                Path = tree.FilePath,
                Namespaces = rootNode.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(declaration => declaration.Name.ToString()).ToList()
            });
            foreach (var declaration in rootNode.DescendantNodes().OfType<MemberDeclarationSyntax>()
                         .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                         .Where(node => node.Parent is not BaseTypeDeclarationSyntax))
            {
                if (semantic.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
                    continue;
                var type = TypeOf(symbol);
                type.Parts.Add($"{tree.FilePath}:{declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                extraction.Extract(declaration, type, tree.FilePath);
                extraction.Behaviour(declaration, symbol, type, semantic, tree.FilePath);
                Storage(declaration, type, semantic, tree.FilePath, TypeOf, index);
            }
            foreach (var name in rootNode.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var declaration = name.Ancestors().FirstOrDefault(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                if (declaration == null || semantic.GetDeclaredSymbol(declaration) is not INamedTypeSymbol sourceType)
                    continue;
                var sourceTop = TypeOf(Outermost(sourceType));
                sourceTop.NamedIn.Add(tree.FilePath);
                var info = semantic.GetSymbolInfo(name);
                // A candidate is used only when the symbol itself is right and only the call around it failed. A type
                // offered where a value is needed is how an inherited member of an unseen base class looks when the
                // project also has a type of that name, so it is never counted.
                var symbol = info.Symbol ?? (info.CandidateReason is CandidateReason.NotAValue or CandidateReason.NotATypeOrNamespace
                    ? null
                    : info.CandidateSymbols.FirstOrDefault());
                if (symbol is INamedTypeSymbol && TypeNameUsedAsValue(name))
                    continue;
                var targetType = (symbol as INamedTypeSymbol ?? symbol?.ContainingType)?.OriginalDefinition;
                if (targetType == null || !targetType.Locations.Any(location => location.IsInSource))
                    continue;
                var targetTop = TypeOf(Outermost(targetType));
                if (ReferenceEquals(sourceTop, targetTop))
                    continue;
                index.References.Add(new IndexReference(
                    sourceType.Name,
                    targetType.Name,
                    sourceTop.Key,
                    targetTop.Key,
                    tree.FilePath,
                    name.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }

        foreach (var type in index.Types)
            Describe(type, symbols[type.Key], byKey, KeyOf);
        index.UnresolvedBaseTypes = symbols.Values.Count(symbol => HasUnseenBase(symbol));
        index.UnseenBaseExamples.AddRange(symbols.Values
            .Select(symbol => (Symbol: symbol, Base: UnseenBase(symbol)))
            .Where(item => item.Base != null)
            .GroupBy(item => item.Base!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(group =>
            {
                var first = group.OrderBy(item => item.Symbol.ToDisplayString(), StringComparer.Ordinal).First();
                var location = first.Symbol.Locations.FirstOrDefault(item => item.IsInSource);
                return (first.Symbol.ToDisplayString(), group.Key,
                    location == null ? string.Empty : $"{location.SourceTree!.FilePath}:{location.GetLineSpan().StartLinePosition.Line + 1}", group.Count());
            }));
        index.Suggestions.AddRange(extraction.Suggest(index.Types));
        index.BuildMilliseconds = watch.ElapsedMilliseconds;
        return index;
    }

    // Unity serializes a script's own document only for these bases: components and assets placed in scenes, prefabs
    // and asset files.
    private static readonly HashSet<string> PlaceableBases = new(StringComparer.Ordinal)
    {
        "MonoBehaviour", "ScriptableObject", "StateMachineBehaviour"
    };

    // ScriptableObjects that Unity creates itself and never stores in a scene, prefab or asset of the project.
    private static readonly HashSet<string> EditorOnlyBases = new(StringComparer.Ordinal)
    {
        "EditorWindow", "Editor", "PropertyDrawer", "ScriptableSingleton", "ScriptableWizard", "PopupWindowContent",
        "AssetPostprocessor", "ScriptedImporter", "ScriptedImporterEditor", "AssetImporterEditor", "SettingsProvider"
    };

    private static readonly HashSet<string> UnityEntryBases = new(StringComparer.Ordinal)
    {
        "MonoBehaviour", "ScriptableObject", "EditorWindow", "Editor", "StateMachineBehaviour", "ScriptedImporter", "AssetPostprocessor"
    };

    private static readonly HashSet<string> UnityEntryAttributes = new(StringComparer.Ordinal)
    {
        "RuntimeInitializeOnLoadMethod", "InitializeOnLoad", "InitializeOnLoadMethod", "MenuItem", "CreateAssetMenu", "DidReloadScripts"
    };

    private static readonly SymbolDisplayFormat MemberFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat.RemoveMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);

    private static void Describe(IndexType type, INamedTypeSymbol symbol, Dictionary<string, IndexType> byKey, Func<INamedTypeSymbol, string> keyOf)
    {
        type.Kind = symbol.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => symbol.IsRecord ? "record struct" : "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => symbol.IsRecord ? "record" : "class"
        };
        type.Base = symbol.BaseType is { SpecialType: SpecialType.None } baseType ? baseType.ToDisplayString() : string.Empty;
        type.Interfaces.AddRange(symbol.Interfaces.Select(contract => contract.ToDisplayString()));
        type.ExtensionCandidate = symbol.DeclaredAccessibility == Accessibility.Public
                                  && (symbol.TypeKind == TypeKind.Interface || symbol.IsAbstract && symbol.TypeKind == TypeKind.Class);
        type.IsAbstract = symbol.IsAbstract && symbol.TypeKind == TypeKind.Class;
        type.IsGeneric = symbol.TypeParameters.Length > 0;
        type.IsSerializable = symbol.TypeKind == TypeKind.Class
                              && symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is "SerializableAttribute" or "Serializable");
        for (var parent = symbol.BaseType; parent != null; parent = parent.BaseType)
            if (parent.Name == "Object" && parent.ContainingNamespace?.ToDisplayString() == "UnityEngine")
                type.IsUnityObject = true;
        for (var parent = symbol.BaseType; parent != null; parent = parent.BaseType)
        {
            if (EditorOnlyBases.Contains(parent.Name))
            {
                type.Placeable = false;
                break;
            }
            if (PlaceableBases.Contains(parent.Name))
                type.Placeable = true;
        }
        foreach (var member in symbol.GetMembers()
                     .Where(member => member.DeclaredAccessibility == Accessibility.Public && !member.IsImplicitlyDeclared)
                     .Where(member => member is not IMethodSymbol method
                                      || method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion))
        {
            var location = member.Locations.FirstOrDefault(item => item.IsInSource);
            var (_, referenceName, parameters) = MemberReference(member);
            type.Members.Add(new IndexMember(
                member.Kind.ToString().ToLowerInvariant(),
                member.ToDisplayString(MemberFormat),
                location == null ? string.Empty : $"{location.SourceTree!.FilePath}:{location.GetLineSpan().StartLinePosition.Line + 1}",
                referenceName,
                parameters ?? System.Array.Empty<string>(),
                parameters != null));
        }
        for (var parent = symbol.BaseType; parent != null; parent = parent.BaseType)
        {
            if (UnityEntryBases.Contains(parent.Name))
                type.UnityEntry = true;
            if (byKey.TryGetValue(keyOf(Outermost(parent.OriginalDefinition)), out var baseEntry))
                baseEntry.DerivedBy.Add(type.Key);
        }
        foreach (var contract in symbol.AllInterfaces)
            if (byKey.TryGetValue(keyOf(Outermost(contract.OriginalDefinition)), out var contractEntry))
                contractEntry.DerivedBy.Add(type.Key);
        if (symbol.GetAttributes().Concat(symbol.GetMembers().SelectMany(member => member.GetAttributes()))
            .Select(attribute => (attribute.AttributeClass?.Name ?? string.Empty).Replace("Attribute", string.Empty))
            .Any(UnityEntryAttributes.Contains))
            type.UnityEntry = true;
    }

    private static (string Kind, string Name, string[]? Parameters) MemberReference(ISymbol member)
    {
        static string[] Parameters(IEnumerable<IParameterSymbol> parameters) => parameters.Select(parameter =>
            (parameter.RefKind switch { RefKind.Out => "out:", RefKind.Ref => "ref:", RefKind.In => "in:", _ => string.Empty })
            + ShortMetadataName(parameter.Type)).ToArray();
        return member switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } method => ("constructor", ".ctor", Parameters(method.Parameters)),
            IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } method => ("operator", method.MetadataName, Parameters(method.Parameters)),
            IMethodSymbol method => ("method", method.Name, Parameters(method.Parameters)),
            IPropertySymbol property => ("property", property.MetadataName, property.Parameters.Length > 0 ? Parameters(property.Parameters) : null),
            IEventSymbol eventSymbol => ("event", eventSymbol.Name, null),
            IFieldSymbol field => ("field", field.Name, null),
            _ => (member.Kind.ToString().ToLowerInvariant(), member.Name, null)
        };
    }

    // The type's metadata name without namespace, containing type or generic arity, as the type index's symbol keys
    // reduce to: Single for float, List for List<int>, !0 and !!0 for type parameters, a [] suffix for arrays.
    internal static string ShortMetadataName(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => ShortMetadataName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]",
        ITypeParameterSymbol parameter => (parameter.TypeParameterKind == TypeParameterKind.Method ? "!!" : "!") + parameter.Ordinal,
        IPointerTypeSymbol pointer => ShortMetadataName(pointer.PointedAtType) + "*",
        _ => type.MetadataName.Split('`')[0]
    };

    // A type name where C# needs a value is only valid when something else of that name is in scope, such as a member
    // inherited from a base class the analysis cannot see. Type positions, "Type.Member", qualified names and nameof
    // stay references.
    private static bool TypeNameUsedAsValue(SimpleNameSyntax name)
    {
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node)
            node = qualified;
        if (node.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax or UsingDirectiveSyntax or NameEqualsSyntax
            or AttributeSyntax or TypeArgumentListSyntax or BaseTypeSyntax or TypeConstraintSyntax)
            return false;
        if (node is ExpressionSyntax expression && SyntaxFacts.IsInTypeOnlyContext(expression))
            return false;
        if (node.Parent is MemberAccessExpressionSyntax access && access.Expression == node)
            return false;
        if (node.Parent is MemberAccessExpressionSyntax nameAccess && nameAccess.Name == node)
            return false;
        if (node.Ancestors().OfType<InvocationExpressionSyntax>().Any(call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }))
            return false;
        return node.Parent is ArgumentSyntax or EqualsValueClauseSyntax or ArrowExpressionClauseSyntax or ReturnStatementSyntax
            or AssignmentExpressionSyntax or BinaryExpressionSyntax or ConditionalExpressionSyntax or InitializerExpressionSyntax
            or ParenthesizedExpressionSyntax or InterpolationSyntax or ExpressionStatementSyntax or PrefixUnaryExpressionSyntax
            or PostfixUnaryExpressionSyntax or ElementAccessExpressionSyntax;
    }

    // How values of project types are stored and created, for the wiring of types assets cannot name: [SerializeReference]
    // fields and their element types, fields Unity serializes by value, objects created with new, and reflection sites
    // (GetTypes + a base-type filter + Activator.CreateInstance in one method).
    private static readonly string[] ReflectionFilters = { "IsSubclassOf", "IsAssignableFrom", "IsAssignableTo", "GetInterface" };

    private static void Storage(SyntaxNode declaration, IndexType type, SemanticModel semantic, string file, Func<INamedTypeSymbol, IndexType> typeOf, CodeIndex index)
    {
        string At(SyntaxNode node) => $"{file}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
        IndexType? Project(ITypeSymbol? candidate) =>
            candidate is INamedTypeSymbol named && named.Locations.Any(location => location.IsInSource) ? typeOf(Outermost(named.OriginalDefinition)) : null;
        foreach (var field in declaration.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            var attributes = field.AttributeLists.SelectMany(list => list.Attributes)
                .Select(attribute => FactsExtractors.AttributeName(attribute.Name.ToString().Split('.').Last()))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var variable in field.Declaration.Variables)
            {
                if (semantic.GetDeclaredSymbol(variable) is not IFieldSymbol symbol || symbol.IsStatic || symbol.IsConst || symbol.IsReadOnly)
                    continue;
                var element = Element(symbol.Type);
                if (attributes.Contains("SerializeReference"))
                {
                    var target = Project(element);
                    type.ReferenceFields.Add(($"{symbol.ContainingType.Name}.{symbol.Name}", target?.Key ?? string.Empty, At(variable)));
                    continue;
                }
                var serialized = !attributes.Contains("NonSerialized")
                                 && (symbol.DeclaredAccessibility == Accessibility.Public || attributes.Contains("SerializeField"));
                if (serialized && Project(element) is { } byValue && byValue.ByValueAt.Length == 0 && !ReferenceEquals(byValue, type))
                    byValue.ByValueAt = At(variable);
            }
        }
        foreach (var creation in declaration.DescendantNodes().Where(node => node is BaseObjectCreationExpressionSyntax))
            if (Project(semantic.GetTypeInfo(creation).Type) is { } created && created.CreatedAt.Length == 0)
                created.CreatedAt = At(creation);
        foreach (var body in declaration.DescendantNodes().Where(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax))
        {
            var calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
            if (!calls.Any(call => CallName(call) is "GetTypes" or "GetExportedTypes"))
                continue;
            var create = calls.FirstOrDefault(call => CallName(call) == "CreateInstance"
                                                      && call.Expression is MemberAccessExpressionSyntax access
                                                      && access.Expression.ToString().EndsWith("Activator", StringComparison.Ordinal));
            if (create == null)
                continue;
            var bases = calls.Where(call => ReflectionFilters.Contains(CallName(call)))
                .SelectMany(call => call.DescendantNodesAndSelf().OfType<TypeOfExpressionSyntax>())
                .Select(typeOfExpression => Project(semantic.GetTypeInfo(typeOfExpression.Type).Type))
                .Where(found => found != null)
                .Distinct();
            foreach (var found in bases)
                index.ReflectionSites.Add((found!.Key, At(create)));
        }
    }

    internal static string CallName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty
    };

    // The element type of T, T[] and List<T>.
    private static ITypeSymbol Element(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => array.ElementType,
        INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic when generic.Name is "List" or "IList" or "IReadOnlyList" => generic.TypeArguments[0],
        _ => type
    };

    private static string? UnseenBase(INamedTypeSymbol symbol)
    {
        for (var parent = symbol.BaseType; parent != null; parent = parent.BaseType)
            if (parent.TypeKind == TypeKind.Error)
                return parent.ToDisplayString();
        return null;
    }

    private static bool HasUnseenBase(INamedTypeSymbol symbol)
    {
        for (var parent = symbol.BaseType; parent != null; parent = parent.BaseType)
            if (parent.TypeKind == TypeKind.Error)
                return true;
        return false;
    }

    // What Unity's project files make the compilation see besides the project's own sources (Assets): the assemblies their
    // <Reference HintPath> entries name (engine, packages Unity ships compiled), and every <ProjectReference> followed
    // transitively. A referenced project whose sources lie outside Assets (a package or plugin compiled from source) is
    // loaded from Library/ScriptAssemblies/<AssemblyName>.dll, or, when Unity has not compiled it, compiled here from its
    // own sources. Left out: the .NET assemblies (the host brings its own) and the project's own assemblies, whose
    // sources the index compiles itself. Without the project files nothing is added.
    internal sealed class UnityProjectReferences
    {
        public List<MetadataReference> References { get; } = new();
        public int HintReferences { get; set; }
        public int Compiled { get; set; }
        public int FromSources { get; set; }
        public List<string> Missing { get; } = new();
    }

    internal static UnityProjectReferences UnityReferences(string root, IReadOnlyList<MetadataReference> platform)
    {
        var result = new UnityProjectReferences();
        var projects = new[] { "Assembly-CSharp.csproj", "Assembly-CSharp-Editor.csproj" }.Select(name => Path.Combine(root, name)).Where(File.Exists).ToArray();
        if (projects.Length == 0)
            return result;
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = Path.Combine(root, "Assets");
        if (Directory.Exists(assets))
            foreach (var definition in Directory.GetFiles(assets, "*.asmdef", SearchOption.AllDirectories))
            {
                var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(definition), "\"name\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success)
                    own.Add(match.Groups[1].Value);
            }
        var hints = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, MetadataReference?>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        XDocument? Load(string project)
        {
            try
            {
                return File.Exists(project) ? XDocument.Load(project) : null;
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        IEnumerable<MetadataReference> HintsOf(string project, XDocument document)
        {
            var projectFolder = Path.GetDirectoryName(project) ?? root;
            foreach (var hint in document.Descendants().Where(node => node.Name.LocalName == "HintPath")
                         .Select(node => Path.GetFullPath(Path.Combine(projectFolder, node.Value.Trim().Replace('\\', Path.DirectorySeparatorChar)))))
            {
                var name = Path.GetFileNameWithoutExtension(hint);
                var normalized = hint.Replace('\\', '/');
                if (name.Length == 0 || own.Contains(name) || name.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)
                    || name is "mscorlib" or "netstandard" || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                    || normalized.Contains("/UnityReferenceAssemblies/", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains("/NetStandard/", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(hint))
                    continue;
                if (!hints.TryGetValue(name, out var reference))
                {
                    try
                    {
                        reference = MetadataReference.CreateFromFile(hint);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
                    {
                        continue;
                    }
                    hints[name] = reference;
                }
                yield return reference;
            }
        }

        // The referenced projects of one project file, each resolved to a metadata reference (or nothing for an own one).
        IEnumerable<MetadataReference> ProjectsOf(string project, XDocument document)
        {
            var folder = Path.GetDirectoryName(project) ?? root;
            foreach (var include in document.Descendants().Where(node => node.Name.LocalName == "ProjectReference")
                         .Select(node => node.Attribute("Include")?.Value ?? string.Empty).Where(value => value.Length > 0))
            {
                var reference = Resolve(Path.GetFullPath(Path.Combine(folder, include.Replace('\\', Path.DirectorySeparatorChar))));
                if (reference != null)
                    yield return reference;
            }
        }

        MetadataReference? Resolve(string project)
        {
            if (resolved.TryGetValue(project, out var known))
                return known;
            if (!visiting.Add(project))
                return null;
            var document = Load(project);
            MetadataReference? reference = null;
            if (document == null)
                result.Missing.Add(Path.GetFileNameWithoutExtension(project));
            else
            {
                var name = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "AssemblyName")?.Value.Trim();
                if (string.IsNullOrEmpty(name))
                    name = Path.GetFileNameWithoutExtension(project);
                var folder = Path.GetDirectoryName(project) ?? root;
                var sources = document.Descendants().Where(node => node.Name.LocalName == "Compile")
                    .Select(node => node.Attribute("Include")?.Value ?? string.Empty)
                    .Where(value => value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .Select(value => Path.GetFullPath(Path.Combine(folder, value.Replace('\\', Path.DirectorySeparatorChar))))
                    .ToArray();
                var dependencies = HintsOf(project, document).Concat(ProjectsOf(project, document)).ToList();
                var ownSources = sources.Length == 0 || sources.All(file => Path.GetRelativePath(root, file).Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal));
                if (!ownSources && !own.Contains(name))
                {
                    var compiled = Path.Combine(root, "Library", "ScriptAssemblies", name + ".dll");
                    if (File.Exists(compiled))
                    {
                        try
                        {
                            reference = MetadataReference.CreateFromFile(compiled);
                            result.Compiled++;
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
                        {
                        }
                    }
                    if (reference == null)
                    {
                        var existing = sources.Where(File.Exists).ToArray();
                        if (existing.Length > 0)
                        {
                            var defines = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "DefineConstants")?.Value.Split(';') ?? System.Array.Empty<string>();
                            var trees = existing.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: defines), file));
                            reference = CSharpCompilation.Create(name, trees, platform.Concat(dependencies),
                                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)).ToMetadataReference();
                            result.FromSources++;
                        }
                        else
                            result.Missing.Add(name);
                    }
                }
            }
            visiting.Remove(project);
            resolved[project] = reference;
            return reference;
        }

        foreach (var project in projects)
        {
            var document = Load(project);
            if (document == null)
                continue;
            HintsOf(project, document).ToList();
            ProjectsOf(project, document).ToList();
        }
        result.HintReferences = hints.Count;
        result.References.AddRange(hints.Values);
        result.References.AddRange(resolved.Values.Where(reference => reference != null).Select(reference => reference!));
        return result;
    }

    private static string[] Defines(string root)
    {
        var project = Path.Combine(root, "Assembly-CSharp.csproj");
        return File.Exists(project)
            ? XDocument.Load(project).Descendants().First(node => node.Name.LocalName == "DefineConstants").Value.Split(';')
            : new[] { "UNITY_EDITOR", "UNITY_2021_2_OR_NEWER", "UNITY_6000_3_OR_NEWER" };
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

    // Project rules (code-facts.json) applied while reading a declaration, and the repeated calls and attributes they miss.
    private sealed class Extraction
    {
        private readonly FactsExtractors extractors;
        private readonly List<(string Name, string[] TypeArguments, string At)> calls = new();
        private readonly List<(string Name, string At)> attributes = new();

        public Extraction(FactsExtractors extractors)
        {
            this.extractors = extractors;
        }

        public void Extract(SyntaxNode declaration, IndexType type, string file)
        {
            string At(SyntaxNode node) => $"{file}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
            foreach (var generic in declaration.DescendantNodes().OfType<GenericNameSyntax>())
            {
                if (generic.Parent is not (InvocationExpressionSyntax or MemberAccessExpressionSyntax))
                    continue;
                var name = generic.Identifier.ValueText;
                if (extractors.GenericCalls.Contains(name))
                    type.AddExtra("generic_calls", $"{name}<{string.Join(", ", generic.TypeArgumentList.Arguments)}>", At(generic));
                else
                    calls.Add((name, generic.TypeArgumentList.Arguments.Select(ShortTypeName).ToArray(), At(generic)));
            }
            foreach (var attribute in declaration.DescendantNodes().OfType<AttributeSyntax>())
            {
                var attributeName = FactsExtractors.AttributeName(attribute.Name.ToString().Split('.').Last());
                var types = attribute.Parent?.Parent switch
                {
                    BaseMethodDeclarationSyntax method => method.ParameterList.Parameters.Select(parameter => parameter.Type?.ToString() ?? string.Empty),
                    FieldDeclarationSyntax field => new[] { field.Declaration.Type.ToString() },
                    PropertyDeclarationSyntax property => new[] { property.Type.ToString() },
                    ParameterSyntax parameter => new[] { parameter.Type?.ToString() ?? string.Empty },
                    _ => null
                };
                if (types == null)
                    continue;
                var kind = extractors.InjectionAttributes.Contains(attributeName) ? "injected"
                    : extractors.SerializedAttributes.Contains(attributeName) ? "serialized"
                    : null;
                if (kind == null)
                {
                    if (!CommonAttributes.Contains(attributeName))
                        attributes.Add((attributeName, At(attribute)));
                    continue;
                }
                foreach (var value in types.Where(value => value.Length > 0))
                    type.AddExtra(kind, value, At(attribute));
            }
        }

        private static readonly HashSet<string> LifecycleMethods = new(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable", "OnDestroy"
        };

        // Frame waits that make a loop run every frame, and calls that poll every frame wherever they stand.
        private static readonly HashSet<string> FrameWaits = new(StringComparer.Ordinal)
        {
            "Yield", "NextFrame", "DelayFrame", "WaitForEndOfFrame", "WaitForFixedUpdate"
        };

        private static readonly HashSet<string> PollingCalls = new(StringComparer.Ordinal)
        {
            "WaitUntil", "WaitWhile", "WaitUntilValueChanged", "EveryUpdate", "EveryValueChanged"
        };

        private static readonly string[] FrameWaitOwners = { "UniTask", "Task", "UniTaskAsyncEnumerable", "Observable" };

        // What a type reads, writes, subscribes to, assigns in other types and which Unity callbacks it has, each with
        // file:line, so an agent opens the lines that decide what the type owns instead of the whole file.
        public void Behaviour(SyntaxNode declaration, INamedTypeSymbol owner, IndexType type, SemanticModel semantic, string file)
        {
            string At(SyntaxNode node) => $"{file}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
            string Section(string name) => extractors.Access(name) switch { "read" => "reads", "write" => "writes", _ => "uses" };
            foreach (var generic in declaration.DescendantNodes().OfType<GenericNameSyntax>())
            {
                if (generic.Parent is not (InvocationExpressionSyntax or MemberAccessExpressionSyntax))
                    continue;
                var name = generic.Identifier.ValueText;
                if (!extractors.GenericCalls.Contains(name))
                    continue;
                type.AddBehaviour(Section(name), $"{name}<{string.Join(", ", generic.TypeArgumentList.Arguments)}>", At(generic));
            }
            foreach (var call in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = CallName(call);
                if (name.Length == 0)
                    continue;
                var nameNode = call.Expression switch
                {
                    MemberAccessExpressionSyntax access => access.Name,
                    MemberBindingExpressionSyntax binding => binding.Name,
                    SimpleNameSyntax simple => simple,
                    _ => null
                };
                if (nameNode is GenericNameSyntax)
                    continue;
                // A non-generic overload of a configured generic call (Add(instance) next to Add<T>()) counts the same.
                if (extractors.GenericCalls.Contains(name)
                    && semantic.GetSymbolInfo(call).Symbol is IMethodSymbol { IsGenericMethod: false } method
                    && method.ContainingType.GetMembers(name).OfType<IMethodSymbol>().Any(sibling => sibling.IsGenericMethod))
                {
                    var argument = call.ArgumentList.Arguments.FirstOrDefault();
                    var argumentType = argument == null ? string.Empty : semantic.GetTypeInfo(argument.Expression).Type?.Name ?? string.Empty;
                    type.AddBehaviour(Section(name), $"{name}({argumentType})", At(call));
                    continue;
                }
                // memberCalls: a call on a receiver of a configured base type.
                if (extractors.MemberCalls.TryGetValue(name, out var rules) && call.Expression is MemberAccessExpressionSyntax receiverAccess)
                {
                    var receiver = semantic.GetTypeInfo(receiverAccess.Expression).Type;
                    var rule = rules.FirstOrDefault(candidate => DerivesFrom(receiver, candidate.On));
                    if (receiver != null && rule.On != null)
                    {
                        type.AddBehaviour(rule.Access switch { "read" => "reads", "write" => "writes", _ => "uses" }, $"{receiver.Name}.{name}", At(call));
                        continue;
                    }
                }
                var receiverText = (call.Expression as MemberAccessExpressionSyntax)?.Expression.ToString() ?? string.Empty;
                var onFrameOwner = FrameWaitOwners.Any(owner => receiverText == owner || receiverText.EndsWith("." + owner, StringComparison.Ordinal));
                if (onFrameOwner && PollingCalls.Contains(name))
                    type.AddBehaviour("recurring", $"{ShortReceiver(receiverText)}.{name}", At(call));
                else if (onFrameOwner && FrameWaits.Contains(name) && InLoop(call))
                    type.AddBehaviour("recurring", $"{ShortReceiver(receiverText)}.{name} in a loop", At(call));
                else if (extractors.RecurringCalls.Contains(name))
                    type.AddBehaviour("recurring", receiverText.Length > 0 ? $"{ShortReceiver(receiverText)}.{name}" : name, At(call));
            }
            foreach (var yield in declaration.DescendantNodes().OfType<YieldStatementSyntax>())
            {
                if (!yield.IsKind(SyntaxKind.YieldReturnStatement) || !InLoop(yield))
                    continue;
                var value = yield.Expression switch
                {
                    LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression) => "yield return null",
                    ObjectCreationExpressionSyntax creation when creation.Type.ToString().Split('.').Last() is "WaitForEndOfFrame" or "WaitForFixedUpdate"
                        => $"yield return new {creation.Type.ToString().Split('.').Last()}()",
                    _ => null
                };
                if (value != null)
                    type.AddBehaviour("recurring", value + " in a loop", At(yield));
            }
            foreach (var assignment in declaration.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var target = semantic.GetSymbolInfo(assignment.Left).Symbol;
                if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) || assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
                {
                    var handlerType = target switch
                    {
                        IEventSymbol => null,
                        IFieldSymbol field => field.Type,
                        IPropertySymbol property => property.Type,
                        ILocalSymbol local => local.Type,
                        IParameterSymbol parameter => parameter.Type,
                        _ => null
                    };
                    if (target is IEventSymbol || handlerType?.TypeKind == TypeKind.Delegate)
                        type.AddBehaviour(assignment.IsKind(SyntaxKind.AddAssignmentExpression) ? "subscribes" : "unsubscribes", MemberName(target!), At(assignment));
                    continue;
                }
                if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    || assignment.Left is not MemberAccessExpressionSyntax access
                    || access.Expression is ThisExpressionSyntax or BaseExpressionSyntax
                    || target is not (IFieldSymbol or IPropertySymbol)
                    || target.ContainingType == null
                    || SymbolEqualityComparer.Default.Equals(target.ContainingType.OriginalDefinition, owner.OriginalDefinition)
                    || LocalValueCopy(access.Expression, semantic))
                    continue;
                type.AddBehaviour("assigns", MemberName(target), At(assignment));
            }
            if (owner.TypeKind != TypeKind.Class || !DerivesFrom(owner, "MonoBehaviour") && !DerivesFrom(owner, "ScriptableObject"))
                return;
            foreach (var method in declaration.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .Where(method => method.Parent == declaration && method.ParameterList.Parameters.Count == 0)
                         .Where(method => LifecycleMethods.Contains(method.Identifier.ValueText)))
                type.AddBehaviour("lifecycle", method.Identifier.ValueText, At(method.Identifier.Parent!));
        }

        // An assignment whose receiver chain starts at a local or a by-value parameter of a value type changes a copy,
        // not another object's state (position.x = 1; var copy = map[key]; copy.a = 1).
        private static bool LocalValueCopy(ExpressionSyntax receiver, SemanticModel semantic)
        {
            var current = receiver;
            while (true)
            {
                if (semantic.GetTypeInfo(current).Type is not { IsValueType: true })
                    return false;
                switch (current)
                {
                    case MemberAccessExpressionSyntax member:
                        current = member.Expression;
                        continue;
                    case ElementAccessExpressionSyntax:
                        return false;
                    case ParenthesizedExpressionSyntax parenthesized:
                        current = parenthesized.Expression;
                        continue;
                }
                return semantic.GetSymbolInfo(current).Symbol switch
                {
                    ILocalSymbol local => local.RefKind == RefKind.None,
                    IParameterSymbol parameter => parameter.RefKind == RefKind.None,
                    _ => false
                };
            }
        }

        private static bool DerivesFrom(ITypeSymbol? type, string baseName)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (current.Name == baseName)
                    return true;
            return type?.AllInterfaces.Any(contract => contract.Name == baseName) == true;
        }

        private static bool InLoop(SyntaxNode node) => node.Ancestors()
            .TakeWhile(ancestor => ancestor is not (BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .Any(ancestor => ancestor is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax);

        private static string ShortReceiver(string receiver) => receiver.Split('.').Last();

        private static string MemberName(ISymbol symbol) =>
            symbol.ContainingType == null ? symbol.Name : $"{symbol.ContainingType.Name}.{symbol.Name}";

        public IEnumerable<FactsRuleSuggestion> Suggest(IEnumerable<IndexType> types)
        {
            var ownNames = types.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
            var callSuggestions = calls
                .Where(call => !extractors.ExcludedFromSuggestions(call.At))
                .Where(call => !CommonGenericCalls.Contains(call.Name) && call.TypeArguments.Any(ownNames.Contains))
                .GroupBy(call => call.Name)
                .Where(group => group.Count() >= SuggestionMinimumUses)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(group => new FactsRuleSuggestion("genericCalls", group.Key, group.Count(), group.First().At, Folders(group.Select(call => call.At))));
            var attributeSuggestions = attributes
                .Where(attribute => !extractors.ExcludedFromSuggestions(attribute.At))
                .GroupBy(attribute => attribute.Name)
                .Where(group => group.Count() >= SuggestionMinimumUses)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(group => new FactsRuleSuggestion("injectionAttributes", group.Key, group.Count(), group.First().At, Folders(group.Select(attribute => attribute.At))));
            return callSuggestions.Concat(attributeSuggestions).ToArray();
        }

        private static IReadOnlyList<string> Folders(IEnumerable<string> places) => places
            .Select(place => place[..Math.Max(0, place.LastIndexOf(':'))])
            .Select(file => string.Join("/", file.Split('/').SkipLast(1).Take(3)))
            .GroupBy(folder => folder)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(5)
            .Select(group => group.Key)
            .ToArray();

        private const int SuggestionMinimumUses = 10;

        private static string ShortTypeName(TypeSyntax type)
        {
            var text = type.ToString();
            var generic = text.IndexOf('<');
            if (generic >= 0)
                text = text[..generic];
            return text.Split('.').Last().TrimEnd('?', ']', '[');
        }

        // Attributes that say nothing about how types are wired; they are never suggested as project rules.
        private static readonly HashSet<string> CommonAttributes = new(StringComparer.Ordinal)
        {
            "Serializable", "NonSerialized", "Obsolete", "Header", "Tooltip", "Range", "Space", "Min", "Multiline", "TextArea",
            "HideInInspector", "ContextMenu", "ContextMenuItem", "MenuItem", "FormerlySerializedAs", "Test", "TestCase",
            "TestCaseSource", "SetUp", "TearDown", "OneTimeSetUp", "OneTimeTearDown", "UnityTest", "UnitySetUp", "UnityTearDown",
            "Category", "Values", "InitializeOnLoadMethod", "RuntimeInitializeOnLoadMethod", "DidReloadScripts", "MethodImpl",
            "DllImport", "Conditional", "DebuggerHidden", "DebuggerStepThrough", "DebuggerBrowsable", "CallerMemberName",
            "CallerFilePath", "CallerLineNumber", "NotNull", "CanBeNull", "MaybeNull", "NotNullWhen", "MaybeNullWhen", "Preserve",
            "UsedImplicitly", "SuppressMessage", "JsonProperty", "JsonIgnore", "ReadOnly", "BurstCompile", "Params", "In", "Out",
            "Optional", "DefaultValue", "Description", "Pure", "AssertionMethod", "CompilerGenerated", "EditorBrowsable"
        };

        // Generic calls of the base library that take any type and wire nothing.
        private static readonly HashSet<string> CommonGenericCalls = new(StringComparer.Ordinal)
        {
            "Empty", "Cast", "OfType", "Equals", "Parse", "TryParse", "Default", "SizeOf", "Unsafe", "As", "Repeat", "Range"
        };
    }

    // Snapshot: a string table followed by the records, written atomically so a reader never sees half a file.
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = new List<string>();
        int S(string? value)
        {
            value ??= string.Empty;
            if (!strings.TryGetValue(value, out var id))
            {
                id = table.Count;
                strings[value] = id;
                table.Add(value);
            }
            return id;
        }
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
        {
            void List(IReadOnlyCollection<string> values)
            {
                writer.Write(values.Count);
                foreach (var value in values)
                    writer.Write(S(value));
            }
            writer.Write(Files.Count);
            foreach (var file in Files)
            {
                writer.Write(S(file.Path));
                List(file.Namespaces);
            }
            writer.Write(Types.Count);
            foreach (var type in Types)
            {
                foreach (var value in new[] { type.Key, type.FullName, type.Name, type.Display, type.Namespace, type.FirstFile, type.Kind, type.Base })
                    writer.Write(S(value));
                writer.Write(type.ExtensionCandidate);
                writer.Write(type.UnityEntry);
                writer.Write(type.IsAbstract);
                writer.Write(type.IsGeneric);
                writer.Write(type.Placeable);
                writer.Write(type.IsSerializable);
                writer.Write(type.IsUnityObject);
                writer.Write(S(type.ByValueAt));
                writer.Write(S(type.CreatedAt));
                writer.Write(type.ReferenceFields.Count);
                foreach (var (field, element, at) in type.ReferenceFields)
                {
                    writer.Write(S(field));
                    writer.Write(S(element));
                    writer.Write(S(at));
                }
                List(type.Parts);
                List(type.Interfaces);
                List(type.DerivedBy.ToArray());
                List(type.NamedIn.ToArray());
                writer.Write(type.Members.Count);
                foreach (var member in type.Members)
                {
                    writer.Write(S(member.Kind));
                    writer.Write(S(member.Display));
                    writer.Write(S(member.At));
                    writer.Write(S(member.Name));
                    writer.Write(member.HasParameters);
                    writer.Write(member.Parameters.Count);
                    foreach (var parameter in member.Parameters)
                        writer.Write(S(parameter));
                }
                writer.Write(type.Extras.Count);
                foreach (var (kind, values) in type.Extras)
                {
                    writer.Write(S(kind));
                    writer.Write(values.Count);
                    foreach (var (value, at) in values)
                    {
                        writer.Write(S(value));
                        writer.Write(S(at));
                    }
                }
                writer.Write(type.Behaviour.Count);
                foreach (var (section, entries) in type.Behaviour)
                {
                    writer.Write(S(section));
                    writer.Write(entries.Count);
                    foreach (var (value, at) in entries)
                    {
                        writer.Write(S(value));
                        writer.Write(S(at));
                    }
                }
            }
            writer.Write(References.Count);
            foreach (var reference in References)
            {
                writer.Write(S(reference.SourceName));
                writer.Write(S(reference.TargetName));
                writer.Write(S(reference.SourceKey));
                writer.Write(S(reference.TargetKey));
                writer.Write(S(reference.File));
                writer.Write(reference.Line);
            }
            writer.Write(Suggestions.Count);
            foreach (var suggestion in Suggestions)
            {
                writer.Write(S(suggestion.Rule));
                writer.Write(S(suggestion.Name));
                writer.Write(suggestion.Uses);
                writer.Write(S(suggestion.Example));
                writer.Write(suggestion.Folders.Count);
                foreach (var folder in suggestion.Folders)
                    writer.Write(S(folder));
            }
            writer.Write(UnityReferenceCount);
            writer.Write(UnresolvedBaseTypes);
            writer.Write(ProjectReferencesCompiled);
            writer.Write(ProjectReferencesFromSources);
            writer.Write(MissingAssemblies.Count);
            foreach (var name in MissingAssemblies)
                writer.Write(S(name));
            writer.Write(UnseenBaseExamples.Count);
            foreach (var example in UnseenBaseExamples)
            {
                writer.Write(S(example.Type));
                writer.Write(S(example.Base));
                writer.Write(S(example.At));
                writer.Write(example.Count);
            }
            writer.Write(ReflectionSites.Count);
            foreach (var (baseKey, at) in ReflectionSites)
            {
                writer.Write(S(baseKey));
                writer.Write(S(at));
            }
        }

        var temporary = path + "." + Environment.ProcessId + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(file, Encoding.UTF8))
        {
            writer.Write("ArchitectorCodeIndex");
            writer.Write(FormatVersion);
            writer.Write(Signature);
            writer.Write(ReferenceSet);
            writer.Write(table.Count);
            foreach (var value in table)
                writer.Write(value);
            body.Position = 0;
            body.CopyTo(file);
        }
        File.Move(temporary, path, true);
    }

    // "fresh" when the saved index matches the code, "stale" when the code changed since, "missing" when none is saved.
    public static string SnapshotState(string root, string referenceSet)
    {
        var path = SnapshotPath(root, referenceSet);
        if (!File.Exists(path))
            return "missing";
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(file, Encoding.UTF8);
            if (reader.ReadString() != "ArchitectorCodeIndex" || reader.ReadInt32() != FormatVersion)
                return "stale";
            return reader.ReadString() == ComputeSignature(root) ? "fresh" : "stale";
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or FormatException)
        {
            return "stale";
        }
    }

    public static CodeIndex? TryLoad(string path, string signature)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var watch = Stopwatch.StartNew();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(file, Encoding.UTF8);
            if (reader.ReadString() != "ArchitectorCodeIndex" || reader.ReadInt32() != FormatVersion)
                return null;
            var index = new CodeIndex { Signature = reader.ReadString(), ReferenceSet = reader.ReadString(), FromDisk = true };
            if (index.Signature != signature)
                return null;
            var table = new string[reader.ReadInt32()];
            for (var position = 0; position < table.Length; position++)
                table[position] = reader.ReadString();
            string S() => table[reader.ReadInt32()];
            List<string> List()
            {
                var values = new List<string>(reader.ReadInt32());
                for (var position = values.Capacity; position > 0; position--)
                    values.Add(S());
                return values;
            }
            for (var count = reader.ReadInt32(); count > 0; count--)
                index.Files.Add(new IndexFile { Path = S(), Namespaces = List() });
            for (var count = reader.ReadInt32(); count > 0; count--)
            {
                var type = new IndexType
                {
                    Key = S(), FullName = S(), Name = S(), Display = S(), Namespace = S(), FirstFile = S(), Kind = S(), Base = S(),
                    ExtensionCandidate = reader.ReadBoolean(), UnityEntry = reader.ReadBoolean(),
                    IsAbstract = reader.ReadBoolean(), IsGeneric = reader.ReadBoolean(), Placeable = reader.ReadBoolean(),
                    IsSerializable = reader.ReadBoolean(), IsUnityObject = reader.ReadBoolean()
                };
                type.ByValueAt = S();
                type.CreatedAt = S();
                for (var fields = reader.ReadInt32(); fields > 0; fields--)
                    type.ReferenceFields.Add((S(), S(), S()));
                type.Parts.AddRange(List());
                type.Interfaces.AddRange(List());
                type.DerivedBy.UnionWith(List());
                type.NamedIn.UnionWith(List());
                for (var members = reader.ReadInt32(); members > 0; members--)
                {
                    var (kind, display, at, name, hasParameters) = (S(), S(), S(), S(), reader.ReadBoolean());
                    var parameters = new string[reader.ReadInt32()];
                    for (var slot = 0; slot < parameters.Length; slot++)
                        parameters[slot] = S();
                    type.Members.Add(new IndexMember(kind, display, at, name, parameters, hasParameters));
                }
                for (var kinds = reader.ReadInt32(); kinds > 0; kinds--)
                {
                    var kind = S();
                    for (var values = reader.ReadInt32(); values > 0; values--)
                        type.AddExtra(kind, S(), S());
                }
                for (var sections = reader.ReadInt32(); sections > 0; sections--)
                {
                    var section = S();
                    for (var entries = reader.ReadInt32(); entries > 0; entries--)
                        type.AddBehaviour(section, S(), S());
                }
                index.Types.Add(type);
            }
            for (var count = reader.ReadInt32(); count > 0; count--)
                index.References.Add(new IndexReference(S(), S(), S(), S(), S(), reader.ReadInt32()));
            for (var count = reader.ReadInt32(); count > 0; count--)
            {
                var (rule, name, uses, example) = (S(), S(), reader.ReadInt32(), S());
                var folders = new string[reader.ReadInt32()];
                for (var folder = 0; folder < folders.Length; folder++)
                    folders[folder] = S();
                index.Suggestions.Add(new FactsRuleSuggestion(rule, name, uses, example, folders));
            }
            index.UnityReferenceCount = reader.ReadInt32();
            index.UnresolvedBaseTypes = reader.ReadInt32();
            index.ProjectReferencesCompiled = reader.ReadInt32();
            index.ProjectReferencesFromSources = reader.ReadInt32();
            for (var count = reader.ReadInt32(); count > 0; count--)
                index.MissingAssemblies.Add(S());
            for (var count = reader.ReadInt32(); count > 0; count--)
                index.UnseenBaseExamples.Add((S(), S(), S(), reader.ReadInt32()));
            for (var count = reader.ReadInt32(); count > 0; count--)
                index.ReflectionSites.Add((S(), S()));
            index.BuildMilliseconds = watch.ElapsedMilliseconds;
            return index;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or IndexOutOfRangeException or FormatException)
        {
            return null;
        }
    }
}

public sealed class IndexFile
{
    public string Path = string.Empty;
    public List<string> Namespaces = new();
}

public sealed class IndexType
{
    public string Key = string.Empty;
    public string FullName = string.Empty;
    public string Name = string.Empty;
    public string Display = string.Empty;
    public string Namespace = string.Empty;
    public string FirstFile = string.Empty;
    public string Kind = "class";
    public string Base = string.Empty;
    public bool ExtensionCandidate;
    public bool IsAbstract;
    public bool IsGeneric;
    public bool Placeable;
    public bool UnityEntry;
    public bool IsSerializable;
    public bool IsUnityObject;
    // First place where Unity stores this type by value (a serialized field), and the first "new" of it; empty when none.
    public string ByValueAt = string.Empty;
    public string CreatedAt = string.Empty;
    // [SerializeReference] fields declared here: "Owner.field", the element type's key (empty when not a project type), file:line.
    public List<(string Field, string ElementKey, string At)> ReferenceFields { get; } = new();
    public List<string> Parts { get; } = new();
    public List<string> Interfaces { get; } = new();
    public HashSet<string> DerivedBy { get; } = new(StringComparer.Ordinal);
    // Files where this type's declaration contains at least one name; DependencyEvidence resolves the owner of such a type.
    public SortedSet<string> NamedIn { get; } = new(StringComparer.Ordinal);
    public List<IndexMember> Members { get; } = new();
    public SortedDictionary<string, SortedDictionary<string, string>> Extras { get; } = new(StringComparer.Ordinal);
    // Behaviour facts by section (reads, writes, uses, subscribes, unsubscribes, assigns, lifecycle), every occurrence.
    public SortedDictionary<string, List<(string Value, string At)>> Behaviour { get; } = new(StringComparer.Ordinal);

    public void AddBehaviour(string section, string value, string at)
    {
        if (!Behaviour.TryGetValue(section, out var entries))
            Behaviour[section] = entries = new List<(string Value, string At)>();
        entries.Add((value, at));
    }

    public void AddExtra(string kind, string value, string at)
    {
        if (!Extras.TryGetValue(kind, out var values))
            Extras[kind] = values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        values.TryAdd(value, at);
    }
}

// Kind, Name and Parameters form the member reference plans accept ({kind, name, parameters}): the kinds and names
// Architector's type index uses (".ctor" for constructors, "op_…" for operators, "Item" for indexers) and the short
// metadata names of the parameter types ("Single", "List", "ref:Clock", "Int32[]", "!0").
public sealed record IndexMember(string Kind, string Display, string At, string Name, IReadOnlyList<string> Parameters, bool HasParameters);

public sealed record IndexReference(string SourceName, string TargetName, string SourceKey, string TargetKey, string File, int Line);
