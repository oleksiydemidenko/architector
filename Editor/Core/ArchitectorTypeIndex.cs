using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;

namespace Architector.Editor
{
    public enum ArchitectorTypeResolution
    {
        Resolved,
        OutOfScope,
        Unresolved
    }

    public enum ArchitectorMemberResolution
    {
        Resolved,
        Missing,
        SignatureChanged
    }

    public sealed class ArchitectorIndexedMember
    {
        public string SymbolKey { get; set; } = string.Empty;
        public string LooseKey { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Declaration { get; set; } = string.Empty;
        public string DeclaringTypeName { get; set; } = string.Empty;
        public int ParentDepth { get; set; }
        public bool IsInterfaceMember { get; set; }
        public bool IsFrameworkMember { get; set; }
    }

    public sealed class ArchitectorIndexedType
    {
        public string AssemblyName { get; set; } = string.Empty;
        public string MetadataName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public IReadOnlyList<string> GenericParameters { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Modifiers { get; set; } = Array.Empty<string>();
        public IReadOnlyList<ArchitectorSourceDeclarationRecord> SourceDeclarations { get; set; } = Array.Empty<ArchitectorSourceDeclarationRecord>();

        public string Key => ArchitectorTypeIdentity.Key(AssemblyName, MetadataName);
        public string FullDisplayName => string.IsNullOrEmpty(Namespace) ? DisplayName : $"{Namespace}.{DisplayName}";
    }

    public sealed class ArchitectorIndexedTypeReference
    {
        public string AssemblyName { get; set; } = string.Empty;
        public string MetadataName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Key => ArchitectorTypeIdentity.Key(AssemblyName, MetadataName);
        public string FullDisplayName => string.IsNullOrEmpty(Namespace) ? DisplayName : $"{Namespace}.{DisplayName}";

        public ArchitectorTypeReferenceRecord ToRecord()
        {
            return new ArchitectorTypeReferenceRecord
            {
                mode = "linked",
                kind = Kind,
                typeNamespace = Namespace,
                name = DisplayName,
                assemblyName = AssemblyName,
                metadataName = MetadataName
            };
        }
    }

    public static class ArchitectorTypeIdentity
    {
        public static string Key(string assemblyName, string metadataName)
        {
            return $"{assemblyName ?? string.Empty}:{metadataName ?? string.Empty}";
        }
    }

    public sealed class ArchitectorTypeIndex : IDisposable
    {
        private readonly ArchitectorRepository repository;
        private IReadOnlyList<ArchitectorIndexedType> types = Array.Empty<ArchitectorIndexedType>();
        private IReadOnlyDictionary<string, Type> compiledTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<ArchitectorIndexedMember>> membersByTypeKey = new(StringComparer.Ordinal);
        private string sourceSettingsIdentity;
        private string memberSettingsIdentity;
        private bool dirty = true;

        public int Generation { get; private set; }

