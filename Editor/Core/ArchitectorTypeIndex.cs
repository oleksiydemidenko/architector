using System;
using System.Collections.Generic;
using System.Linq;

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

    // The code facts behind the type index: the types the project declares in the scope, their bases, interfaces and
    // members. The Unity Editor reads them from its compiled assemblies by reflection (ArchitectorReflectionTypeFacts with
    // the sources of CompilationPipeline); the command line reads them from the C# sources with Roslyn. Both give the same
    // keys and declarations.
    public interface IArchitectorTypeFacts : IDisposable
    {
        // The code changed (Unity: a compilation finished); the index rebuilds on its next use.
        event Action CodeChanged;

        ArchitectorTypeFactsBuild Start(IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots);
    }

    // One build of the facts. Run yields progress 0..1; work that must stay on the calling thread runs inside MoveNext,
    // other work may run on workers while the steps yield.
    public abstract class ArchitectorTypeFactsBuild
    {
        public bool IsWaitingForWorkers { get; protected set; }
        public IArchitectorTypeSnapshot Result { get; protected set; }

        public abstract IEnumerator<float> Run(bool blocking);

        public virtual void Cancel()
        {
        }
    }

    public interface IArchitectorTypeSnapshot
    {
        IReadOnlyList<ArchitectorIndexedType> Types { get; }

        ArchitectorIndexedTypeReference DirectBase(string typeKey);

        IReadOnlyList<ArchitectorIndexedTypeReference> Interfaces(string typeKey);

        // The visible members of the type and its origins, or null when the code has no such type.
        IReadOnlyList<ArchitectorIndexedMember> Members(string typeKey, ISet<string> projectTypeKeys, ArchitectorOptimizationSettings optimization);

        // Every visible member that one origin of the type (itself, a base class or an interface) declares.
        IReadOnlyList<ArchitectorIndexedMember> OriginMembers(string typeKey, string declaringIdentity);
    }

    public sealed class ArchitectorTypeIndex : IDisposable
    {
        // Which facts a new index reads. The Unity package reads its compiled assemblies; the command line sets Roslyn.
        public static Func<ArchitectorRepository, IArchitectorTypeFacts> CreateFacts { get; set; } = DefaultFacts;

#if UNITY_EDITOR
        private static IArchitectorTypeFacts DefaultFacts(ArchitectorRepository repository) =>
            new ArchitectorReflectionTypeFacts(new ArchitectorUnityTypeSources());
#else
        private static IArchitectorTypeFacts DefaultFacts(ArchitectorRepository repository) => new ArchitectorNoTypeFacts();
#endif

        private readonly ArchitectorRepository repository;
        private readonly IArchitectorTypeFacts facts;
        private IArchitectorTypeSnapshot snapshot = ArchitectorNoTypeFacts.Empty;
        private IReadOnlyList<ArchitectorIndexedType> types = Array.Empty<ArchitectorIndexedType>();
        private IReadOnlyDictionary<string, ArchitectorIndexedType> typesByKey = new Dictionary<string, ArchitectorIndexedType>(StringComparer.Ordinal);
        private HashSet<string> projectTypeKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<ArchitectorIndexedMember>> membersByTypeKey = new(StringComparer.Ordinal);
        private readonly List<ArchitectorTypeFactsBuild> runningBuilds = new();
        private string sourceSettingsIdentity;
        private string memberSettingsIdentity;
        private bool dirty = true;
        private int dirtyStamp;

        public int Generation { get; private set; }
        public bool IsBuilt => !dirty;
        // True while a stepped rebuild waits for its worker threads; the step that yields then has nothing to do.
        public bool IsWaitingForWorkers { get; private set; }

        public ArchitectorTypeIndex(ArchitectorRepository repository)
            : this(repository, CreateFacts(repository))
        {
        }

        public ArchitectorTypeIndex(ArchitectorRepository repository, IArchitectorTypeFacts facts)
        {
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            this.facts = facts ?? throw new ArgumentNullException(nameof(facts));
            sourceSettingsIdentity = SourceSettingsIdentity();
            memberSettingsIdentity = MemberSettingsIdentity();
            repository.Changed += OnRepositoryChanged;
            facts.CodeChanged += OnCodeChanged;
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

        // Rebuilds the index in steps for a loading bar, yielding progress 0..1. Only the Unity calls run on the main
        // thread; reflection and reading and parsing the sources run on worker threads while the steps yield.
        public IEnumerator<float> RebuildSteps() => BuildSteps(false);

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
            _ = Types;
            return typesByKey.TryGetValue(key, out var type) ? type : null;
        }

        public ArchitectorIndexedTypeReference GetDirectBase(ArchitectorTypeRecord linkedType)
        {
            _ = Types;
            return snapshot.DirectBase(KeyOf(linkedType));
        }

        public IReadOnlyList<ArchitectorIndexedTypeReference> GetInterfaces(ArchitectorTypeRecord linkedType)
        {
            _ = Types;
            return snapshot.Interfaces(KeyOf(linkedType)) ?? Array.Empty<ArchitectorIndexedTypeReference>();
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
            var indexed = snapshot.Members(key, projectTypeKeys, repository.Settings.optimization);
            if (indexed == null)
                return Array.Empty<ArchitectorIndexedMember>();
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
            facts.CodeChanged -= OnCodeChanged;
            CancelRunningBuilds();
            facts.Dispose();
        }

        public void Invalidate()
        {
            sourceSettingsIdentity = SourceSettingsIdentity();
            memberSettingsIdentity = MemberSettingsIdentity();
            MarkDirty();
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
                MarkDirty();
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

        private void OnCodeChanged()
        {
            CancelRunningBuilds();
            Invalidate();
        }

        private static string KeyOf(ArchitectorTypeRecord linkedType) =>
            ArchitectorTypeIdentity.Key(linkedType?.assemblyName, linkedType?.metadataName);

        private void MarkDirty()
        {
            dirty = true;
            dirtyStamp++;
        }

        private void CancelRunningBuilds()
        {
            foreach (var build in runningBuilds.ToArray())
                build.Cancel();
            runningBuilds.Clear();
        }

        private static bool Matches(ArchitectorIndexedTypeReference actual, ArchitectorTypeReferenceRecord expected)
        {
            if (actual == null || expected == null)
                return false;
            if (string.Equals(expected.mode, "linked", StringComparison.Ordinal))
                return string.Equals(actual.Key, ArchitectorTypeReference.Key(expected), StringComparison.Ordinal);
            return string.Equals(actual.FullDisplayName, ArchitectorTypeReference.FullName(expected), StringComparison.Ordinal);
        }

        private void Rebuild()
        {
            using var steps = BuildSteps(true);
            while (steps.MoveNext())
            {
            }
        }

        private IEnumerator<float> BuildSteps(bool blocking)
        {
            while (dirty)
            {
                var stamp = dirtyStamp;
                var settings = repository.Settings.sourceIndex;
                var build = facts.Start(NormalizeRoots(settings.includedRoots), NormalizeRoots(settings.excludedRoots));
                runningBuilds.Add(build);
                try
                {
                    using (var steps = build.Run(blocking))
                    {
                        while (steps.MoveNext())
                        {
                            IsWaitingForWorkers = build.IsWaitingForWorkers;
                            yield return steps.Current;
                        }
                    }
                    IsWaitingForWorkers = false;
                    if (!dirty)
                        yield break;
                    if (stamp != dirtyStamp)
                        continue;
                    snapshot = build.Result ?? ArchitectorNoTypeFacts.Empty;
                    types = snapshot.Types;
                    typesByKey = types.ToDictionary(type => type.Key, StringComparer.Ordinal);
                    projectTypeKeys = new HashSet<string>(types.Select(type => type.Key), StringComparer.Ordinal);
                    membersByTypeKey.Clear();
                    dirty = false;
                }
                finally
                {
                    IsWaitingForWorkers = false;
                    runningBuilds.Remove(build);
                    build.Cancel();
                }
                yield return 1f;
            }
        }

        private IReadOnlyList<ArchitectorIndexedMember> FindSelectedMember(
            ArchitectorTypeRecord linkedType,
            string memberKey,
            bool exact)
        {
            var keyParts = (memberKey ?? string.Empty).Split('|');
            if (keyParts.Length < 3)
                return Array.Empty<ArchitectorIndexedMember>();
            _ = Types;
            return (snapshot.OriginMembers(KeyOf(linkedType), keyParts[1]) ?? Array.Empty<ArchitectorIndexedMember>())
                .Where(member => string.Equals(exact ? member.SymbolKey : member.LooseKey, memberKey, StringComparison.Ordinal))
                .ToArray();
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

        public static string[] NormalizeRoots(IEnumerable<string> roots)
        {
            return (roots ?? Array.Empty<string>())
                .Select(NormalizePath)
                .Where(root => root.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static bool IsInScope(string path, IEnumerable<string> includedRoots, IEnumerable<string> excludedRoots)
        {
            return includedRoots.Any(root => IsAtOrBelow(path, root))
                   && !excludedRoots.Any(root => IsAtOrBelow(path, root));
        }

        private static bool IsAtOrBelow(string path, string root)
        {
            return path.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizePath(string path)
        {
            return (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }
    }

    // No code facts: an index without types, for a host that has no code to read.
    public sealed class ArchitectorNoTypeFacts : IArchitectorTypeFacts
    {
        public static readonly IArchitectorTypeSnapshot Empty = new EmptySnapshot();

        public event Action CodeChanged
        {
            add { }
            remove { }
        }

        public ArchitectorTypeFactsBuild Start(IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots) => new EmptyBuild();

        public void Dispose()
        {
        }

        private sealed class EmptyBuild : ArchitectorTypeFactsBuild
        {
            public override IEnumerator<float> Run(bool blocking)
            {
                Result = Empty;
                yield return 1f;
            }
        }

        private sealed class EmptySnapshot : IArchitectorTypeSnapshot
        {
            public IReadOnlyList<ArchitectorIndexedType> Types => Array.Empty<ArchitectorIndexedType>();
            public ArchitectorIndexedTypeReference DirectBase(string typeKey) => null;
            public IReadOnlyList<ArchitectorIndexedTypeReference> Interfaces(string typeKey) => Array.Empty<ArchitectorIndexedTypeReference>();
            public IReadOnlyList<ArchitectorIndexedMember> Members(string typeKey, ISet<string> projectTypeKeys, ArchitectorOptimizationSettings optimization) => null;
            public IReadOnlyList<ArchitectorIndexedMember> OriginMembers(string typeKey, string declaringIdentity) => Array.Empty<ArchitectorIndexedMember>();
        }
    }
}
