using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Assembly = System.Reflection.Assembly;

namespace Architector.Editor
{
    // Where the reflection facts find their code: the compiled assemblies and the source files of each. The Unity Editor
    // gives them from CompilationPipeline (ArchitectorUnityTypeSources); a test can give any loaded assembly.
    public interface IArchitectorTypeSources : IDisposable
    {
        event Action CodeChanged;

        // Called on the main thread. The sources in the scope and the loaded assemblies by name.
        ArchitectorTypeSourceSet Collect(IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots);
    }

    public sealed class ArchitectorTypeSourceSet
    {
        public ArchitectorTypeSourceSet(IReadOnlyList<ArchitectorTypeIndexSource> sources, IReadOnlyDictionary<string, Assembly> assemblies)
        {
            Sources = sources ?? Array.Empty<ArchitectorTypeIndexSource>();
            Assemblies = assemblies ?? new Dictionary<string, Assembly>(StringComparer.Ordinal);
        }

        public IReadOnlyList<ArchitectorTypeIndexSource> Sources { get; }
        public IReadOnlyDictionary<string, Assembly> Assemblies { get; }
    }

    // Type facts by reflection over compiled assemblies: the Unity Editor's facts. The declarations and keys written here
    // are the contract that the command line's Roslyn facts repeat.
    public sealed class ArchitectorReflectionTypeFacts : IArchitectorTypeFacts
    {
        private readonly IArchitectorTypeSources sources;

        public ArchitectorReflectionTypeFacts(IArchitectorTypeSources sources)
        {
            this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
            sources.CodeChanged += OnCodeChanged;
        }

        public event Action CodeChanged;

        public ArchitectorTypeFactsBuild Start(IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots) =>
            new Build(sources, includedRoots, excludedRoots);

        public void Dispose()
        {
            sources.CodeChanged -= OnCodeChanged;
            sources.Dispose();
        }

        private void OnCodeChanged() => CodeChanged?.Invoke();

        private sealed class Build : ArchitectorTypeFactsBuild
        {
            private readonly IArchitectorTypeSources sources;
            private readonly IReadOnlyList<string> includedRoots;
            private readonly IReadOnlyList<string> excludedRoots;
            private ArchitectorTypeIndexBuild running;

            public Build(IArchitectorTypeSources sources, IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots)
            {
                this.sources = sources;
                this.includedRoots = includedRoots;
                this.excludedRoots = excludedRoots;
            }

            public override IEnumerator<float> Run(bool blocking)
            {
                var set = sources.Collect(includedRoots, excludedRoots);
                yield return 0.2f;
                var sourceAssemblies = set.Sources.Select(source => source.AssemblyName)
                    .Distinct(StringComparer.Ordinal)
                    .Where(name => set.Assemblies.ContainsKey(name))
                    .Select(name => set.Assemblies[name])
                    .ToArray();
                running = new ArchitectorTypeIndexBuild(
                    sourceAssemblies.Length + set.Sources.Count,
                    current => BuildOnWorkers(set.Sources, sourceAssemblies, current));
                try
                {
                    if (blocking)
                        running.Wait();
                    while (!running.IsCompleted)
                    {
                        IsWaitingForWorkers = true;
                        yield return 0.2f + 0.75f * running.Progress;
                    }
                    IsWaitingForWorkers = false;
                    var result = running.Result;
                    Result = new Snapshot(result.Types, result.LoadedTypes, set.Assemblies);
                }
                finally
                {
                    IsWaitingForWorkers = false;
                }
            }

            public override void Cancel() => running?.Cancel();
        }

        private sealed class Snapshot : IArchitectorTypeSnapshot
        {
            // Types of the assemblies that hold indexed sources; types of other assemblies are looked up on first use and
            // kept here, misses as null.
            private readonly Dictionary<string, Type> compiledTypes;
            private readonly IReadOnlyDictionary<string, Assembly> compiledAssemblies;

            public Snapshot(IReadOnlyList<ArchitectorIndexedType> types, Dictionary<string, Type> loadedTypes, IReadOnlyDictionary<string, Assembly> assemblies)
            {
                Types = types;
                compiledTypes = loadedTypes;
                compiledAssemblies = assemblies;
            }

