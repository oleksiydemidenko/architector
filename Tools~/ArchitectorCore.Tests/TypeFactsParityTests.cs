using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Architector.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Architector.Core.Tests
{
    // The command line reads type facts from the sources with Roslyn; the Unity Editor reads them by reflection from the
    // compiled assemblies. The same sources must give the same types, keys, bases, interfaces and declarations.
    public sealed class TypeFactsParityTests
    {
        private const string AssemblyName = "Game.Runtime";

        private const string Source = @"
using System;
using System.Collections.Generic;

namespace Game.Parity
{
    public enum Status : byte { Ready = 1, Busy = 2 }

    public delegate int Scorer(string name, ref int total);

    public interface IContract
    {
        void InterfaceMethod();
        int Count { get; }
        event Action Changed;
    }

    public interface IGeneric<T> { T Get(); }

    public abstract class DeclarationContract
    {
        public const int Limit = 4;
        public static readonly string Name = ""x"";
        protected internal int Shared;
        private protected int Narrow;
        public object Owner { get; private set; }
        public int State { get; internal set; }
        public int PrivateGetter { private get; set; }
        public int this[int index, string key] => index;
        protected virtual void OnAdded() { }
        protected abstract void RequiredHook();
        internal void Register(object entity) { }
        public void Initialize(params object[] categories) { }
        public void Add(bool enabled = true, Status status = Status.Busy, string label = null, int order = 3, float ratio = 0.5f) { }
        public T Pick<T, U>(List<T> items, Dictionary<string, U[]> map, out int found) where T : class { found = 0; return null; }
        public void Refs(ref int a, in double b, int[,] grid, int?[] nullable) { }
        public static DeclarationContract operator +(DeclarationContract a, DeclarationContract b) => a;
        public static implicit operator int(DeclarationContract value) => 0;
        public event EventHandler Moved;
        protected DeclarationContract() { }
        ~DeclarationContract() { }
        private void Hidden() { }
    }

    public sealed class Concrete : DeclarationContract, IContract, IGeneric<Status>
    {
        protected sealed override void OnAdded() { }
        protected override void RequiredHook() { }
        public void InterfaceMethod() { }
        public int Count => 0;
        public event Action Changed;
        Status IGeneric<Status>.Get() => Status.Ready;
        public override string ToString() => ""c"";

        public sealed class Nested<TItem>
        {
            public TItem Item;
            public void Use<TOther>(TItem item, TOther other, Nested<TItem> self) { }

            public struct Deeper
            {
                public TItem Value;
            }
        }
    }

    public static class Helpers
    {
        public static (int count, string name) Pair() => (0, null);
        public static void Extend(this Concrete value) { }
    }

    public struct Point
    {
        public float X;
        public Point(float x) { X = x; }
    }

    public class Generic<T> : List<T> where T : struct
    {
        public virtual T Value { get; set; }
    }

    public class Closed : Generic<int>
    {
        public override int Value { get; set; }
    }
}
";

        private string project;

        [SetUp]
        public void SetUp()
        {
            project = Path.Combine(Path.GetTempPath(), "ArchitectorParity", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(project, "Assets", "Parity"));
            Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
            File.WriteAllText(Path.Combine(project, "Assets", "Parity", "Parity.cs"), Source);
            File.WriteAllText(Path.Combine(project, "Assets", "Parity", "Game.Runtime.asmdef"), "{ \"name\": \"" + AssemblyName + "\" }");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(project))
                Directory.Delete(project, true);
        }

        [Test]
        public void RoslynFactsEqualReflectionFactsOfTheCompiledAssembly()
        {
            // The reflection facts read the sources relative to the project folder, as Unity runs in it.
            var folder = Environment.CurrentDirectory;
            Environment.CurrentDirectory = project;
            IArchitectorTypeSnapshot reflection;
            try
            {
                reflection = Snapshot(new ArchitectorReflectionTypeFacts(new CompiledSources(Compile())));
            }
            finally
            {
                Environment.CurrentDirectory = folder;
            }
            var roslyn = Snapshot(new McpTypeFacts(project));

            Assert.That(Describe(roslyn.Types), Is.EqualTo(Describe(reflection.Types)));
            var keys = reflection.Types.Select(type => type.Key).ToHashSet(StringComparer.Ordinal);
            var optimization = new ArchitectorOptimizationSettings { memberParentDepth = 1, includeInterfaceMembers = true, includeFrameworkMembers = false };
            foreach (var type in reflection.Types)
            {
                Assert.That(Reference(roslyn.DirectBase(type.Key)), Is.EqualTo(Reference(reflection.DirectBase(type.Key))), type.Key + " base");
                Assert.That(roslyn.Interfaces(type.Key).Select(Reference), Is.EqualTo(reflection.Interfaces(type.Key).Select(Reference)), type.Key + " interfaces");
                var expectedMembers = Members(reflection.Members(type.Key, keys, optimization));
                var actualMembers = Members(roslyn.Members(type.Key, keys, optimization));
                Assert.That(actualMembers, Is.EqualTo(expectedMembers), type.Key + " members; only Roslyn: " + string.Join(" ## ", actualMembers.Except(expectedMembers)) + "; only reflection: " + string.Join(" ## ", expectedMembers.Except(actualMembers)));
            }
            Assert.That(reflection.Types.Count, Is.GreaterThan(10));
        }

        private static IArchitectorTypeSnapshot Snapshot(IArchitectorTypeFacts facts)
        {
            var build = facts.Start(new[] { "Assets" }, Array.Empty<string>());
            using (var steps = build.Run(true))
                while (steps.MoveNext())
                {
                }
            return build.Result;
        }

        private static string[] Describe(IEnumerable<ArchitectorIndexedType> types) =>
            types.Select(type => string.Join(" | ", type.Key, type.DisplayName, type.Namespace, type.Kind,
                    string.Join(",", type.GenericParameters), string.Join(",", type.Modifiers),
                    string.Join(",", type.SourceDeclarations.Select(source => $"{source.repositoryPath}:{source.line}:{source.column}"))))
                .ToArray();

        // The test runs on .NET, whose core library is System.Private.CoreLib; the Unity Editor runs Mono, whose core
        // library is mscorlib, and the Roslyn facts name the Mono one.
        private static string Mono(string text) => System.Text.RegularExpressions.Regex.Replace(text,
            @"System\.Private\.CoreLib, Version=[^,]+, Culture=neutral, PublicKeyToken=7cec85d7bea7798e",
            "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089").Replace("System.Private.CoreLib:", "mscorlib:");

        private static string Reference(ArchitectorIndexedTypeReference reference) =>
            reference == null ? "none" : Mono($"{reference.Key} | {reference.DisplayName} | {reference.Namespace} | {reference.Kind}");

        private static string[] Members(IEnumerable<ArchitectorIndexedMember> members) =>
            (members ?? Array.Empty<ArchitectorIndexedMember>())
            .Select(member => Mono($"{member.SymbolKey} || {member.Declaration} || {member.Kind} || {member.ParentDepth} || {member.IsInterfaceMember} || {member.IsFrameworkMember}"))
            .ToArray();

        private Assembly Compile()
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(AssemblyName,
                new[] { CSharpSyntaxTree.ParseText(Source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
            return Assembly.Load(stream.ToArray());
        }

        private sealed class CompiledSources : IArchitectorTypeSources
        {
            private readonly Assembly assembly;

            public CompiledSources(Assembly assembly)
            {
                this.assembly = assembly;
            }

            public event Action CodeChanged
            {
                add { }
                remove { }
            }

            public ArchitectorTypeSourceSet Collect(IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots) =>
                new(new[] { new ArchitectorTypeIndexSource(AssemblyName, "Assets/Parity/Parity.cs", string.Empty) },
                    new Dictionary<string, Assembly> { [AssemblyName] = assembly });

            public void Dispose()
            {
            }
        }
    }
}
