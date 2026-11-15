using Architector.CodeFacts;
using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Architector.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// The type facts of the command line: the project's C# sources compiled with Roslyn, read as the Unity Editor reads its
// compiled assemblies by reflection (ArchitectorReflectionTypeFacts). Keys, kinds, modifiers and declarations follow the
// reflection rules: CLR type names (Int32, not int), metadata names with ` and +, the Mono runtime's assembly names in
// generic arguments (mscorlib for the core library). Known limit: members that only the Editor's Mono class library has
// (internal members of System types) are not seen here.
internal sealed class McpTypeFacts : IArchitectorTypeFacts
{
    private readonly string root;

    public McpTypeFacts(string root)
    {
        this.root = Path.GetFullPath(root);
    }

    public event Action? CodeChanged
    {
        add { }
        remove { }
    }

    public ArchitectorTypeFactsBuild Start(IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots) =>
        new Build(root, includedRoots, excludedRoots);

    public void Dispose()
    {
    }

    private sealed class Build : ArchitectorTypeFactsBuild
    {
        private readonly string root;
        private readonly IReadOnlyList<string> includedRoots;
        private readonly IReadOnlyList<string> excludedRoots;

        public Build(string root, IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots)
        {
            this.root = root;
            this.includedRoots = includedRoots;
            this.excludedRoots = excludedRoots;
        }

        public override IEnumerator<float> Run(bool blocking)
        {
            Result = Snapshot.Create(root, includedRoots, excludedRoots);
            yield return 1f;
        }
    }

    internal sealed class Snapshot : IArchitectorTypeSnapshot
    {
        private readonly McpProjectAssemblies assemblies;
        private readonly Dictionary<string, INamedTypeSymbol> projectTypes;
        private readonly ConcurrentDictionary<string, INamedTypeSymbol?> lookedUp = new(StringComparer.Ordinal);
        private readonly Compilation compilation;

        private Snapshot(Compilation compilation, McpProjectAssemblies assemblies, IReadOnlyList<ArchitectorIndexedType> types,
            Dictionary<string, INamedTypeSymbol> projectTypes)
        {
            this.compilation = compilation;
            this.assemblies = assemblies;
            Types = types;
            this.projectTypes = projectTypes;
        }

        public IReadOnlyList<ArchitectorIndexedType> Types { get; }