            public IReadOnlyList<ArchitectorIndexedType> Types { get; }

            public ArchitectorIndexedTypeReference DirectBase(string typeKey) =>
                TryGetLoadedType(typeKey, out var type) ? CreateReference(type.BaseType) : null;

            public IReadOnlyList<ArchitectorIndexedTypeReference> Interfaces(string typeKey)
            {
                if (!TryGetLoadedType(typeKey, out var type))
                    return Array.Empty<ArchitectorIndexedTypeReference>();
                return type.GetInterfaces()
                    .Select(CreateReference)
                    .Where(reference => reference != null)
                    .OrderBy(reference => reference.FullDisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(reference => reference.AssemblyName, StringComparer.Ordinal)
                    .ToArray();
            }

            public IReadOnlyList<ArchitectorIndexedMember> Members(string typeKey, ISet<string> projectTypeKeys, ArchitectorOptimizationSettings optimization) =>
                TryGetLoadedType(typeKey, out var type) ? BuildMembers(type, projectTypeKeys, optimization) : null;

            public IReadOnlyList<ArchitectorIndexedMember> OriginMembers(string typeKey, string declaringIdentity)
            {
                if (!TryGetLoadedType(typeKey, out var rootType))
                    return Array.Empty<ArchitectorIndexedMember>();
                var origin = MemberOrigins(rootType).FirstOrDefault(candidate => string.Equals(
                    ArchitectorTypeIdentity.Key(candidate.type.Assembly.GetName().Name, candidate.type.FullName),
                    declaringIdentity,
                    StringComparison.Ordinal));
                if (origin.type == null)
                    return Array.Empty<ArchitectorIndexedMember>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                           | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                return origin.type.GetMembers(flags)
                    .Where(IsSupportedVisibleMember)
                    .Where(member => origin.parentDepth == 0 || member is not ConstructorInfo)
                    .Select(member => CreateMember(member, origin.parentDepth, origin.isInterface, false))
                    .Where(member => member != null)
                    .ToArray();
            }

            private bool TryGetLoadedType(string key, out Type type)
            {
                if (compiledTypes.TryGetValue(key, out type))
                    return type != null;
                var separator = key.IndexOf(':');
                if (separator > 0 && compiledAssemblies.TryGetValue(key.Substring(0, separator), out var assembly))
                {
                    type = assembly.GetType(key.Substring(separator + 1), false);
                    if (type != null && (type.IsGenericParameter || IsCompilerGenerated(type)))
                        type = null;
                }
                compiledTypes[key] = type;
                return type != null;
            }
        }

        private static ArchitectorIndexedTypeReference CreateReference(Type type)
        {
            if (type == null || type.FullName == null || !TryGetKind(type, out var kind))
                return null;
            return new ArchitectorIndexedTypeReference
            {
                AssemblyName = type.Assembly.GetName().Name,
                MetadataName = type.FullName,
                DisplayName = RemoveGenericArity(type.Name),
                Namespace = type.Namespace ?? string.Empty,
                Kind = kind
            };
        }