        public ArchitectorTypeIndex(ArchitectorRepository repository)
        {
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            sourceSettingsIdentity = SourceSettingsIdentity();
            memberSettingsIdentity = MemberSettingsIdentity();
            repository.Changed += OnRepositoryChanged;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public IReadOnlyList<ArchitectorIndexedType> Types
        {
            get
            {
                if (dirty)
                    Rebuild();
                return types;
            }
        }

        public ArchitectorTypeResolution Resolve(ArchitectorTypeRecord linkedType)
        {
            if (linkedType == null || !string.Equals(linkedType.mode, "linked", StringComparison.Ordinal))
                return ArchitectorTypeResolution.Unresolved;
            var settings = repository.Settings.sourceIndex;
            var includedRoots = NormalizeRoots(settings.includedRoots);
            var excludedRoots = NormalizeRoots(settings.excludedRoots);
            if (linkedType.sourceDeclarations != null
                && linkedType.sourceDeclarations.Count > 0
                && !linkedType.sourceDeclarations.Any(declaration =>
                    declaration != null && IsInScope(NormalizePath(declaration.repositoryPath), includedRoots, excludedRoots)))
                return ArchitectorTypeResolution.OutOfScope;
            return Find(linkedType) != null
                ? ArchitectorTypeResolution.Resolved
                : ArchitectorTypeResolution.Unresolved;
        }

        public ArchitectorIndexedType Find(ArchitectorTypeRecord linkedType)
        {
            if (linkedType == null || !string.Equals(linkedType.mode, "linked", StringComparison.Ordinal))
                return null;
            var key = ArchitectorTypeIdentity.Key(linkedType.assemblyName, linkedType.metadataName);
            return Types.FirstOrDefault(type => string.Equals(type.Key, key, StringComparison.Ordinal));
        }

        public ArchitectorIndexedTypeReference GetDirectBase(ArchitectorTypeRecord linkedType)
        {
            return TryGetCompiledType(linkedType, out var type) ? CreateReference(type.BaseType) : null;
        }

        public IReadOnlyList<ArchitectorIndexedTypeReference> GetInterfaces(ArchitectorTypeRecord linkedType)
        {
            if (!TryGetCompiledType(linkedType, out var type))
                return Array.Empty<ArchitectorIndexedTypeReference>();
            return type.GetInterfaces()
                .Select(CreateReference)
                .Where(reference => reference != null)
                .OrderBy(reference => reference.FullDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.AssemblyName, StringComparer.Ordinal)
                .ToArray();
        }

        public bool HasDirectBase(ArchitectorTypeRecord linkedType, ArchitectorTypeReferenceRecord expected)
        {
            var actual = GetDirectBase(linkedType);
            return Matches(actual, expected);
        }

        public bool ImplementsInterface(ArchitectorTypeRecord linkedType, ArchitectorTypeReferenceRecord expected)
        {
            return GetInterfaces(linkedType).Any(actual => Matches(actual, expected));
        }

        public IReadOnlyList<ArchitectorIndexedMember> GetMembers(ArchitectorTypeRecord linkedType)
        {
            if (linkedType == null || !string.Equals(linkedType.mode, "linked", StringComparison.Ordinal))
                return Array.Empty<ArchitectorIndexedMember>();
            _ = Types;
            var key = ArchitectorTypeIdentity.Key(linkedType.assemblyName, linkedType.metadataName);
            if (membersByTypeKey.TryGetValue(key, out var cached))
                return cached;
            if (!compiledTypes.TryGetValue(key, out var type))
                return Array.Empty<ArchitectorIndexedMember>();
            var projectTypeKeys = new HashSet<string>(types.Select(candidate => candidate.Key), StringComparer.Ordinal);
            var indexed = BuildMembers(type, projectTypeKeys, repository.Settings.optimization);
            membersByTypeKey[key] = indexed;
            return indexed;
        }

        public ArchitectorMemberResolution ResolveMember(
            ArchitectorTypeRecord linkedType,
            ArchitectorMemberRecord linkedMember)
        {
            if (linkedMember == null || string.IsNullOrEmpty(linkedMember.symbolKey))
                return ArchitectorMemberResolution.Missing;
            var members = GetMembers(linkedType);
            var exact = members.FirstOrDefault(member => string.Equals(member.SymbolKey, linkedMember.symbolKey, StringComparison.Ordinal));
            if (exact == null)
                exact = FindSelectedMember(linkedType, linkedMember.symbolKey, true).SingleOrDefault();
            if (exact != null)
                return string.Equals(exact.Declaration, linkedMember.declaration, StringComparison.Ordinal)
                    ? ArchitectorMemberResolution.Resolved
                    : ArchitectorMemberResolution.SignatureChanged;
            var looseKey = LooseKeyFromSymbolKey(linkedMember.symbolKey);
            var looseMatches = members.Where(member => string.Equals(member.LooseKey, looseKey, StringComparison.Ordinal))
                .Concat(FindSelectedMember(linkedType, looseKey, false))
                .GroupBy(member => member.SymbolKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            return looseKey.Length > 0 && looseMatches.Length == 1
                ? ArchitectorMemberResolution.SignatureChanged
                : ArchitectorMemberResolution.Missing;
        }

        public void Dispose()
        {
            repository.Changed -= OnRepositoryChanged;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
        }

        public void Invalidate()
        {
            sourceSettingsIdentity = SourceSettingsIdentity();
            memberSettingsIdentity = MemberSettingsIdentity();
            dirty = true;
            membersByTypeKey.Clear();
            Generation++;
        }

        private void OnRepositoryChanged()
        {
            var nextSourceIdentity = SourceSettingsIdentity();
            var nextMemberIdentity = MemberSettingsIdentity();
            if (!string.Equals(sourceSettingsIdentity, nextSourceIdentity, StringComparison.Ordinal))
            {
                sourceSettingsIdentity = nextSourceIdentity;
                memberSettingsIdentity = nextMemberIdentity;
                dirty = true;
                membersByTypeKey.Clear();
                Generation++;
                return;
            }
            if (string.Equals(memberSettingsIdentity, nextMemberIdentity, StringComparison.Ordinal))
                return;
            memberSettingsIdentity = nextMemberIdentity;
            membersByTypeKey.Clear();
            Generation++;
        }

        private void OnCompilationFinished(object _)
        {
            Invalidate();
        }

        private bool TryGetCompiledType(ArchitectorTypeRecord linkedType, out Type type)
        {
            _ = Types;
            var key = ArchitectorTypeIdentity.Key(linkedType?.assemblyName, linkedType?.metadataName);
            return compiledTypes.TryGetValue(key, out type);
        }

        private static bool Matches(ArchitectorIndexedTypeReference actual, ArchitectorTypeReferenceRecord expected)
        {
            if (actual == null || expected == null)
                return false;
            if (string.Equals(expected.mode, "linked", StringComparison.Ordinal))
                return string.Equals(actual.Key, ArchitectorTypeReference.Key(expected), StringComparison.Ordinal);
            return string.Equals(actual.FullDisplayName, ArchitectorTypeReference.FullName(expected), StringComparison.Ordinal);
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

        private void Rebuild()
        {
            var settings = repository.Settings.sourceIndex;
            var includedRoots = NormalizeRoots(settings.includedRoots);
            var excludedRoots = NormalizeRoots(settings.excludedRoots);
            var compilationAssemblies = CompilationPipeline.GetAssemblies();
            var compiledAssemblies = new HashSet<string>(compilationAssemblies.Select(assembly => assembly.name), StringComparer.Ordinal);
            var sourceFilesByAssembly = compilationAssemblies.ToDictionary(
                assembly => assembly.name,
                assembly => new HashSet<string>(assembly.sourceFiles.Select(ProjectRelativePath), StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);
            var loadedTypes = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => compiledAssemblies.Contains(assembly.GetName().Name))
                .SelectMany(GetLoadableTypes)
                .Where(type => type != null && !type.IsGenericParameter && !IsCompilerGenerated(type))
                .ToDictionary(
                    type => ArchitectorTypeIdentity.Key(type.Assembly.GetName().Name, type.FullName),
                    type => type,
                    StringComparer.Ordinal);
            compiledTypes = loadedTypes;

            var declarationsByKey = new Dictionary<string, List<ArchitectorSourceDeclarationRecord>>(StringComparer.Ordinal);
            foreach (var compilationAssembly in compilationAssemblies)
            {
                if (!sourceFilesByAssembly.TryGetValue(compilationAssembly.name, out var assemblySources))
                    continue;
                foreach (var path in assemblySources.Where(candidate => IsInScope(candidate, includedRoots, excludedRoots)))
                {
                    if (!System.IO.File.Exists(path))
                        continue;
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    var declarations = ArchitectorCSharpTypeDeclarationScanner.ScanFile(path);
                    foreach (var declaration in declarations)
                    {
                        var key = ArchitectorTypeIdentity.Key(compilationAssembly.name, declaration.MetadataName);
                        if (!loadedTypes.ContainsKey(key))
                            continue;
                        if (!declarationsByKey.TryGetValue(key, out var sources))
                        {
                            sources = new List<ArchitectorSourceDeclarationRecord>();
                            declarationsByKey[key] = sources;
                        }
                        if (sources.Any(source => string.Equals(source.repositoryPath, path, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        sources.Add(new ArchitectorSourceDeclarationRecord
                        {
                            scriptGuid = guid,
                            repositoryPath = path,
                            line = declaration.Line,
                            column = declaration.Column
                        });
                    }
                }
            }

            types = declarationsByKey.Select(pair =>
                {
                    var type = loadedTypes[pair.Key];
                    return TryGetKind(type, out var kind)
                        ? CreateRecord(type, kind, pair.Value)
                        : null;
                })
                .Where(type => type != null)
                .OrderBy(type => type.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(type => type.Namespace, StringComparer.OrdinalIgnoreCase)
                .ThenBy(type => type.AssemblyName, StringComparer.Ordinal)
                .ToArray();
            dirty = false;
        }

        private static IReadOnlyList<ArchitectorIndexedMember> BuildMembers(
            Type type,
            HashSet<string> projectTypeKeys,
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

        private IReadOnlyList<ArchitectorIndexedMember> FindSelectedMember(
            ArchitectorTypeRecord linkedType,
            string memberKey,
            bool exact)
        {
            var keyParts = (memberKey ?? string.Empty).Split('|');
            if (keyParts.Length < 3)
                return Array.Empty<ArchitectorIndexedMember>();
            var rootKey = ArchitectorTypeIdentity.Key(linkedType.assemblyName, linkedType.metadataName);
            if (!compiledTypes.TryGetValue(rootKey, out var rootType))
                return Array.Empty<ArchitectorIndexedMember>();
            var declaringIdentity = keyParts[1];
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
                .Where(member => member != null && string.Equals(
                    exact ? member.SymbolKey : member.LooseKey,
                    memberKey,
                    StringComparison.Ordinal))
                .ToArray();
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

        private static string LooseKeyFromSymbolKey(string symbolKey)
        {
            if (string.IsNullOrEmpty(symbolKey))
                return string.Empty;
            var separators = 0;
            for (var index = 0; index < symbolKey.Length; index++)
            {
                if (symbolKey[index] != '|')
                    continue;
                separators++;
                if (separators == 3)
                    return symbolKey.Substring(0, index);
            }
            return string.Empty;
        }

        private static ArchitectorIndexedType CreateRecord(
            Type type,
            string kind,
            IReadOnlyList<ArchitectorSourceDeclarationRecord> sourceDeclarations)
        {
            var genericParameters = type.IsGenericTypeDefinition
                ? type.GetGenericArguments().Select(argument => argument.Name).ToArray()
                : Array.Empty<string>();
            return new ArchitectorIndexedType
            {
                AssemblyName = type.Assembly.GetName().Name,
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

        private string SourceSettingsIdentity()
        {
            var settings = repository.Settings.sourceIndex;
            return $"{SettingsListIdentity(settings.includedRoots)}\n{SettingsListIdentity(settings.excludedRoots)}";
        }

        private string MemberSettingsIdentity()
        {
            var settings = repository.Settings.optimization ?? new ArchitectorOptimizationSettings();
            return $"{settings.memberParentDepth}|{settings.includeInterfaceMembers}|{settings.includeFrameworkMembers}";
        }

        private static string SettingsListIdentity(IEnumerable<string> values)
        {
            return string.Join("\u001f", (values ?? Array.Empty<string>()).Select(value => value ?? string.Empty));
        }

        private static string[] NormalizeRoots(IEnumerable<string> roots)
        {
            return (roots ?? Array.Empty<string>())
                .Select(NormalizePath)
                .Where(root => root.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsInScope(string path, IEnumerable<string> includedRoots, IEnumerable<string> excludedRoots)
        {
            return includedRoots.Any(root => IsAtOrBelow(path, root))
                   && !excludedRoots.Any(root => IsAtOrBelow(path, root));
        }

        private static bool IsAtOrBelow(string path, string root)
        {
            return path.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }

        private static string ProjectRelativePath(string path)
        {
            var relative = FileUtil.GetProjectRelativePath(path);
            return NormalizePath(string.IsNullOrEmpty(relative) ? path : relative);
        }
    }
}