        public static Snapshot Create(string root, IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots)
        {
            var assemblies = McpProjectAssemblies.Read(root);
            var files = assemblies.SourceFiles
                .Concat(ScopeFiles(root, includedRoots, excludedRoots))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var options = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: assemblies.Defines);
            var trees = files.AsParallel().AsOrdered()
                .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)), options, path))
                .ToArray();
            var platform = Basic.Reference.Assemblies.Net100.References.All.Cast<MetadataReference>().ToList();
            var unity = CodeIndex.UnityReferences(assemblies.ReferenceRoot, platform).References;
            var compilation = CSharpCompilation.Create(
                "Architector.TypeFacts",
                trees,
                platform.Concat(unity),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
                    .WithMetadataImportOptions(MetadataImportOptions.All));

            var projectTypes = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            var declarations = new Dictionary<string, List<ArchitectorSourceDeclarationRecord>>(StringComparer.Ordinal);
            var host = new ArchitectorProcessHost(root);
            foreach (var tree in trees)
            {
                var path = tree.FilePath;
                if (!ArchitectorTypeIndex.IsInScope(path, includedRoots, excludedRoots))
                    continue;
                var assembly = assemblies.AssemblyOf(path);
                var declared = DeclaredTypes(compilation.GetSemanticModel(tree), tree);
                string? guid = null;
                foreach (var declaration in ArchitectorCSharpTypeDeclarationScanner.Scan(tree.GetText().ToString()))
                {
                    if (!declared.TryGetValue(declaration.MetadataName, out var symbol) || IsCompilerGenerated(symbol))
                        continue;
                    var key = ArchitectorTypeIdentity.Key(assembly, declaration.MetadataName);
                    projectTypes[key] = symbol;
                    if (!declarations.TryGetValue(key, out var records))
                        declarations[key] = records = new List<ArchitectorSourceDeclarationRecord>();
                    if (records.Any(record => string.Equals(record.repositoryPath, path, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    records.Add(new ArchitectorSourceDeclarationRecord
                    {
                        scriptGuid = guid ??= host.AssetGuid(path),
                        repositoryPath = path,
                        line = declaration.Line,
                        column = declaration.Column
                    });
                }
            }

            var snapshot = new Snapshot(compilation, assemblies, Array.Empty<ArchitectorIndexedType>(), projectTypes);
            var types = declarations
                .Select(pair => snapshot.CreateRecord(projectTypes[pair.Key], pair.Key, pair.Value))
                .Where(type => type != null)
                .Select(type => type!)
                .OrderBy(type => type.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(type => type.Namespace, StringComparer.OrdinalIgnoreCase)
                .ThenBy(type => type.AssemblyName, StringComparer.Ordinal)
                .ToArray();
            return new Snapshot(compilation, assemblies, types, projectTypes);
        }

        private static IEnumerable<string> ScopeFiles(string root, IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots)
        {
            foreach (var scope in includedRoots)
            {
                var folder = Path.Combine(root, scope);
                if (File.Exists(folder) && folder.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    yield return ArchitectorTypeIndex.NormalizePath(scope);
                    continue;
                }
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
                {
                    var path = ArchitectorTypeIndex.NormalizePath(Path.GetRelativePath(root, file));
                    if (path.Split('/').Any(part => part.EndsWith('~') || part.StartsWith('.')))
                        continue;
                    if (ArchitectorTypeIndex.IsInScope(path, includedRoots, excludedRoots))
                        yield return path;
                }
            }
        }

        // Every type a file declares, nested ones too, by its reflection full name ("Space.Outer+Inner`1").
        private static Dictionary<string, INamedTypeSymbol> DeclaredTypes(SemanticModel model, SyntaxTree tree)
        {
            var result = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is not (Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.DelegateDeclarationSyntax))
                    continue;
                if (model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol)
                    result.TryAdd(MetadataFullName(symbol), symbol);
            }
            return result;
        }

        public ArchitectorIndexedTypeReference? DirectBase(string typeKey) =>
            Find(typeKey) is { } type ? CreateReference(type.BaseType) : null;

        public IReadOnlyList<ArchitectorIndexedTypeReference> Interfaces(string typeKey)
        {
            if (Find(typeKey) is not { } type)
                return Array.Empty<ArchitectorIndexedTypeReference>();
            return type.AllInterfaces
                .Select(CreateReference)
                .Where(reference => reference != null)
                .Select(reference => reference!)
                .OrderBy(reference => reference.FullDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.AssemblyName, StringComparer.Ordinal)
                .ToArray();
        }

        public IReadOnlyList<ArchitectorIndexedMember>? Members(string typeKey, ISet<string> projectTypeKeys, ArchitectorOptimizationSettings optimization)
        {
            if (Find(typeKey) is not { } type)
                return null;
            optimization ??= new ArchitectorOptimizationSettings();
            var maximumParentDepth = optimization.memberParentDepth < 0 ? int.MaxValue : optimization.memberParentDepth;
            var origins = new List<(INamedTypeSymbol type, int parentDepth, bool isInterface)> { (type, 0, false) };
            var parent = type.BaseType;
            var depth = 1;
            while (parent != null && depth <= maximumParentDepth)
            {
                origins.Add((parent, depth, false));
                parent = parent.BaseType;
                depth++;
            }
            if (optimization.includeInterfaceMembers)
                origins.AddRange(type.AllInterfaces.OrderBy(FullTypeName, StringComparer.Ordinal).Select(item => (item, 0, true)));

            return origins.SelectMany(origin => VisibleMembers(origin.type, origin.parentDepth)
                    .Select(member => CreateMember(member, origin.type, origin.parentDepth, origin.isInterface,
                        !projectTypeKeys.Contains(ArchitectorTypeIdentity.Key(AssemblyName(origin.type), FullName(origin.type))))))
                .Where(member => member != null)
                .Select(member => member!)
                .Where(member => optimization.includeFrameworkMembers || !member.IsFrameworkMember)
                .GroupBy(member => member.SymbolKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(member => ArchitectorMemberOrder.Rank(member.Kind))
                .ThenBy(member => member.Declaration, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.SymbolKey, StringComparer.Ordinal)
                .ToArray();
        }

        public IReadOnlyList<ArchitectorIndexedMember> OriginMembers(string typeKey, string declaringIdentity)
        {
            if (Find(typeKey) is not { } root)
                return Array.Empty<ArchitectorIndexedMember>();
            var origins = new List<(INamedTypeSymbol type, int parentDepth, bool isInterface)> { (root, 0, false) };
            var depth = 1;
            for (var parent = root.BaseType; parent != null; parent = parent.BaseType)
                origins.Add((parent, depth++, false));
            origins.AddRange(root.AllInterfaces.OrderBy(FullTypeName, StringComparer.Ordinal).Select(item => (item, 0, true)));
            var origin = origins.FirstOrDefault(candidate => string.Equals(
                ArchitectorTypeIdentity.Key(AssemblyName(candidate.type), FullName(candidate.type)), declaringIdentity, StringComparison.Ordinal));
            if (origin.type == null)
                return Array.Empty<ArchitectorIndexedMember>();
            return VisibleMembers(origin.type, origin.parentDepth)
                .Select(member => CreateMember(member, origin.type, origin.parentDepth, origin.isInterface, false))
                .Where(member => member != null)
                .Select(member => member!)
                .ToArray();
        }

        private INamedTypeSymbol? Find(string key)
        {
            if (projectTypes.TryGetValue(key, out var type))
                return type;
            return lookedUp.GetOrAdd(key, candidate =>
            {
                var separator = candidate.IndexOf(':');
                if (separator <= 0)
                    return null;
                var metadataName = candidate.Substring(separator + 1);
                return compilation.GetTypeByMetadataName(metadataName);
            });
        }

        // Members as Type.GetMembers(DeclaredOnly, all bindings) gives them, kept when reflection keeps them.
        private static IEnumerable<object> VisibleMembers(INamedTypeSymbol type, int parentDepth)
        {
            foreach (var member in type.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol field when IsVisible(field.DeclaredAccessibility) && !field.IsImplicitlyDeclared:
                        yield return field;
                        break;
                    case IPropertySymbol property when Accessors(property).Any(accessor => IsVisible(accessor.DeclaredAccessibility)):
                        yield return property;
                        break;
                    case IEventSymbol eventSymbol when IsVisible(eventSymbol.AddMethod?.DeclaredAccessibility) || IsVisible(eventSymbol.RemoveMethod?.DeclaredAccessibility):
                        yield return eventSymbol;
                        break;
                    case IMethodSymbol method when IsVisible(method.DeclaredAccessibility) && IsReflectedMethod(method, type, parentDepth):
                        yield return method;
                        break;
                }
            }
            // The runtime field of every enum.
            if (type.TypeKind == TypeKind.Enum && type.EnumUnderlyingType != null)
                yield return new EnumValueField(type);
        }

        private static bool IsReflectedMethod(IMethodSymbol method, INamedTypeSymbol type, int parentDepth)
        {
            switch (method.MethodKind)
            {
                case MethodKind.Constructor:
                    if (parentDepth > 0)
                        return false;
                    // A struct or an enum has no parameterless constructor in metadata unless it declares one.
                    return !(method.IsImplicitlyDeclared && type.IsValueType && method.Parameters.Length == 0);
                case MethodKind.Ordinary:
                case MethodKind.UserDefinedOperator:
                case MethodKind.Conversion:
                case MethodKind.Destructor:
                case MethodKind.DelegateInvoke:
                    return !(method.IsPartialDefinition && method.PartialImplementationPart == null);
                default:
                    return false;
            }
        }

        private static IEnumerable<IMethodSymbol> Accessors(IPropertySymbol property)
        {
            if (property.GetMethod != null)
                yield return property.GetMethod;
            if (property.SetMethod != null)
                yield return property.SetMethod;
        }

        private ArchitectorIndexedMember? CreateMember(object member, INamedTypeSymbol declaringType, int parentDepth, bool isInterfaceMember, bool isFrameworkMember)
        {
            var declaringIdentity = ArchitectorTypeIdentity.Key(AssemblyName(declaringType), FullName(declaringType));
            string kind, name, parameterKey, declaration;
            switch (member)
            {
                case EnumValueField enumField:
                    kind = "field";
                    name = "value__";
                    parameterKey = string.Empty;
                    declaration = $"public {TypeName(enumField.Underlying)} value__";
                    break;
                case IFieldSymbol field:
                    kind = field.IsConst ? "constant" : "field";
                    name = field.MetadataName;
                    parameterKey = string.Empty;
                    declaration = FormatField(field);
                    break;
                case IPropertySymbol property:
                    kind = property.IsIndexer ? "indexer" : "property";
                    name = property.MetadataName;
                    parameterKey = ParameterKey(property.Parameters);
                    declaration = FormatProperty(property);
                    break;
                case IEventSymbol eventSymbol:
                    kind = "event";
                    name = eventSymbol.MetadataName;
                    parameterKey = string.Empty;
                    var accessor = eventSymbol.AddMethod ?? eventSymbol.RemoveMethod;
                    declaration = $"{Accessibility(accessor?.DeclaredAccessibility)}{MethodModifiers(accessor)} event {TypeName(eventSymbol.Type)} {eventSymbol.MetadataName}";
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                    kind = "constructor";
                    name = ".ctor";
                    parameterKey = ParameterKey(constructor.Parameters);
                    declaration = $"{Accessibility(constructor.DeclaredAccessibility)} {RemoveGenericArity(declaringType.MetadataName)}({FormatParameters(constructor.Parameters)})";
                    break;
                case IMethodSymbol method:
                    name = method.MetadataName;
                    kind = name.StartsWith("op_", StringComparison.Ordinal) ? "operator" : "method";
                    parameterKey = $"{method.TypeParameters.Length}|{ParameterKey(method.Parameters)}";
                    declaration = FormatMethod(method, kind == "operator");
                    break;
                default:
                    return null;
            }
            var looseKey = $"{kind}|{declaringIdentity}|{name}";
            return new ArchitectorIndexedMember
            {
                SymbolKey = $"{looseKey}|{parameterKey}",
                LooseKey = looseKey,
                Kind = kind,
                Declaration = declaration,
                DeclaringTypeName = FullTypeName(declaringType),
                ParentDepth = parentDepth,
                IsInterfaceMember = isInterfaceMember,
                IsFrameworkMember = isFrameworkMember
            };
        }

        private string FormatField(IFieldSymbol field)
        {
            var staticText = field.IsStatic && !field.IsConst ? " static" : string.Empty;
            var constText = field.IsConst ? " const" : field.IsReadOnly ? " readonly" : string.Empty;
            return $"{Accessibility(field.DeclaredAccessibility)}{staticText}{constText} {TypeName(field.Type)} {field.MetadataName}";
        }

        private string FormatProperty(IPropertySymbol property)
        {
            var main = Accessors(property).OrderByDescending(accessor => AccessibilityRank(accessor.DeclaredAccessibility)).FirstOrDefault();
            var accessibility = Accessibility(main?.DeclaredAccessibility);
            var modifiers = MethodModifiers(main);
            var name = property.IsIndexer ? $"this[{FormatParameters(property.Parameters)}]" : property.MetadataName;
            var access = $"{{ {FormatAccessor(property.GetMethod, "get", accessibility)}{FormatAccessor(property.SetMethod, "set", accessibility)}}}";
            return $"{accessibility}{modifiers} {TypeName(property.Type)}{(property.ReturnsByRef || property.ReturnsByRefReadonly ? "&" : string.Empty)} {name} {access}";
        }

        private static string FormatAccessor(IMethodSymbol? accessor, string keyword, string propertyAccessibility)
        {
            if (accessor == null)
                return string.Empty;
            var accessibility = Accessibility(accessor.DeclaredAccessibility);
            return (accessibility == propertyAccessibility ? string.Empty : accessibility + " ") + keyword + "; ";
        }

        private static string MethodModifiers(IMethodSymbol? method)
        {
            if (method == null)
                return string.Empty;
            if (method.IsStatic)
                return " static";
            if (method.ContainingType?.TypeKind == TypeKind.Interface)
                return string.Empty;
            // A destructor is Finalize overriding Object.Finalize in metadata.
            if (method.IsOverride || method.MethodKind == MethodKind.Destructor)
                return method.IsAbstract ? " abstract override" : method.IsSealed ? " sealed override" : " override";
            if (method.IsAbstract)
                return " abstract";
            // A delegate's runtime methods are virtual in metadata.
            if (method.IsVirtual || method.ContainingType?.TypeKind == TypeKind.Delegate && method.MethodKind != MethodKind.Constructor)
                return " virtual";
            return string.Empty;
        }

        private string FormatMethod(IMethodSymbol method, bool isOperator)
        {
            var modifiers = MethodModifiers(method);
            var generic = method.IsGenericMethod ? $"<{string.Join(", ", method.TypeParameters.Select(parameter => parameter.Name))}>" : string.Empty;
            var name = isOperator ? OperatorName(method.MetadataName) : method.MetadataName;
            var returnType = method.ReturnsVoid ? "void" : TypeName(method.ReturnType) + (method.ReturnsByRef || method.ReturnsByRefReadonly ? "&" : string.Empty);
            return $"{Accessibility(method.DeclaredAccessibility)}{modifiers} {returnType} {name}{generic}({FormatParameters(method.Parameters)})";
        }

        private string FormatParameters(IEnumerable<IParameterSymbol> parameters)
        {
            return string.Join(", ", parameters.Select(parameter =>
            {
                var prefix = parameter.IsParams ? "params "
                    : parameter.RefKind == RefKind.Out ? "out "
                    : parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter ? "in "
                    : parameter.RefKind == RefKind.Ref ? "ref " : string.Empty;
                var defaultValue = parameter.HasExplicitDefaultValue ? " = " + FormatDefaultValue(parameter.Type, parameter.ExplicitDefaultValue) : string.Empty;
                return $"{prefix}{TypeName(parameter.Type)} {parameter.Name}{defaultValue}";
            }));
        }

        private string FormatDefaultValue(ITypeSymbol type, object? value)
        {
            if (value == null)
                return type.IsValueType && !IsNullable(type) ? "default" : "null";
            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                var member = enumType.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, value));
                return member == null
                    ? $"({TypeName(type)}){Convert.ToString(value, CultureInfo.InvariantCulture)}"
                    : $"{TypeName(type)}.{member.MetadataName}";
            }
            return value switch
            {
                bool boolean => boolean ? "true" : "false",
                string text => "\"" + EscapeLiteral(text) + "\"",
                char character => "'" + EscapeLiteral(character.ToString()).Replace("'", "\\'") + "'",
                float number => number.ToString("R", CultureInfo.InvariantCulture) + "f",
                double number => number.ToString("R", CultureInfo.InvariantCulture) + "d",
                decimal number => number.ToString(CultureInfo.InvariantCulture) + "m",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        private static bool IsNullable(ITypeSymbol type) =>
            type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        private static string EscapeLiteral(string value)
        {
            return string.Concat(value.Select(character => character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => char.IsControl(character) ? "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture) : character.ToString()
            }));
        }

        private string ParameterKey(IEnumerable<IParameterSymbol> parameters)
        {
            return string.Join(",", parameters.Select(parameter =>
            {
                var prefix = parameter.RefKind == RefKind.Out ? "out:" : parameter.RefKind != RefKind.None ? "ref:" : string.Empty;
                return prefix + TypeIdentity(parameter.Type);
            }));
        }

        // Type.Name as reflection writes it in a declaration: CLR names, generic arguments in <>.
        private string TypeName(ITypeSymbol? type)
        {
            switch (type)
            {
                case null:
                    return "void";
                case IArrayTypeSymbol array:
                    return TypeName(array.ElementType) + $"[{new string(',', array.Rank - 1)}]";
                case ITypeParameterSymbol parameter:
                    return parameter.Name;
                case IPointerTypeSymbol pointer:
                    return TypeName(pointer.PointedAtType) + "*";
                case IDynamicTypeSymbol:
                    return "Object";
                case INamedTypeSymbol named:
                    named = Underlying(named);
                    if (named.SpecialType == SpecialType.System_Void)
                        return "void";
                    var arguments = AllTypeArguments(named);
                    return arguments.Count > 0
                        ? $"{RemoveGenericArity(named.MetadataName)}<{string.Join(", ", arguments.Select(TypeName))}>"
                        : named.MetadataName;
                default:
                    return type.MetadataName;
            }
        }

        // The identity of a parameter type in a member key: full names, !n for a type's and !!n for a method's parameter.
        private string TypeIdentity(ITypeSymbol? type)
        {
            switch (type)
            {
                case null:
                    return string.Empty;
                case IArrayTypeSymbol array:
                    return TypeIdentity(array.ElementType) + $"[{new string(',', array.Rank - 1)}]";
                case ITypeParameterSymbol parameter:
                    return parameter.TypeParameterKind == TypeParameterKind.Method
                        ? $"!!{parameter.Ordinal}"
                        : $"!{parameter.Ordinal + OuterTypeParameterCount(parameter.DeclaringType)}";
                case IPointerTypeSymbol pointer:
                    return TypeIdentity(pointer.PointedAtType) + "*";
                case IDynamicTypeSymbol:
                    return "System.Object";
                case INamedTypeSymbol named:
                    named = Underlying(named);
                    var arguments = AllTypeArguments(named);
                    return arguments.Count > 0
                        ? $"{MetadataFullName(named.OriginalDefinition)}[{string.Join(",", arguments.Select(TypeIdentity))}]"
                        : MetadataFullName(named);
                default:
                    return type.MetadataName;
            }
        }

        private static int OuterTypeParameterCount(INamedTypeSymbol? type)
        {
            var count = 0;
            for (var outer = type?.ContainingType; outer != null; outer = outer.ContainingType)
                count += outer.TypeParameters.Length;
            return count;
        }

        private static INamedTypeSymbol Underlying(INamedTypeSymbol type) =>
            type.IsTupleType && type.TupleUnderlyingType != null ? type.TupleUnderlyingType : type;

        // Reflection's generic arguments: those of the containing types first, then the type's own.
        private static List<ITypeSymbol> AllTypeArguments(INamedTypeSymbol type)
        {
            var result = new List<ITypeSymbol>();
            if (type.ContainingType != null)
                result.AddRange(AllTypeArguments(type.ContainingType));
            result.AddRange(type.TypeArguments);
            return result;
        }

        // Type.FullName: null for a type with an open generic argument; generic arguments assembly-qualified.
        private string? FullName(INamedTypeSymbol type)
        {
            type = Underlying(type);
            var arguments = AllTypeArguments(type);
            if (arguments.Count == 0 || SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition))
                return MetadataFullName(type.OriginalDefinition);
            var qualified = new List<string>();
            foreach (var argument in arguments)
            {
                var name = ArgumentFullName(argument);
                if (name == null)
                    return null;
                qualified.Add($"[{name}, {AssemblyIdentity(argument)}]");
            }
            return $"{MetadataFullName(type.OriginalDefinition)}[{string.Join(",", qualified)}]";
        }

        private string? ArgumentFullName(ITypeSymbol type) => type switch
        {
            INamedTypeSymbol named => FullName(named),
            IArrayTypeSymbol array => ArgumentFullName(array.ElementType) is { } element ? element + $"[{new string(',', array.Rank - 1)}]" : null,
            _ => null
        };

        private string FullTypeName(INamedTypeSymbol type) => (FullName(type) ?? type.MetadataName).Replace('+', '.');

        internal static string MetadataFullName(INamedTypeSymbol type)
        {
            if (type.ContainingType != null)
                return MetadataFullName(type.ContainingType) + "+" + type.MetadataName;
            var space = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : string.Empty;
            return space + type.MetadataName;
        }

        private ArchitectorIndexedTypeReference? CreateReference(INamedTypeSymbol? type)
        {
            if (type == null || FullName(type) is not { } fullName || Kind(type) is not { } kind)
                return null;
            return new ArchitectorIndexedTypeReference
            {
                AssemblyName = AssemblyName(type),
                MetadataName = fullName,
                DisplayName = RemoveGenericArity(type.MetadataName),
                Namespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty,
                Kind = kind
            };
        }

        private ArchitectorIndexedType? CreateRecord(INamedTypeSymbol type, string key, IReadOnlyList<ArchitectorSourceDeclarationRecord> sources)
        {
            if (Kind(type) is not { } kind)
                return null;
            var separator = key.IndexOf(':');
            var allParameters = new List<string>();
            for (var outer = type; outer != null; outer = outer.ContainingType)
                allParameters.InsertRange(0, outer.TypeParameters.Select(parameter => parameter.Name));
            return new ArchitectorIndexedType
            {
                AssemblyName = key.Substring(0, separator),
                MetadataName = key.Substring(separator + 1),
                DisplayName = RemoveGenericArity(type.MetadataName),
                Namespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty,
                Kind = kind,
                GenericParameters = allParameters.ToArray(),
                Modifiers = TypeModifiers(type),
                SourceDeclarations = sources
                    .OrderBy(source => source.repositoryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(source => source.line)
                    .ThenBy(source => source.column)
                    .ToArray()
            };
        }

        private static string? Kind(INamedTypeSymbol type) => type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            TypeKind.Struct => "struct",
            TypeKind.Class => "class",
            _ => null
        };

        private static IReadOnlyList<string> TypeModifiers(INamedTypeSymbol type)
        {
            var result = new List<string> { type.ContainingType == null && type.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public ? "internal" : Accessibility(type.DeclaredAccessibility) };
            if (type.IsStatic)
                result.Add("static");
            else
            {
                if (type.IsAbstract && type.TypeKind != TypeKind.Interface)
                    result.Add("abstract");
                if ((type.IsSealed || type.TypeKind == TypeKind.Delegate) && !type.IsValueType)
                    result.Add("sealed");
            }
            return result;
        }

        private static bool IsCompilerGenerated(INamedTypeSymbol type) =>
            type.Name.StartsWith("<", StringComparison.Ordinal) || type.IsImplicitlyDeclared;

        // The assembly reflection names: the project assembly of a source file, the referenced assembly otherwise, and
        // for the .NET library the Mono assembly the Unity Editor loads (mscorlib for most).
        private string AssemblyName(INamedTypeSymbol type)
        {
            type = Underlying(type).OriginalDefinition;
            var source = type.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath;
            if (source != null)
                return assemblies.AssemblyOf(source);
            var identity = type.ContainingAssembly?.Identity;
            if (identity == null)
                return string.Empty;
            return IsPlatform(identity.Name) ? MonoLibrary(type) : identity.Name;
        }

        private string AssemblyIdentity(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
                return AssemblyIdentity(array.ElementType);
            if (type is not INamedTypeSymbol named)
                return "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
            var name = AssemblyName(named);
            var definition = Underlying(named).OriginalDefinition;
            var fromSource = definition.Locations.Any(location => location.IsInSource);
            var identity = definition.ContainingAssembly?.Identity;
            if (fromSource || identity == null)
                return $"{name}, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
            if (IsPlatform(identity.Name))
                return $"{name}, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
            var token = identity.PublicKeyToken.IsDefaultOrEmpty ? "null" : string.Concat(identity.PublicKeyToken.Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
            return $"{name}, Version={identity.Version}, Culture={(string.IsNullOrEmpty(identity.CultureName) ? "neutral" : identity.CultureName)}, PublicKeyToken={token}";
        }

        private static bool IsPlatform(string assembly) =>
            assembly is "mscorlib" or "netstandard" or "System" || assembly.StartsWith("System.", StringComparison.Ordinal)
            || assembly.StartsWith("Microsoft.", StringComparison.Ordinal);

        private static string MonoLibrary(INamedTypeSymbol type)
        {
            var space = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var name = type.MetadataName;
            if (space == "System.Collections.Generic" && name is "HashSet`1")
                return "System.Core";
            if (space == "System.Linq" || space.StartsWith("System.Linq.", StringComparison.Ordinal))
                return "System.Core";
            if (space == "System.Collections.Generic" && name is "LinkedList`1" or "LinkedListNode`1" or "Queue`1" or "Stack`1" or "SortedSet`1"
                    or "SortedDictionary`2" or "SortedList`2")
                return "System";
            return "mscorlib";
        }

        private static bool IsVisible(Microsoft.CodeAnalysis.Accessibility? accessibility) =>
            accessibility is Microsoft.CodeAnalysis.Accessibility.Public or Microsoft.CodeAnalysis.Accessibility.Internal
                or Microsoft.CodeAnalysis.Accessibility.Protected or Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal
                or Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal;

        private static string Accessibility(Microsoft.CodeAnalysis.Accessibility? accessibility) => accessibility switch
        {
            null => "public",
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
            Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            _ => "private"
        };

        private static int AccessibilityRank(Microsoft.CodeAnalysis.Accessibility accessibility) => accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => 6,
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => 5,
            Microsoft.CodeAnalysis.Accessibility.Protected => 4,
            Microsoft.CodeAnalysis.Accessibility.Internal => 3,
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => 2,
            _ => 1
        };

        private static string OperatorName(string metadataName) => metadataName switch
        {
            "op_Addition" => "operator +",
            "op_Subtraction" => "operator -",
            "op_Multiply" => "operator *",
            "op_Division" => "operator /",
            "op_Equality" => "operator ==",
            "op_Inequality" => "operator !=",
            "op_Implicit" => "implicit operator",
            "op_Explicit" => "explicit operator",
            _ => metadataName.Replace("op_", "operator ")
        };

        private static string RemoveGenericArity(string name)
        {
            var marker = name.IndexOf('`');
            return marker < 0 ? name : name.Substring(0, marker);
        }

        // value__, the instance field every enum has in metadata; Roslyn does not list it.
        private sealed class EnumValueField
        {
            public EnumValueField(INamedTypeSymbol type)
            {
                Underlying = type.EnumUnderlyingType!;
            }

            public INamedTypeSymbol Underlying { get; }
        }
    }
}