        private static ArchitectorTypeIndexBuildResult BuildOnWorkers(
            IReadOnlyList<ArchitectorTypeIndexSource> sources,
            IReadOnlyList<Assembly> sourceAssemblies,
            ArchitectorTypeIndexBuild build)
        {
            var loadedTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
            var assemblyNames = new Dictionary<Assembly, string>();
            foreach (var assembly in sourceAssemblies)
            {
                build.Token.ThrowIfCancellationRequested();
                var assemblyName = assembly.GetName().Name;
                assemblyNames[assembly] = assemblyName;
                foreach (var type in GetLoadableTypes(assembly))
                {
                    if (type != null && !type.IsGenericParameter && !IsCompilerGenerated(type))
                        loadedTypes[ArchitectorTypeIdentity.Key(assemblyName, type.FullName)] = type;
                }
                build.Advance();
            }

            var scanned = new IReadOnlyList<ArchitectorCSharpTypeDeclaration>[sources.Count];
            var options = new ParallelOptions
            {
                CancellationToken = build.Token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };
            Parallel.For(0, sources.Count, options, index =>
            {
                var path = sources[index].Path;
                scanned[index] = System.IO.File.Exists(path)
                    ? ArchitectorCSharpTypeDeclarationScanner.ScanFile(path)
                    : Array.Empty<ArchitectorCSharpTypeDeclaration>();
                build.Advance();
            });

            var declarationsByKey = new Dictionary<string, List<ArchitectorSourceDeclarationRecord>>(StringComparer.Ordinal);
            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                foreach (var declaration in scanned[index])
                {
                    var key = ArchitectorTypeIdentity.Key(source.AssemblyName, declaration.MetadataName);
                    if (!loadedTypes.ContainsKey(key))
                        continue;
                    if (!declarationsByKey.TryGetValue(key, out var records))
                    {
                        records = new List<ArchitectorSourceDeclarationRecord>();
                        declarationsByKey[key] = records;
                    }
                    if (records.Any(record => string.Equals(record.repositoryPath, source.Path, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    records.Add(new ArchitectorSourceDeclarationRecord
                    {
                        scriptGuid = source.Guid,
                        repositoryPath = source.Path,
                        line = declaration.Line,
                        column = declaration.Column
                    });
                }
            }
            build.Token.ThrowIfCancellationRequested();

            var types = declarationsByKey.Select(pair =>
                {
                    var type = loadedTypes[pair.Key];
                    return TryGetKind(type, out var kind)
                        ? CreateRecord(type, assemblyNames[type.Assembly], kind, pair.Value)
                        : null;
                })
                .Where(type => type != null)
                .OrderBy(type => type.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(type => type.Namespace, StringComparer.OrdinalIgnoreCase)
                .ThenBy(type => type.AssemblyName, StringComparer.Ordinal)
                .ToArray();
            return new ArchitectorTypeIndexBuildResult(types, loadedTypes);
        }

        private static IReadOnlyList<ArchitectorIndexedMember> BuildMembers(
            Type type,
            ISet<string> projectTypeKeys,
            ArchitectorOptimizationSettings optimization)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                       | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            optimization ??= new ArchitectorOptimizationSettings();
            var maximumParentDepth = optimization.memberParentDepth < 0
                ? int.MaxValue
                : optimization.memberParentDepth;
            var origins = new List<(Type type, int parentDepth, bool isInterface)> { (type, 0, false) };
            var parent = type.BaseType;
            var depth = 1;
            while (parent != null && depth <= maximumParentDepth)
            {
                origins.Add((parent, depth, false));
                parent = parent.BaseType;
                depth++;
            }
            if (optimization.includeInterfaceMembers)
            {
                origins.AddRange(type.GetInterfaces()
                    .OrderBy(FullTypeName, StringComparer.Ordinal)
                    .Select(interfaceType => (interfaceType, 0, true)));
            }

            var members = origins.SelectMany(origin => origin.type.GetMembers(flags)
                    .Where(member => IsSupportedVisibleMember(member)
                                     && (origin.parentDepth == 0 || member is not ConstructorInfo))
                    .Select(member => CreateMember(
                        member,
                        origin.parentDepth,
                        origin.isInterface,
                        !projectTypeKeys.Contains(ArchitectorTypeIdentity.Key(
                            origin.type.Assembly.GetName().Name,
                            origin.type.FullName)))))
                .Where(member => member != null)
                .Where(member => optimization.includeFrameworkMembers || !member.IsFrameworkMember)
                .GroupBy(member => member.SymbolKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(member => ArchitectorMemberOrder.Rank(member.Kind))
                .ThenBy(member => member.Declaration, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.SymbolKey, StringComparer.Ordinal)
                .ToArray();
            return members;
        }

        private static IEnumerable<(Type type, int parentDepth, bool isInterface)> MemberOrigins(Type type)
        {
            yield return (type, 0, false);
            var parent = type.BaseType;
            var depth = 1;
            while (parent != null)
            {
                yield return (parent, depth, false);
                parent = parent.BaseType;
                depth++;
            }
            foreach (var interfaceType in type.GetInterfaces().OrderBy(FullTypeName, StringComparer.Ordinal))
                yield return (interfaceType, 0, true);
        }

        private static bool IsSupportedVisibleMember(MemberInfo member)
        {
            return member switch
            {
                FieldInfo field => IsVisible(field),
                PropertyInfo property => property.GetAccessors(true).Any(IsVisible),
                EventInfo eventInfo => IsVisible(eventInfo.AddMethod) || IsVisible(eventInfo.RemoveMethod),
                ConstructorInfo constructor => IsVisible(constructor),
                MethodInfo method => IsVisible(method) && (!method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal)),
                _ => false
            };
        }

        private static bool IsVisible(FieldInfo field)
        {
            return field != null && (field.IsPublic || field.IsAssembly || field.IsFamily || field.IsFamilyOrAssembly || field.IsFamilyAndAssembly);
        }

        private static bool IsVisible(MethodBase method)
        {
            return method != null && (method.IsPublic || method.IsAssembly || method.IsFamily || method.IsFamilyOrAssembly || method.IsFamilyAndAssembly);
        }

        private static ArchitectorIndexedMember CreateMember(
            MemberInfo member,
            int parentDepth,
            bool isInterfaceMember,
            bool isFrameworkMember)
        {
            var declaringType = member.DeclaringType;
            if (declaringType == null)
                return null;
            var declaringIdentity = ArchitectorTypeIdentity.Key(declaringType.Assembly.GetName().Name, declaringType.FullName);
            string kind;
            string name;
            string parameterKey;
            string declaration;
            switch (member)
            {
                case FieldInfo field:
                    kind = field.IsLiteral ? "constant" : "field";
                    name = field.Name;
                    parameterKey = string.Empty;
                    declaration = FormatField(field);
                    break;
                case PropertyInfo property:
                    kind = property.GetIndexParameters().Length > 0 ? "indexer" : "property";
                    name = property.Name;
                    parameterKey = ParameterKey(property.GetIndexParameters());
                    declaration = FormatProperty(property);
                    break;
                case EventInfo eventInfo:
                    kind = "event";
                    name = eventInfo.Name;
                    parameterKey = string.Empty;
                    var eventAccessor = eventInfo.AddMethod ?? eventInfo.RemoveMethod;
                    declaration = $"{Accessibility(eventAccessor)}{MethodModifiers(eventAccessor)} event {TypeName(eventInfo.EventHandlerType)} {eventInfo.Name}";
                    break;
                case ConstructorInfo constructor:
                    kind = "constructor";
                    name = ".ctor";
                    parameterKey = ParameterKey(constructor.GetParameters());
                    declaration = $"{Accessibility(constructor)} {RemoveGenericArity(declaringType.Name)}({FormatParameters(constructor.GetParameters())})";
                    break;
                case MethodInfo method:
                    kind = method.Name.StartsWith("op_", StringComparison.Ordinal) ? "operator" : "method";
                    name = method.Name;
                    parameterKey = $"{(method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0)}|{ParameterKey(method.GetParameters())}";
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

        private static string FormatField(FieldInfo field)
        {
            var staticText = field.IsStatic && !field.IsLiteral ? " static" : string.Empty;
            var constText = field.IsLiteral ? " const" : field.IsInitOnly ? " readonly" : string.Empty;
            return $"{Accessibility(field)}{staticText}{constText} {TypeName(field.FieldType)} {field.Name}";
        }

        private static string FormatProperty(PropertyInfo property)
        {
            var accessors = property.GetAccessors(true);
            var accessibility = Accessibility(accessors.OrderByDescending(AccessibilityRank).FirstOrDefault());
            var modifiers = MethodModifiers(accessors.OrderByDescending(AccessibilityRank).FirstOrDefault());
            var name = property.GetIndexParameters().Length == 0
                ? property.Name
                : $"this[{FormatParameters(property.GetIndexParameters())}]";
            var access = $"{{ {FormatAccessor(property.GetGetMethod(true), "get", accessibility)}{FormatAccessor(property.GetSetMethod(true), "set", accessibility)}}}";
            return $"{accessibility}{modifiers} {TypeName(property.PropertyType)} {name} {access}";
        }

        private static string FormatAccessor(MethodInfo accessor, string keyword, string propertyAccessibility)
        {
            if (accessor == null) return string.Empty;
            var accessibility = Accessibility(accessor);
            return (accessibility == propertyAccessibility ? string.Empty : accessibility + " ") + keyword + "; ";
        }

        private static string MethodModifiers(MethodInfo method)
        {
            if (method == null) return string.Empty;
            if (method.IsStatic) return " static";
            // Interface implementation methods can be virtual/final in IL without a C# modifier.
            if (method.DeclaringType?.IsInterface == true) return string.Empty;
            var overrides = method.IsVirtual && method.GetBaseDefinition().DeclaringType != method.DeclaringType;
            if (overrides)
                return method.IsAbstract ? " abstract override" : method.IsFinal ? " sealed override" : " override";
            if (method.IsAbstract) return " abstract";
            return method.IsVirtual && !method.IsFinal ? " virtual" : string.Empty;
        }

        private static string FormatMethod(MethodInfo method, bool isOperator)
        {
            var modifiers = MethodModifiers(method);
            var generic = method.IsGenericMethodDefinition
                ? $"<{string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name))}>"
                : string.Empty;
            var name = isOperator ? OperatorName(method.Name) : method.Name;
            return $"{Accessibility(method)}{modifiers} {TypeName(method.ReturnType)} {name}{generic}({FormatParameters(method.GetParameters())})";
        }

        private static string FormatParameters(IEnumerable<ParameterInfo> parameters)
        {
            return string.Join(", ", parameters.Select(parameter =>
            {
                var prefix = parameter.IsDefined(typeof(ParamArrayAttribute), false) ? "params "
                    : parameter.IsOut ? "out " : parameter.IsIn && parameter.ParameterType.IsByRef ? "in "
                    : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
                var parameterType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                var defaultValue = parameter.HasDefaultValue ? " = " + FormatDefaultValue(parameterType, parameter.DefaultValue) : string.Empty;
                return $"{prefix}{TypeName(parameterType)} {parameter.Name}{defaultValue}";
            }));
        }

        private static string FormatDefaultValue(Type type, object value)
        {
            if (value == null) return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? "default" : "null";
            if (value is bool boolean) return boolean ? "true" : "false";
            if (value is string text) return "\"" + EscapeLiteral(text) + "\"";
            if (value is char character) return "'" + EscapeLiteral(character.ToString()).Replace("'", "\\'") + "'";
            if (type.IsEnum)
            {
                var name = Enum.GetName(type, value);
                return name == null ? $"({TypeName(type)}){Convert.ToString(value, CultureInfo.InvariantCulture)}" : $"{TypeName(type)}.{name}";
            }
            return value switch
            {
                float number => number.ToString("R", CultureInfo.InvariantCulture) + "f",
                double number => number.ToString("R", CultureInfo.InvariantCulture) + "d",
                decimal number => number.ToString(CultureInfo.InvariantCulture) + "m",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            };
        }

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

        private static string ParameterKey(IEnumerable<ParameterInfo> parameters)
        {
            return string.Join(",", parameters.Select(parameter =>
            {
                var prefix = parameter.IsOut ? "out:" : parameter.ParameterType.IsByRef ? "ref:" : parameter.IsIn ? "in:" : string.Empty;
                var parameterType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                return prefix + TypeIdentity(parameterType);
            }));
        }

        private static string TypeIdentity(Type type)
        {
            if (type == null) return string.Empty;
            if (type.IsArray) return TypeIdentity(type.GetElementType()) + $"[{new string(',', type.GetArrayRank() - 1)}]";
            if (type.IsGenericParameter)
                return $"{(type.DeclaringMethod == null ? "!" : "!!")}{type.GenericParameterPosition}";
            if (type.IsGenericType)
                return $"{type.GetGenericTypeDefinition().FullName}[{string.Join(",", type.GetGenericArguments().Select(TypeIdentity))}]";
            return type.FullName ?? type.Name;
        }

        private static string TypeName(Type type)
        {
            if (type == null) return "void";
            if (type.IsArray) return TypeName(type.GetElementType()) + $"[{new string(',', type.GetArrayRank() - 1)}]";
            if (type.IsGenericParameter) return type.Name;
            if (type.IsGenericType)
                return $"{RemoveGenericArity(type.Name)}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
            return type == typeof(void) ? "void" : type.Name;
        }

        private static string FullTypeName(Type type)
        {
            return (type.FullName ?? type.Name).Replace('+', '.');
        }

        private static string Accessibility(FieldInfo field)
        {
            if (field.IsPublic) return "public";
            if (field.IsFamilyOrAssembly) return "protected internal";
            if (field.IsFamilyAndAssembly) return "private protected";
            if (field.IsFamily) return "protected";
            return field.IsAssembly ? "internal" : "private";
        }

        private static string Accessibility(MethodBase method)
        {
            if (method == null) return "public";
            if (method.IsPublic) return "public";
            if (method.IsFamilyOrAssembly) return "protected internal";
            if (method.IsFamilyAndAssembly) return "private protected";
            if (method.IsFamily) return "protected";
            return method.IsAssembly ? "internal" : "private";
        }

        private static int AccessibilityRank(MethodBase method)
        {
            if (method == null) return 0;
            if (method.IsPublic) return 6;
            if (method.IsFamilyOrAssembly) return 5;
            if (method.IsFamily) return 4;
            if (method.IsAssembly) return 3;
            if (method.IsFamilyAndAssembly) return 2;
            return 1;
        }

        private static string OperatorName(string metadataName)
        {
            return metadataName switch
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
        }

        private static ArchitectorIndexedType CreateRecord(
            Type type,
            string assemblyName,
            string kind,
            IReadOnlyList<ArchitectorSourceDeclarationRecord> sourceDeclarations)
        {
            var genericParameters = type.IsGenericTypeDefinition
                ? type.GetGenericArguments().Select(argument => argument.Name).ToArray()
                : Array.Empty<string>();
            return new ArchitectorIndexedType
            {
                AssemblyName = assemblyName,
                MetadataName = type.FullName,
                DisplayName = RemoveGenericArity(type.Name),
                Namespace = type.Namespace ?? string.Empty,
                Kind = kind,
                GenericParameters = genericParameters,
                Modifiers = TypeModifiers(type),
                SourceDeclarations = sourceDeclarations
                    .OrderBy(source => source.repositoryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(source => source.line)
                    .ThenBy(source => source.column)
                    .ToArray()
            };
        }

        private static IEnumerable<Type> GetLoadableTypes(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
        }

        private static bool TryGetKind(Type type, out string kind)
        {
            if (type.IsInterface) kind = "interface";
            else if (type.IsEnum) kind = "enum";
            else if (typeof(MulticastDelegate).IsAssignableFrom(type.BaseType)) kind = "delegate";
            else if (type.IsValueType) kind = "struct";
            else if (type.IsClass) kind = "class";
            else kind = string.Empty;
            return kind.Length > 0;
        }

        private static IReadOnlyList<string> TypeModifiers(Type type)
        {
            var result = new List<string>();
            if (type.IsPublic || type.IsNestedPublic) result.Add("public");
            else if (type.IsNestedFamily) result.Add("protected");
            else if (type.IsNestedFamORAssem) result.Add("protected internal");
            else if (type.IsNestedFamANDAssem) result.Add("private protected");
            else if (type.IsNestedPrivate) result.Add("private");
            else result.Add("internal");
            if (type.IsAbstract && type.IsSealed) result.Add("static");
            else
            {
                if (type.IsAbstract && !type.IsInterface) result.Add("abstract");
                if (type.IsSealed && !type.IsValueType) result.Add("sealed");
            }
            return result;
        }

        private static bool IsCompilerGenerated(Type type)
        {
            return type.Name.StartsWith("<", StringComparison.Ordinal)
                   || type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length > 0;
        }

        private static string RemoveGenericArity(string name)
        {
            var marker = name.IndexOf('`');
            return marker < 0 ? name : name.Substring(0, marker);
        }
    }
}
