using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorTypeIndexTests : ArchitectorTypeIndexTestBase, IArchitectorTypeIndexTestContract
    {
        private string temporaryRoot;
        private ArchitectorRepository repository;
        private ArchitectorTypeIndex index;

        [SetUp]
        public void SetUp()
        {
            temporaryRoot = Path.Combine(Path.GetTempPath(), "ArchitectorTypeIndexTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            repository.Settings.sourceIndex.includedRoots.Clear();
            repository.Settings.sourceIndex.includedRoots.Add(ArchitectorPackagePaths.Root + "/Tests/Editor");
            repository.Settings.sourceIndex.excludedRoots.Clear();
            index = new ArchitectorTypeIndex(repository);
        }

        [TearDown]
        public void TearDown()
        {
            index?.Dispose();
            if (!string.IsNullOrEmpty(temporaryRoot) && Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, true);
        }

        [Test]
        public void ExactMonoScriptTypeExposesVisibleMembersLazily()
        {
            var indexedType = index.Types.Single(type => type.MetadataName == typeof(ArchitectorTypeIndexTests).FullName);
            var linkedType = LinkedTypeRecord(indexedType);

            var members = index.GetMembers(linkedType);

            Assert.That(members.Any(member => member.Declaration.Contains(nameof(PublicContractMethod))), Is.True);
            Assert.That(members.Any(member => member.Declaration.Contains(nameof(ProtectedContractMethod))), Is.True);
            Assert.That(members.Any(member => member.Declaration.Contains(nameof(PrivateHelper))), Is.False);
        }

        [Test]
        public void OneSourceFileIndexesEveryCompiledTopLevelEnumAndNestedType()
        {
            var expected = new[]
            {
                typeof(ArchitectorTypeIndexTests),
                typeof(ArchitectorTypeIndexTestBase),
                typeof(IArchitectorTypeIndexTestContract),
                typeof(ArchitectorTypeIndexTestStatus),
                typeof(NestedTypeIndexContract)
            };

            foreach (var expectedType in expected)
            {
                var indexed = index.Types.Single(type => type.MetadataName == expectedType.FullName);
                var source = indexed.SourceDeclarations.Single(declaration =>
                    declaration.repositoryPath.EndsWith("/ArchitectorTypeIndexTests.cs", StringComparison.Ordinal));
                Assert.That(source.line, Is.GreaterThan(0), expectedType.FullName);
                Assert.That(source.column, Is.GreaterThan(0), expectedType.FullName);
            }
        }

        [Test]
        public void MemberResolutionDistinguishesSnapshotChangeAndMissingMember()
        {
            var indexedType = index.Types.Single(type => type.MetadataName == typeof(ArchitectorTypeIndexTests).FullName);
            var linkedType = LinkedTypeRecord(indexedType);
            var indexedMember = index.GetMembers(linkedType)
                .Single(member => member.Declaration.Contains(nameof(PublicContractMethod)));
            var selected = new ArchitectorMemberRecord
            {
                kind = indexedMember.Kind,
                declaration = indexedMember.Declaration,
                symbolKey = indexedMember.SymbolKey
            };

            Assert.That(index.ResolveMember(linkedType, selected), Is.EqualTo(ArchitectorMemberResolution.Resolved));
            selected.declaration += " changed";
            Assert.That(index.ResolveMember(linkedType, selected), Is.EqualTo(ArchitectorMemberResolution.SignatureChanged));
            selected.symbolKey = "method|missing:Missing.Type|Missing|0|";
            Assert.That(index.ResolveMember(linkedType, selected), Is.EqualTo(ArchitectorMemberResolution.Missing));
        }

        [Test]
        public void ConformanceAggregatesApprovedLinkedTypeAndSelectedMemberDrift()
        {
            var indexedType = index.Types.Single(type => type.MetadataName == typeof(ArchitectorTypeIndexTests).FullName);
            var indexedMember = index.GetMembers(LinkedTypeRecord(indexedType))
                .Single(member => member.Declaration.Contains(nameof(PublicContractMethod)));
            var layerId = repository.CreateLayer(string.Empty, "Contracts");
            var nodeId = repository.CreateLinkedNode(layerId, 0f, 0f, indexedType);
            repository.AddLinkedMember(layerId, nodeId, indexedMember);
            var consumerId = repository.CreateManualNode(layerId, 400f, 0f);
            repository.CreateEdge(layerId, consumerId, nodeId, ArchitectorEdgeKinds.Dependency, "auto");
            repository.ApproveLayers(new[] { layerId });

            var conformant = ArchitectorConformance.Evaluate(repository, index, new[] { layerId }, 10);

            Assert.That(conformant.State, Is.EqualTo(ArchitectorConformanceStates.Conformant));
            Assert.That(conformant.LinkedTypeCount, Is.EqualTo(1));
            Assert.That(conformant.SelectedMemberCount, Is.EqualTo(1));
            Assert.That(conformant.PassedCount, Is.EqualTo(2));
            Assert.That(conformant.Diagnostics, Is.Empty);

            var layer = repository.FindLayer(layerId);
            var stored = ArchitectorJson.Read<ArchitectorStoredLayerDocument>(repository.Paths.ToAbsoluteDataPath(layer.semanticPath));
            var node = stored.nodes.Single(candidate => candidate.id == nodeId);
            node.type.kind = "interface";
            node.members.Single().declaration += " changed";
            repository.ApplyCanonicalBatch(new ArchitectorCanonicalBatch("Introduce Contract Drift")
                .SetDocument(layer.semanticPath, ArchitectorJson.Serialize(stored)));

            var drifted = ArchitectorConformance.Evaluate(repository, index, new[] { layerId }, 10);

            Assert.That(drifted.State, Is.EqualTo(ArchitectorConformanceStates.Conflicts));
            Assert.That(drifted.ErrorCount, Is.EqualTo(2));
            Assert.That(drifted.UnapprovedLayerCount, Is.EqualTo(1));
            Assert.That(drifted.Diagnostics.Select(item => item.Code), Does.Contain(ArchitectorConformanceCodes.TypeKindChanged));
            Assert.That(drifted.Diagnostics.Select(item => item.Code), Does.Contain(ArchitectorConformanceCodes.MemberSignatureChanged));
        }

        [Test]
        public void ConformanceReportsUnanchoredLinkedNodeInStandaloneLayer()
        {
            var indexedType = index.Types.Single(type => type.MetadataName == typeof(ArchitectorTypeIndexTests).FullName);
            var layerId = repository.CreateLayer(string.Empty, "Contracts");
            var linkedId = repository.CreateLinkedNode(layerId, 0f, 0f, indexedType);

            var report = ArchitectorConformance.Evaluate(repository, index, new[] { layerId }, 10);

            Assert.That(report.State, Is.EqualTo(ArchitectorConformanceStates.Conflicts));
            Assert.That(report.Diagnostics.Single(item => item.Code == ArchitectorConformanceCodes.UnanchoredNode).NodeId,
                Is.EqualTo(linkedId));
        }

        [Test]
        public void ConformanceWarnsAboutReportedDependencyCyclesInEveryLayer()
        {
            repository.UpdateSettings("Report Cycles", settings =>
                settings.rules.dependencyCycles = ArchitectorDependencyCyclePolicy.Report);
            var firstLayer = repository.CreateLayer(string.Empty, "First");
            var secondLayer = repository.CreateLayer(string.Empty, "Second");
            var firstNode = repository.CreateEmptyNode(firstLayer, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondLayer, 0f, 0f);
            repository.CreateCrossLayerEdge(firstLayer, firstNode, secondLayer, secondNode, ArchitectorEdgeKinds.Dependency);
            repository.CreateCrossLayerEdge(secondLayer, secondNode, firstLayer, firstNode, ArchitectorEdgeKinds.Dependency);

            var report = ArchitectorConformance.Evaluate(repository, index, new[] { firstLayer, secondLayer }, 10);

            var cycles = report.Diagnostics.Where(item => item.Code == ArchitectorConformanceCodes.DependencyCycle).ToArray();
            Assert.That(cycles.Select(item => item.LayerId), Is.EquivalentTo(new[] { firstLayer, secondLayer }));
            Assert.That(cycles.Select(item => item.Severity), Is.All.EqualTo(ArchitectorConformanceSeverities.Warning));
            Assert.That(cycles[0].Message, Does.Contain("First/New Node → Second/New Node → First/New Node"));
            Assert.That(report.ErrorCount, Is.Zero);
        }

        [Test]
        public void ExplicitBaseAndInheritedInterfaceFactsConformAndReportDrift()
        {
            var indexedType = index.Types.Single(type => type.MetadataName == typeof(ArchitectorTypeIndexTests).FullName);
            var linkedType = LinkedTypeRecord(indexedType);
            var directBase = index.GetDirectBase(linkedType);
            var contractInterface = index.GetInterfaces(linkedType)
                .Single(item => item.MetadataName == typeof(IArchitectorTypeIndexTestContract).FullName);
            Assert.That(directBase.MetadataName, Is.EqualTo(typeof(ArchitectorTypeIndexTestBase).FullName));

            var layerId = repository.CreateLayer(string.Empty, "Type Facts");
            var nodeId = repository.CreateLinkedNode(layerId, 0f, 0f, indexedType);
            repository.UpdateTypeFacts(layerId, nodeId, directBase.ToRecord(), new[] { contractInterface.ToRecord() });
            var consumerId = repository.CreateManualNode(layerId, 400f, 0f);
            repository.CreateEdge(layerId, consumerId, nodeId, ArchitectorEdgeKinds.Dependency, "auto");
            repository.ApproveLayers(new[] { layerId });

            var conformant = ArchitectorConformance.Evaluate(repository, index, new[] { layerId }, 10);
            Assert.That(conformant.State, Is.EqualTo(ArchitectorConformanceStates.Conformant));
            Assert.That(conformant.SelectedTypeFactCount, Is.EqualTo(2));
            Assert.That(conformant.PassedCount, Is.EqualTo(3));

            repository.UpdateTypeFacts(
                layerId,
                nodeId,
                ArchitectorTypeReference.Declared("System.Object", "class"),
                new[] { ArchitectorTypeReference.Declared("System.IDisposable", "interface") });
            var drifted = ArchitectorConformance.Evaluate(repository, index, new[] { layerId }, 10);
            Assert.That(drifted.ErrorCount, Is.EqualTo(2));
            Assert.That(drifted.Diagnostics.Select(item => item.Code), Does.Contain(ArchitectorConformanceCodes.BaseTypeMismatch));
            Assert.That(drifted.Diagnostics.Select(item => item.Code), Does.Contain(ArchitectorConformanceCodes.InterfaceMissing));
        }

        [Test]
        public void MemberDiscoveryScopeDefaultsToDeclaredMembersAndRebuildsOnlyMemberCache()
        {
            var indexedType = index.Types.Single(type => type.MetadataName == typeof(ArchitectorTypeIndexTests).FullName);
            var linkedType = LinkedTypeRecord(indexedType);
            var typesBefore = index.Types;
            var declaredOnly = index.GetMembers(linkedType);

            Assert.That(declaredOnly.Any(member => member.Declaration.Contains(nameof(PublicContractMethod))), Is.True);
            Assert.That(declaredOnly.Any(member => member.Declaration.Contains(nameof(ParentContractMethod))), Is.False);
            Assert.That(declaredOnly.Any(member => member.IsInterfaceMember), Is.False);

            repository.UpdateSettings("Expand member discovery", settings =>
            {
                settings.optimization.memberParentDepth = 1;
                settings.optimization.includeInterfaceMembers = true;
                settings.optimization.includeFrameworkMembers = true;
            });

            Assert.That(index.Types, Is.SameAs(typesBefore));
            var expanded = index.GetMembers(linkedType);
            Assert.That(expanded, Is.Not.SameAs(declaredOnly));
            Assert.That(expanded.Any(member => member.Declaration.Contains(nameof(ParentContractMethod))), Is.True);
            Assert.That(expanded.Any(member => member.IsInterfaceMember
                                                   && member.Declaration.Contains(nameof(InterfaceContractMethod))), Is.True);
        }

        [Test]
        public void LayoutChangesRetainPopulatedTypeAndMemberCaches()
        {
            var layerId = repository.CreateLayer(string.Empty, "Cache Test");
            var nodeId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var indexedType = index.Types.Single(type => type.MetadataName == typeof(ArchitectorTypeIndexTests).FullName);
            var linkedType = LinkedTypeRecord(indexedType);
            var typesBefore = index.Types;
            var membersBefore = index.GetMembers(linkedType);

            repository.MoveNodes(layerId, new Dictionary<string, ArchitectorPoint>
            {
                [nodeId] = new ArchitectorPoint(160f, 80f)
            });

            Assert.That(index.Types, Is.SameAs(typesBefore));
            Assert.That(index.GetMembers(linkedType), Is.SameAs(membersBefore));
        }

        [Test]
        public void SelectedInheritedMemberStillResolvesAfterDiscoveryScopeNarrows()
        {
            repository.UpdateSettings("Expand member discovery", settings =>
            {
                settings.optimization.memberParentDepth = 1;
                settings.optimization.includeFrameworkMembers = true;
            });
            var indexedType = index.Types.Single(type => type.MetadataName == typeof(ArchitectorTypeIndexTests).FullName);
            var linkedType = LinkedTypeRecord(indexedType);
            var inherited = index.GetMembers(linkedType)
                .Single(member => member.Declaration.Contains(nameof(ParentContractMethod)));
            var selected = new ArchitectorMemberRecord
            {
                kind = inherited.Kind,
                declaration = inherited.Declaration,
                symbolKey = inherited.SymbolKey
            };

            repository.UpdateSettings("Narrow member discovery", settings =>
            {
                settings.optimization.memberParentDepth = 0;
                settings.optimization.includeFrameworkMembers = false;
            });

            Assert.That(index.GetMembers(linkedType).Any(member => member.SymbolKey == inherited.SymbolKey), Is.False);
            Assert.That(index.ResolveMember(linkedType, selected), Is.EqualTo(ArchitectorMemberResolution.Resolved));
        }

        [Test]
        public void SourceRootChangeInvalidatesFullTypeIndex()
        {
            var typesBefore = index.Types;

            repository.UpdateSettings("Change source roots", settings =>
                settings.sourceIndex.excludedRoots.Add(ArchitectorPackagePaths.Root + "/Tests/Editor/Excluded"));

            Assert.That(index.Types, Is.Not.SameAs(typesBefore));
        }

        [TestCase("Owner", "public Object Owner { get; private set; }")]
        [TestCase("State", "public Int32 State { get; internal set; }")]
        [TestCase("ProtectedSetter", "public Int32 ProtectedSetter { get; protected set; }")]
        [TestCase("PrivateGetter", "public Int32 PrivateGetter { private get; set; }")]
        [TestCase("OnAdded", "protected virtual void OnAdded()")]
        [TestCase("RequiredHook", "protected abstract void RequiredHook()")]
        [TestCase("Register", "internal void Register(Object entity)")]
        [TestCase("Initialize", "public void Initialize(params Object[] categories)")]
        [TestCase("Add", "public void Add(Boolean enabled = true)")]
        [TestCase("Defaults", "public void Defaults(ArchitectorTypeIndexTestStatus status = ArchitectorTypeIndexTestStatus.Ready, String label = null, Int32 order = 0)")]
        public void IndexedDeclarationsPreserveSourceContract(string name, string expected)
        {
            var type = index.Types.Single(candidate => candidate.MetadataName == typeof(DeclarationContract).FullName);
            var member = index.GetMembers(LinkedTypeRecord(type)).Single(candidate => candidate.SymbolKey.Split('|')[2] == name);
            Assert.That(member.Declaration, Is.EqualTo(expected));
        }

        [TestCase("OnAdded", "protected sealed override void OnAdded()")]
        [TestCase("RequiredHook", "protected override void RequiredHook()")]
        [TestCase("InterfaceContractMethod", "public void InterfaceContractMethod()")]
        public void IndexedOverridesDoNotConfuseInterfaceImplementationWithVirtual(string name, string expected)
        {
            var type = index.Types.Single(candidate => candidate.MetadataName == typeof(ConcreteDeclarationContract).FullName);
            var member = index.GetMembers(LinkedTypeRecord(type)).Single(candidate => candidate.SymbolKey.Split('|')[2] == name);
            Assert.That(member.Declaration, Is.EqualTo(expected));
        }

        [Test]
        public void BindingPreservesAccessorsAndVirtualHooksAndReportsRealContractDrift()
        {
            var type = index.Types.Single(candidate => candidate.MetadataName == typeof(DeclarationContract).FullName);
            var names = new[] { "Owner", "State", "OnAdded", "Register", "Initialize", "Add" };
            var members = index.GetMembers(LinkedTypeRecord(type))
                .Where(member => names.Contains(member.SymbolKey.Split('|')[2])).ToArray();
            Assert.That(members.Length, Is.EqualTo(names.Length));
            var created = repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                layers = { new ArchitectorPlanLayer { reference = "contracts", name = "Contracts" } },
                nodes =
                {
                    new ArchitectorPlanNode
                    {
                        reference = "contract", layer = "contracts", title = "Contract", mode = "declared",
                        typeName = "DeclarationContract",
                        members = members.Select(member => new ArchitectorPlanMember
                            { kind = member.Kind, declaration = member.Declaration }).ToList()
                    },
                    new ArchitectorPlanNode { reference = "consumer", layer = "contracts", title = "Consumer", x = 480f }
                },
                edges = { new ArchitectorPlanEdge
                {
                    sourceLayer = "contracts", source = "consumer", targetLayer = "contracts", target = "contract",
                    kind = ArchitectorEdgeKinds.Dependency
                } }
            });
            var layerId = created.References["contracts"];
            var nodeId = created.References["contract"];
            repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                nodeContracts = { new ArchitectorPlanNodeContract
                {
                    layer = layerId, node = nodeId, mode = "linked", typeKey = type.Key,
                    members = members.Select(member => new ArchitectorPlanMember { symbolKey = member.SymbolKey }).ToList()
                } }
            }, typeIndex: index);

            var node = repository.LoadLayer(layerId).semantic.nodes.Single(candidate => candidate.id == nodeId);
            Assert.That(node.members.Select(member => member.declaration), Is.EquivalentTo(members.Select(member => member.Declaration)));
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Draft));
            var report = ArchitectorConformance.Evaluate(repository, index, new[] { layerId });
            Assert.That(report.ErrorCount, Is.Zero);
            Assert.That(report.SelectedMemberCount, Is.EqualTo(names.Length));
            Assert.That(report.Diagnostics.Select(item => item.Code), Is.EquivalentTo(new[] { ArchitectorConformanceCodes.LayerNotApproved }));

            foreach (var selected in node.members.Where(member => member.declaration.Contains("private set") || member.declaration.Contains("virtual")))
            {
                selected.declaration = selected.declaration.Replace("private set", "set").Replace("virtual ", "");
                Assert.That(index.ResolveMember(node.type, selected), Is.EqualTo(ArchitectorMemberResolution.SignatureChanged));
            }
        }

        [Test]
        public void ManualNodeResolvesItsBaseTypeByKey()
        {
            var baseType = index.Types.Single(candidate => candidate.MetadataName == typeof(DeclarationContract).FullName);
            var created = repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                layers = { new ArchitectorPlanLayer { reference = "planned", name = "Planned" } },
                nodes = { new ArchitectorPlanNode
                {
                    reference = "node", layer = "planned", title = "Planned Contract", mode = "manual", typeName = "PlannedContract",
                    baseType = new ArchitectorPlanTypeReference { typeKey = baseType.Key }
                } }
            }, typeIndex: index);

            var node = repository.LoadLayer(created.References["planned"]).semantic.nodes.Single();
            Assert.That(node.type.baseType.mode, Is.EqualTo("linked"));
            Assert.That(node.type.baseType.metadataName, Is.EqualTo(baseType.MetadataName));
            Assert.Throws<ArchitectorDataException>(() => repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                nodes = { new ArchitectorPlanNode
                {
                    reference = "missing", layer = created.References["planned"], title = "Missing", mode = "manual", typeName = "MissingBase",
                    baseType = new ArchitectorPlanTypeReference { typeKey = "Nowhere:Missing" }
                } }
            }, typeIndex: index));
        }

        public abstract class DeclarationContract
        {
            public object Owner { get; private set; }
            public int State { get; internal set; }
            public int ProtectedSetter { get; protected set; }
            public int PrivateGetter { private get; set; }
            protected virtual void OnAdded() { }
            protected abstract void RequiredHook();
            internal void Register(object entity) { }
            public void Initialize(params object[] categories) { }
            public void Add(bool enabled = true) { }
            public void Defaults(ArchitectorTypeIndexTestStatus status = ArchitectorTypeIndexTestStatus.Ready, string label = null, int order = 0) { }
        }

        public sealed class ConcreteDeclarationContract : DeclarationContract, IArchitectorTypeIndexTestContract
        {
            protected sealed override void OnAdded() { }
            protected override void RequiredHook() { }
            public void InterfaceContractMethod() { }
        }

        public void PublicContractMethod(int value)
        {
        }

        protected void ProtectedContractMethod()
        {
        }

        public void InterfaceContractMethod()
        {
        }

        private void PrivateHelper()
        {
        }

        private static string LiteralBeforeNestedType(string value) => $"{value ?? "fallback"}";

        public sealed class NestedTypeIndexContract
        {
        }

        private static ArchitectorTypeRecord LinkedTypeRecord(ArchitectorIndexedType indexedType)
        {
            return new ArchitectorTypeRecord
            {
                mode = "linked",
                assemblyName = indexedType.AssemblyName,
                metadataName = indexedType.MetadataName
            };
        }
    }

    public abstract class ArchitectorTypeIndexTestBase
    {
        public void ParentContractMethod()
        {
        }
    }

    public interface IArchitectorTypeIndexTestContract
    {
        void InterfaceContractMethod();
    }

    public enum ArchitectorTypeIndexTestStatus
    {
        Ready
    }
}
