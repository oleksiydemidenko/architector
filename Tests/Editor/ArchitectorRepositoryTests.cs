using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorRepositoryTests
    {
        private string temporaryRoot;
        private ArchitectorRepository repository;

        [SetUp]
        public void SetUp()
        {
            temporaryRoot = Path.Combine(Path.GetTempPath(), "ArchitectorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(temporaryRoot) && Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, true);
        }

        [TestCase("Authority and Publication", "Authority-and-Publication")]
        [TestCase("  Door - Control: v2 / test ", "Door-Control-v2-test")]
        [TestCase("Слой", "")]
        [TestCase("Very long layer name that keeps going past the limit", "Very-long-layer-name-that-keeps-going-pa")]
        public void EntryLabelKeepsOnlySafeCharacters(string name, string expected)
        {
            Assert.That(ArchitectorPaths.EntryLabel(name), Is.EqualTo(expected));
        }

        [Test]
        public void FilesCarryTheNamesOfTheirEntriesAndFollowRenames()
        {
            var groupId = repository.CreateGroup(string.Empty, "Core Group");
            var firstId = repository.CreateLayer(groupId, "Door Control");
            var secondId = repository.CreateLayer(string.Empty, "Circuits");
            var firstNode = repository.CreateEmptyNode(firstId, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondId, 0f, 0f);
            repository.CreateCrossLayerEdge(firstId, firstNode, secondId, secondNode, ArchitectorEdgeKinds.Dependency);
            repository.ApproveLayers(new[] { firstId });
            var root = Path.Combine(temporaryRoot, "Architector");
            var pairId = Path.GetFileName(Directory.GetFiles(Path.Combine(root, "Links")).Single()).Split('.')[0];
            var names = new[] { firstId, secondId }.OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => repository.FindLayer(id).name.Replace(' ', '-')).ToArray();

            Assert.That(File.Exists(Path.Combine(root, "Groups", groupId + ".Core-Group.group.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "Layers", firstId + ".Door-Control.layer.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "Layouts", firstId + ".Door-Control.layout.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "Baselines", firstId + ".Door-Control.baseline.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "Links", $"{pairId}.{names[0]}--{names[1]}.link.json")), Is.True);

            repository.RenameEntry(secondId, "Light Circuits");

            Assert.That(Directory.GetFiles(Path.Combine(root, "Layers"), secondId + ".*").Select(Path.GetFileName),
                Is.EqualTo(new[] { secondId + ".Light-Circuits.layer.json" }));
            Assert.That(Directory.GetFiles(Path.Combine(root, "Links")).Single(), Does.Contain("Light-Circuits"));
            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(reloaded.FindLayer(secondId).name, Is.EqualTo("Light Circuits"));
        }

        [Test]
        public void RevisionKeepsCountingWhenTheRepositoryIsLoadedAgain()
        {
            repository.CreateLayer(string.Empty, "Counted");
            var before = repository.Revision;

            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));

            Assert.That(reloaded.Revision, Is.GreaterThan(before), "A script reload must not restart the revision at 1.");
        }

        [Test]
        public void RenamingALayerRetitlesThePortalsThatLeadToIt()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Source");
            var targetLayerId = repository.CreateLayer(string.Empty, "Target");
            var pair = repository.CreateLayerPortalPair(sourceLayerId, targetLayerId, 0f, 0f);
            string Title(ArchitectorRepository from, string layerId, string nodeId) =>
                from.LoadLayer(layerId).semantic.nodes.Single(node => node.id == nodeId).title;

            repository.RenameEntry(targetLayerId, "Renamed Target");
            repository.ApplySemanticPlan(
                new ArchitectorSemanticPlan
                {
                    renames = { new ArchitectorPlanRename { kind = "entry", target = sourceLayerId, name = "Renamed Source" } }
                },
                "Rename Source");

            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(Title(reloaded, sourceLayerId, pair.SourceNodeId), Is.EqualTo("Renamed Target"));
            Assert.That(Title(reloaded, targetLayerId, pair.TargetNodeId), Is.EqualTo("Renamed Source"));
            Assert.That(reloaded.RefreshAllPortalTitles(), Is.Zero);
        }

        [Test]
        public void StalePortalTitlesFromOlderRenamesAreRepaired()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Source");
            var targetLayerId = repository.CreateLayer(string.Empty, "Target");
            // A relationship keeps the Portal pair alive in a new session; pairs without one are removed on save.
            repository.CreateCrossLayerEdge(
                sourceLayerId,
                repository.CreateEmptyNode(sourceLayerId, 0f, 0f),
                targetLayerId,
                repository.CreateEmptyNode(targetLayerId, 0f, 0f),
                ArchitectorEdgeKinds.Dependency);
            var linkPath = Directory.GetFiles(Path.Combine(temporaryRoot, "Architector", "Links")).Single();
            var link = ArchitectorJson.Deserialize<ArchitectorStoredLinkDocument>(File.ReadAllText(linkPath));
            var exit = link.endpoints.Single(endpoint => endpoint.layerId == sourceLayerId);
            exit.title = "Old Target";
            File.WriteAllText(linkPath, ArchitectorJson.Serialize(link));
            string ExitTitle(ArchitectorRepository from) =>
                from.LoadLayer(sourceLayerId).semantic.nodes.Single(node => node.id == exit.nodeId).title;
            var stale = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(ExitTitle(stale), Is.EqualTo("Old Target"));

            Assert.That(stale.RefreshAllPortalTitles(), Is.EqualTo(1));
            Assert.That(stale.RefreshAllPortalTitles(), Is.Zero);

            Assert.That(ExitTitle(new ArchitectorRepository(new ArchitectorPaths(temporaryRoot))), Is.EqualTo("Target"));
        }

        [Test]
        public void UnlabeledFilesFromOlderVersionsAreRenamedOnTheNextSave()
        {
            var layerId = repository.CreateLayer(string.Empty, "Door Control");
            var root = Path.Combine(temporaryRoot, "Architector");
            foreach (var directory in new[] { "Layers", "Layouts" })
                foreach (var file in Directory.GetFiles(Path.Combine(root, directory)))
                {
                    var name = Path.GetFileName(file);
                    File.Move(file, Path.Combine(root, directory, name.Split('.')[0] + "." + string.Join(".", name.Split('.').Skip(name.Split('.').Length - 2))));
                }

            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(reloaded.FindLayer(layerId).name, Is.EqualTo("Door Control"));
            reloaded.CreateLayer(string.Empty, "Other");

            Assert.That(Directory.GetFiles(Path.Combine(root, "Layers"), layerId + ".*").Select(Path.GetFileName),
                Is.EqualTo(new[] { layerId + ".Door-Control.layer.json" }));
            Assert.That(Directory.GetFiles(Path.Combine(root, "Layouts"), layerId + ".*").Select(Path.GetFileName),
                Is.EqualTo(new[] { layerId + ".Door-Control.layout.json" }));
        }

        [Test]
        public void TwoFilesForOneEntryAreRejected()
        {
            var layerId = repository.CreateLayer(string.Empty, "Door Control");
            var files = ArchitectorStorageCodec.Encode(repository.Project, new Dictionary<string, ArchitectorLayerDocuments>
            {
                [layerId] = repository.LoadLayer(layerId)
            });
            var labeled = files.Keys.Single(path => path.StartsWith("Layers/", StringComparison.Ordinal));
            files[$"Layers/{layerId}.Renamed-On-Another-Branch.layer.json"] = files[labeled];

            var exception = Assert.Throws<ArchitectorDataException>(() => ArchitectorStorageCodec.Decode(files));
            Assert.That(exception.Message, Does.Contain("Two files describe"));
        }

        [Test]
        public void InitializationCreatesValidDeterministicDocuments()
        {
            Assert.That(File.Exists(repository.Paths.ProjectPath), Is.True);
            Assert.That(File.Exists(repository.Paths.SettingsPath), Is.True);
            Assert.That(repository.Project.schemaVersion, Is.EqualTo(ArchitectorValidation.CurrentSchemaVersion));
            Assert.That(repository.Settings.sourceIndex.includedRoots, Does.Contain("Assets/Scripts"));

            var firstSerialization = File.ReadAllText(repository.Paths.ProjectPath);
            repository.Load();
            var secondSerialization = File.ReadAllText(repository.Paths.ProjectPath);
            Assert.That(secondSerialization, Is.EqualTo(firstSerialization));
        }

        [Test]
        public void LinkedTypeWithoutAuthoredBaseReloadsAsNoFact()
        {
            var layerId = repository.CreateLayer(string.Empty, "Legacy");
            var nodeId = repository.CreateLinkedNode(layerId, 0f, 0f, CreateIndexedType());

            var loaded = repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == nodeId);

            Assert.That(loaded.type.baseType, Is.Null);
        }

        [Test]
        public void MissingLayoutDocumentReportsLayerAndCanonicalPathsOnLoad()
        {
            var layerId = repository.CreateLayer(string.Empty, "Missing Runtime");
            var layer = repository.FindLayer(layerId);
            File.Delete(repository.Paths.ToAbsoluteDataPath(layer.layoutPath));

            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            var exception = Assert.Throws<ArchitectorDataException>(() => repository.LoadLayer(layerId));

            Assert.That(exception.Message, Does.Contain("Missing Runtime"));
            Assert.That(exception.Message, Does.Contain(layer.semanticPath));
            Assert.That(exception.Message, Does.Contain(layer.layoutPath));
            Assert.That(repository.FindLayer(layerId).name, Is.EqualTo("Missing Runtime"));
        }

        [Test]
        public void InvalidExternalDocumentDoesNotReplaceTheLoadedProject()
        {
            var layerId = repository.CreateLayer(string.Empty, "Stable Runtime");
            var loadedProject = repository.Project;
            var layer = repository.FindLayer(layerId);
            File.WriteAllText(repository.Paths.ToAbsoluteDataPath(layer.semanticPath), "{ invalid json");

            Assert.Throws<ArchitectorDataException>(() => repository.ReloadExternalChanges());

            Assert.That(repository.Project, Is.SameAs(loadedProject));
            Assert.That(repository.FindLayer(layerId), Is.SameAs(layer));
        }

        [Test]
        public void ApprovalTracksSemanticChangesButIgnoresLayout()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var nodeId = repository.CreateManualNode(layerId, 0f, 0f);
            repository.ApproveLayers(new[] { layerId });

            var approved = repository.GetLayerApprovalStatus(layerId);
            Assert.That(approved.State, Is.EqualTo(ArchitectorApprovalStates.Approved));

            repository.MoveNode(layerId, nodeId, 320f, 160f);
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));

            repository.UpdateLayerPurpose(layerId, "Owns the runtime boundary.");
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            repository.Undo();
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
        }

        [Test]
        public void ApprovalAllowsUnanchoredConceptsButRejectsUnanchoredLinkedNode()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var sourceId = repository.CreateManualNode(layerId, 0f, 0f);
            var targetId = repository.CreateManualNode(layerId, 400f, 0f);
            repository.CreateManualNode(layerId, 800f, 0f);
            repository.CreateEdge(layerId, sourceId, targetId, ArchitectorEdgeKinds.Dependency, "auto");
            Assert.DoesNotThrow(() => repository.ApproveLayers(new[] { layerId }));

            var linkedId = repository.CreateLinkedNode(layerId, 1200f, 0f, CreateIndexedType());

            var exception = Assert.Throws<ArchitectorDataException>(() =>
                repository.ApproveLayers(new[] { layerId }));

            Assert.That(exception.Message, Does.Contain("has no relationship"));
            repository.CreateEdge(layerId, targetId, linkedId, ArchitectorEdgeKinds.Dependency, "auto");
            Assert.DoesNotThrow(() => repository.ApproveLayers(new[] { layerId }));
        }

        [Test]
        public void ApprovalHashIncludesVisibleContractButIgnoresSourceLocation()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            repository.CreateLinkedNode(layerId, 0f, 0f, CreateIndexedType());
            var layer = repository.FindLayer(layerId);
            var semantic = repository.LoadLayer(layerId).semantic;
            var originalHash = ArchitectorApproval.ComputeSemanticHash(layer, semantic);

            semantic.nodes.Single().type.sourceDeclarations.Single().repositoryPath = "Assets/Moved/PlayerController.cs";
            semantic.nodes.Single().type.sourceDeclarations.Single().scriptGuid = "fedcba9876543210fedcba9876543210";
            semantic.nodes.Single().type.sourceDeclarations.Single().line = 42;
            var movedSourceHash = ArchitectorApproval.ComputeSemanticHash(layer, semantic);

            var renamedLayer = ArchitectorJson.Clone(layer);
            renamedLayer.name = "Runtime Contracts";
            var renamedHash = ArchitectorApproval.ComputeSemanticHash(renamedLayer, semantic);
            renamedLayer.name = layer.name;
            renamedLayer.purpose = "Owns the visible runtime boundary.";
            var purposeHash = ArchitectorApproval.ComputeSemanticHash(renamedLayer, semantic);

            Assert.That(movedSourceHash, Is.EqualTo(originalHash));
            Assert.That(renamedHash, Is.Not.EqualTo(originalHash));
            Assert.That(purposeHash, Is.Not.EqualTo(originalHash));
        }

        [Test]
        public void RevokingApprovalReturnsLayerToDraftAndCanBeUndone()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            repository.ApproveLayers(new[] { layerId });

            repository.RevokeLayerApprovals(new[] { layerId });

            var revoked = repository.GetLayerApprovalStatus(layerId);
            Assert.That(revoked.State, Is.EqualTo(ArchitectorApprovalStates.Draft));
            repository.ApproveLayers(new[] { layerId });
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            repository.Undo();
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Draft));
        }

        [Test]
        public void AgentPreferencesAreLocalAndNeverApproveImplicitly()
        {
            var preferences = new ArchitectorAgentPreferences(temporaryRoot);
            try
            {
                Assert.That(preferences.WorkflowMode, Is.EqualTo(ArchitectorAgentWorkflowModes.Safe));
                Assert.That(preferences.LockWindowsDuringAgentWork, Is.True);
                preferences.WorkflowMode = ArchitectorAgentWorkflowModes.Free;
                preferences.LockWindowsDuringAgentWork = false;

                var reopened = new ArchitectorAgentPreferences(temporaryRoot);
                Assert.That(reopened.WorkflowMode, Is.EqualTo(ArchitectorAgentWorkflowModes.Free));
                Assert.That(reopened.LockWindowsDuringAgentWork, Is.False);
                Assert.That(new ArchitectorAgentPreferences(temporaryRoot + "_other").WorkflowMode,
                    Is.EqualTo(ArchitectorAgentWorkflowModes.Safe));

                var layerId = repository.CreateLayer(string.Empty, "Runtime");
                repository.CreateManualNode(layerId, 0f, 0f);
                Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Draft));
                Assert.That(File.ReadAllText(repository.Paths.SettingsPath), Does.Not.Contain("workflowMode"));
            }
            finally
            {
                preferences.Clear();
            }
        }

        [Test]
        public void CanonicalBatchDryRunIsReadOnlyAndApplyIsOneUndoOperation()
        {
            const string layerId = "layer_ai_batch";
            var semanticPath = ArchitectorPaths.LayerPath(layerId);
            var layoutPath = ArchitectorPaths.LayoutPath(layerId);
            var batch = new ArchitectorCanonicalBatch("Apply AI Architecture Plan")
                .SetDocument(semanticPath, ArchitectorJson.Serialize(new ArchitectorStoredLayerDocument
                {
                    layerId = layerId,
                    name = "AI Batch Layer"
                }))
                .SetDocument(layoutPath, ArchitectorJson.Serialize(new ArchitectorStoredLayoutDocument { layerId = layerId }));
            var revisionBefore = repository.Revision;
            var changedNotifications = 0;
            repository.Changed += () => changedNotifications++;

            var preview = repository.DryRunCanonicalBatch(batch);

            Assert.That(preview.ChangedFileCount, Is.EqualTo(2));
            Assert.That(preview.ValidatedFileCount, Is.EqualTo(3));
            Assert.That(repository.Revision, Is.EqualTo(revisionBefore));
            Assert.That(repository.FindLayer(layerId), Is.Null);
            Assert.That(File.Exists(repository.Paths.ToAbsoluteDataPath(semanticPath)), Is.False);

            var applied = repository.ApplyCanonicalBatch(batch);

            Assert.That(applied.ChangedFileCount, Is.EqualTo(2));
            Assert.That(repository.FindLayer(layerId), Is.Not.Null);
            Assert.That(repository.CanUndo, Is.True);
            Assert.That(repository.NextUndoName, Is.EqualTo("Apply AI Architecture Plan"));
            Assert.That(changedNotifications, Is.EqualTo(1));

            repository.Undo();
            Assert.That(repository.FindLayer(layerId), Is.Null);
            Assert.That(File.Exists(repository.Paths.ToAbsoluteDataPath(semanticPath)), Is.False);

            repository.Redo();
            Assert.That(repository.FindLayer(layerId), Is.Not.Null);
            Assert.That(File.Exists(DataPath(semanticPath)), Is.True);
        }

        [Test]
        public void CanonicalBatchCanRecoverByReplacingAnInvalidCurrentLayer()
        {
            var layerId = repository.CreateLayer(string.Empty, "Broken Layer");
            var sourceId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var targetId = repository.CreateEmptyNode(layerId, 600f, 0f);
            repository.CreateEdge(layerId, sourceId, targetId, ArchitectorEdgeKinds.Dependency, "auto");
            var layer = repository.FindLayer(layerId);
            var layerPath = repository.Paths.ToAbsoluteDataPath(layer.semanticPath);
            var stored = ArchitectorJson.Deserialize<ArchitectorStoredLayerDocument>(File.ReadAllText(layerPath));
            stored.edges.Single().sourceLayerId = string.Empty;
            ArchitectorJson.WriteAtomic(layerPath, stored);

            var batch = new ArchitectorCanonicalBatch("Recover Invalid Canonical")
                .DeleteDocument(layer.semanticPath)
                .DeleteDocument(layer.layoutPath);

            Assert.DoesNotThrow(() => repository.DryRunCanonicalBatch(batch));
            Assert.DoesNotThrow(() => repository.ApplyCanonicalBatch(batch));
            Assert.That(repository.FindLayer(layerId), Is.Null);
            Assert.That(repository.ExternalChangeError, Is.Empty);
        }

        [Test]
        public void SemanticPlanCreatesGraphAndPortalPairAsOneUndoOperation()
        {
            var plan = new ArchitectorSemanticPlan
            {
                groups = { new ArchitectorPlanGroup { reference = "architecture", name = "Architecture" } },
                layers =
                {
                    new ArchitectorPlanLayer { reference = "request", name = "Request", parent = "architecture" },
                    new ArchitectorPlanLayer { reference = "validation", name = "Validation", parent = "architecture" }
                },
                nodes =
                {
                    new ArchitectorPlanNode { reference = "input", layer = "request", title = "Input", x = 0f, y = 0f },
                    new ArchitectorPlanNode { reference = "check", layer = "validation", title = "Check", x = 600f, y = 0f }
                },
                portals =
                {
                    new ArchitectorPlanPortal
                    {
                        exitReference = "to-validation",
                        entryReference = "from-request",
                        fromLayer = "request",
                        toLayer = "validation",
                        exitX = 600f,
                        entryX = 0f
                    }
                },
                edges =
                {
                    new ArchitectorPlanEdge { reference = "request-edge", sourceLayer = "request", source = "input", targetLayer = "validation", target = "check", kind = ArchitectorEdgeKinds.DataFlow }
                },
                edgeChanges =
                {
                    new ArchitectorPlanEdgeChange { edge = "request-edge", kind = ArchitectorEdgeKinds.Dependency }
                }
            };
            var revision = repository.Revision;

            var preview = repository.DryRunSemanticPlan(plan, "Create Semantic Architecture");

            Assert.That(preview.Batch.ChangedFileCount, Is.EqualTo(6));
            Assert.That(repository.Revision, Is.EqualTo(revision));
            Assert.That(repository.Project.groups, Is.Empty);

            var applied = repository.ApplySemanticPlan(plan, "Create Semantic Architecture");

            Assert.That(repository.NextUndoName, Is.EqualTo("Create Semantic Architecture"));
            Assert.That(applied.References.Keys, Does.Contain("to-validation"));
            var requestLayer = repository.LoadLayer(applied.References["request"]);
            var validationLayer = repository.LoadLayer(applied.References["validation"]);
            Assert.That(requestLayer.semantic.edges.Single().kind, Is.EqualTo(ArchitectorEdgeKinds.Dependency));
            Assert.That(validationLayer.semantic.edges, Is.Empty);
            Assert.That(requestLayer.semantic.nodes.Single(node => node.kind == ArchitectorNodeKinds.LayerPortal).portal.role,
                Is.EqualTo(ArchitectorPortalRoles.Exit));
            Assert.That(validationLayer.semantic.nodes.Single(node => node.kind == ArchitectorNodeKinds.LayerPortal).portal.role,
                Is.EqualTo(ArchitectorPortalRoles.Entry));

            repository.Undo();
            Assert.That(repository.Project.groups, Is.Empty);
            Assert.That(repository.Project.layers, Is.Empty);
        }

        [Test]
        public void SemanticPlanModifiesExistingArchitectureAsOneUndoOperation()
        {
            var groupId = repository.CreateGroup(string.Empty, "Architecture");
            var layerId = repository.CreateLayer(groupId, "Runtime");
            var firstNodeId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var secondNodeId = repository.CreateEmptyNode(layerId, 600f, 0f);
            var edgeId = repository.CreateEdge(layerId, firstNodeId, secondNodeId, ArchitectorEdgeKinds.DataFlow, "auto");
            var before = repository.LoadLayer(layerId);
            var firstBefore = before.layout.nodes.Single(node => node.nodeId == firstNodeId);
            var plan = new ArchitectorSemanticPlan
            {
                renames =
                {
                    new ArchitectorPlanRename { kind = "entry", target = groupId, name = "Application" },
                    new ArchitectorPlanRename { kind = "entry", target = layerId, name = "Runtime Flow" },
                    new ArchitectorPlanRename { kind = "node", layer = layerId, target = firstNodeId, name = "Request" }
                },
                moves =
                {
                    new ArchitectorPlanMove { layer = layerId, node = firstNodeId, x = 1200f, y = 600f }
                },
                edgeChanges =
                {
                    new ArchitectorPlanEdgeChange { edge = edgeId, kind = ArchitectorEdgeKinds.Event }
                }
            };

            var preview = repository.DryRunSemanticPlan(plan, "Update Existing Architecture");

            Assert.That(preview.Batch.ChangedFileCount, Is.EqualTo(6), "Renamed group and layer files move to new names.");
            Assert.That(repository.FindGroup(groupId).name, Is.EqualTo("Architecture"));
            Assert.That(repository.LoadLayer(layerId).semantic.edges.Single().kind, Is.EqualTo(ArchitectorEdgeKinds.DataFlow));

            repository.ApplySemanticPlan(plan, "Update Existing Architecture");

            var changed = repository.LoadLayer(layerId);
            var firstChanged = changed.layout.nodes.Single(node => node.nodeId == firstNodeId);
            Assert.That(repository.FindGroup(groupId).name, Is.EqualTo("Application"));
            Assert.That(repository.FindLayer(layerId).name, Is.EqualTo("Runtime Flow"));
            Assert.That(changed.semantic.nodes.Single(node => node.id == firstNodeId).title, Is.EqualTo("Request"));
            Assert.That(changed.semantic.edges.Single().kind, Is.EqualTo(ArchitectorEdgeKinds.Event));
            Assert.That(firstChanged.x, Is.Not.EqualTo(firstBefore.x));
            Assert.That(firstChanged.y, Is.Not.EqualTo(firstBefore.y));
            Assert.That(repository.NextUndoName, Is.EqualTo("Update Existing Architecture"));

            repository.Undo();

            var restored = repository.LoadLayer(layerId);
            var firstRestored = restored.layout.nodes.Single(node => node.nodeId == firstNodeId);
            Assert.That(repository.FindGroup(groupId).name, Is.EqualTo("Architecture"));
            Assert.That(repository.FindLayer(layerId).name, Is.EqualTo("Runtime"));
            Assert.That(restored.semantic.nodes.Single(node => node.id == firstNodeId).title, Is.EqualTo("New Node"));
            Assert.That(restored.semantic.edges.Single().kind, Is.EqualTo(ArchitectorEdgeKinds.DataFlow));
            Assert.That(firstRestored.x, Is.EqualTo(firstBefore.x));
            Assert.That(firstRestored.y, Is.EqualTo(firstBefore.y));
        }

        [Test]
        public void SemanticPlanReparentsExplorerEntriesUsingCreatedParentAsOneUndoOperation()
        {
            var existingGroupId = repository.CreateGroup(string.Empty, "Existing Group");
            var firstLayerId = repository.CreateLayer(string.Empty, "First Layer");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second Layer");
            var plan = new ArchitectorSemanticPlan
            {
                groups =
                {
                    new ArchitectorPlanGroup { reference = "root", name = "Root" }
                },
                entryMoves =
                {
                    new ArchitectorPlanEntryMove
                    {
                        entries = { existingGroupId },
                        parent = "root"
                    },
                    new ArchitectorPlanEntryMove
                    {
                        entries = { secondLayerId, firstLayerId },
                        parent = "root",
                        layerIndex = 0
                    }
                }
            };
            var revision = repository.Revision;

            var preview = repository.DryRunSemanticPlan(plan, "Organize Explorer Entries");

            Assert.That(repository.Revision, Is.EqualTo(revision));
            Assert.That(repository.FindGroup(existingGroupId).parentGroupId, Is.Empty);

            var applied = repository.ApplySemanticPlan(plan, "Organize Explorer Entries");
            var rootId = applied.References["root"];
            var root = repository.FindGroup(rootId);

            Assert.That(root.childGroupIds, Is.EqualTo(new[] { existingGroupId }));
            Assert.That(root.layerIds, Is.EqualTo(new[] { secondLayerId, firstLayerId }));
            Assert.That(repository.FindGroup(existingGroupId).parentGroupId, Is.EqualTo(rootId));
            Assert.That(repository.FindLayer(firstLayerId).parentGroupId, Is.EqualTo(rootId));
            Assert.That(repository.NextUndoName, Is.EqualTo("Organize Explorer Entries"));

            repository.Undo();

            Assert.That(repository.FindGroup(rootId), Is.Null);
            Assert.That(repository.FindGroup(existingGroupId).parentGroupId, Is.Empty);
            Assert.That(repository.Project.rootLayerIds, Is.EqualTo(new[] { firstLayerId, secondLayerId }));
        }

        [Test]
        public void SemanticPlanRejectsExplorerGroupMoveIntoDescendant()
        {
            var parentId = repository.CreateGroup(string.Empty, "Parent");
            var childId = repository.CreateGroup(parentId, "Child");
            var plan = new ArchitectorSemanticPlan
            {
                entryMoves =
                {
                    new ArchitectorPlanEntryMove
                    {
                        entries = { parentId },
                        parent = childId
                    }
                }
            };

            Assert.Throws<ArchitectorDataException>(() => repository.DryRunSemanticPlan(plan));
            Assert.That(repository.FindGroup(parentId).parentGroupId, Is.Empty);
            Assert.That(repository.FindGroup(childId).parentGroupId, Is.EqualTo(parentId));
        }

        [Test]
        public void SemanticPlanDeletingLastRelationshipRemovesPairWithoutTouchingTheSessionOnDryRun()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Source Layer");
            var targetLayerId = repository.CreateLayer(string.Empty, "Target Layer");
            var sourceNode = repository.CreateEmptyNode(sourceLayerId, 0f, 0f);
            var targetNode = repository.CreateEmptyNode(targetLayerId, 0f, 0f);
            repository.CreateCrossLayerEdge(sourceLayerId, sourceNode, targetLayerId, targetNode, ArchitectorEdgeKinds.Dependency);
            var edgeId = repository.LoadLayer(sourceLayerId).semantic.edges.Single(edge => edge.targetLayerId == targetLayerId).id;
            var plan = new ArchitectorSemanticPlan
            {
                deletions =
                {
                    new ArchitectorPlanDeletion { kind = "edge", target = edgeId }
                }
            };
            int PortalCount(string layerId) =>
                repository.LoadLayer(layerId).semantic.nodes.Count(node => node.kind == ArchitectorNodeKinds.LayerPortal);

            repository.DryRunSemanticPlan(plan);
            repository.DryRunSemanticPlan(plan);

            Assert.That(PortalCount(sourceLayerId), Is.EqualTo(1));
            Assert.That(PortalCount(targetLayerId), Is.EqualTo(1));

            repository.ApplySemanticPlan(plan);

            Assert.That(PortalCount(sourceLayerId), Is.Zero);
            Assert.That(PortalCount(targetLayerId), Is.Zero);
            Assert.That(Directory.GetFiles(Path.Combine(temporaryRoot, "Architector", "Links")), Is.Empty);

            repository.Undo();

            Assert.That(PortalCount(sourceLayerId), Is.EqualTo(1));
            Assert.That(PortalCount(targetLayerId), Is.EqualTo(1));
        }

        [Test]
        public void SemanticPlanRecursiveEntryDeletionCleansPortalsAndUndoRestoresEverything()
        {
            var groupId = repository.CreateGroup(string.Empty, "Disposable");
            var deletedLayerId = repository.CreateLayer(groupId, "Deleted Layer");
            var survivingLayerId = repository.CreateLayer(string.Empty, "Surviving Layer");
            var pair = repository.CreateLayerPortalPair(deletedLayerId, survivingLayerId, 0f, 0f);
            var deletedLayer = repository.FindLayer(deletedLayerId);
            var semanticPath = repository.Paths.ToAbsoluteDataPath(deletedLayer.semanticPath);
            var plan = new ArchitectorSemanticPlan
            {
                deletions =
                {
                    new ArchitectorPlanDeletion { kind = "entry", target = groupId }
                }
            };

            repository.ApplySemanticPlan(plan, "Delete Architecture Branch");

            Assert.That(repository.FindGroup(groupId), Is.Null);
            Assert.That(repository.FindLayer(deletedLayerId), Is.Null);
            Assert.That(File.Exists(semanticPath), Is.False);
            Assert.That(repository.LoadLayer(survivingLayerId).semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.False);
            Assert.That(repository.NextUndoName, Is.EqualTo("Delete Architecture Branch"));

            repository.Undo();

            Assert.That(repository.FindGroup(groupId), Is.Not.Null);
            Assert.That(repository.FindLayer(deletedLayerId), Is.Not.Null);
            Assert.That(File.Exists(semanticPath), Is.True);
            Assert.That(repository.LoadLayer(survivingLayerId).semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.True);
            Assert.That(repository.LoadLayer(deletedLayerId).semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.True);
        }

        [Test]
        public void CanonicalBatchRejectsBrokenPortalPairWithoutWriting()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Source");
            var targetLayerId = repository.CreateLayer(string.Empty, "Target");
            var pair = repository.CreateLayerPortalPair(sourceLayerId, targetLayerId, 0f, 0f);
            var linkPath = ArchitectorPaths.LinkPath(pair.PairId);
            var original = File.ReadAllText(DataPath(linkPath));
            var link = ArchitectorJson.Deserialize<ArchitectorStoredLinkDocument>(original);
            link.endpoints.RemoveAt(1);
            var batch = new ArchitectorCanonicalBatch()
                .SetDocument(linkPath, ArchitectorJson.Serialize(link));

            var exception = Assert.Throws<ArchitectorDataException>(() => repository.DryRunCanonicalBatch(batch));

            Assert.That(exception.Message, Does.Contain("exactly two Portal endpoints"));
            Assert.That(File.ReadAllText(DataPath(linkPath)), Is.EqualTo(original));
        }

        [Test]
        public void ExternalReloadValidatesBeforeReplacingSessionAndClearsUndoHistory()
        {
            repository.CreateGroup(string.Empty, "Existing Group");
            WriteExternalGroup("group_external", "External Group");
            var changedNotifications = 0;
            repository.Changed += () => changedNotifications++;

            var preview = repository.ValidateExternalChanges();

            Assert.That(preview.LayerCount, Is.Zero);
            Assert.That(repository.FindGroup("group_external"), Is.Null);
            Assert.That(repository.CanUndo, Is.True);

            repository.ReloadExternalChanges();

            Assert.That(repository.FindGroup("group_external"), Is.Not.Null);
            Assert.That(repository.CanUndo, Is.False);
            Assert.That(repository.CanRedo, Is.False);
            Assert.That(changedNotifications, Is.EqualTo(1));
        }

        [Test]
        public void RecursiveDeleteCanBeUndoneAndRedone()
        {
            var rootGroupId = repository.CreateGroup(string.Empty, "Gameplay");
            var layerId = repository.CreateLayer(rootGroupId, "Interaction");
            var nestedGroupId = repository.WrapLayerInNewGroup(layerId, "Interaction Group");
            var layer = repository.FindLayer(layerId);
            var semanticPath = repository.Paths.ToAbsoluteDataPath(layer.semanticPath);
            var layoutPath = repository.Paths.ToAbsoluteDataPath(layer.layoutPath);

            Assert.That(repository.FindGroup(nestedGroupId), Is.Not.Null);
            Assert.That(File.Exists(semanticPath), Is.True);
            Assert.That(File.Exists(layoutPath), Is.True);

            repository.DeleteEntry(rootGroupId);
            Assert.That(repository.FindGroup(rootGroupId), Is.Null);
            Assert.That(repository.FindLayer(layerId), Is.Null);
            Assert.That(File.Exists(semanticPath), Is.False);
            Assert.That(File.Exists(layoutPath), Is.False);

            repository.Undo();
            Assert.That(repository.FindGroup(rootGroupId), Is.Not.Null);
            Assert.That(repository.FindGroup(nestedGroupId), Is.Not.Null);
            Assert.That(repository.FindLayer(layerId), Is.Not.Null);
            Assert.That(File.Exists(semanticPath), Is.True);
            Assert.That(File.Exists(layoutPath), Is.True);

            repository.Redo();
            Assert.That(repository.FindGroup(rootGroupId), Is.Null);
            Assert.That(repository.FindLayer(layerId), Is.Null);
        }

        [Test]
        public void MultipleExplorerEntriesDeleteAndRestoreAsOneOperation()
        {
            var groupId = repository.CreateGroup(string.Empty, "Gameplay");
            var nestedLayerId = repository.CreateLayer(groupId, "Nested");
            var rootLayerId = repository.CreateLayer(string.Empty, "Root");
            var nestedLayer = repository.FindLayer(nestedLayerId);
            var rootLayer = repository.FindLayer(rootLayerId);

            repository.DeleteEntries(new[] { groupId, nestedLayerId, rootLayerId });

            Assert.That(repository.FindGroup(groupId), Is.Null);
            Assert.That(repository.FindLayer(nestedLayerId), Is.Null);
            Assert.That(repository.FindLayer(rootLayerId), Is.Null);
            Assert.That(File.Exists(repository.Paths.ToAbsoluteDataPath(nestedLayer.semanticPath)), Is.False);
            Assert.That(File.Exists(repository.Paths.ToAbsoluteDataPath(rootLayer.semanticPath)), Is.False);

            repository.Undo();
            Assert.That(repository.FindGroup(groupId), Is.Not.Null);
            Assert.That(repository.FindLayer(nestedLayerId), Is.Not.Null);
            Assert.That(repository.FindLayer(rootLayerId), Is.Not.Null);
            Assert.That(File.Exists(repository.Paths.ToAbsoluteDataPath(nestedLayer.semanticPath)), Is.True);
            Assert.That(File.Exists(repository.Paths.ToAbsoluteDataPath(rootLayer.semanticPath)), Is.True);

            repository.Redo();
            Assert.That(repository.FindGroup(groupId), Is.Null);
            Assert.That(repository.FindLayer(nestedLayerId), Is.Null);
            Assert.That(repository.FindLayer(rootLayerId), Is.Null);
            Assert.That(File.Exists(repository.Paths.ToAbsoluteDataPath(nestedLayer.semanticPath)), Is.False);
            Assert.That(File.Exists(repository.Paths.ToAbsoluteDataPath(rootLayer.semanticPath)), Is.False);
        }

        [Test]
        public void NamesAreUniqueAmongSiblingsIgnoringCase()
        {
            var groupId = repository.CreateGroup(string.Empty, "Gameplay");
            var layerId = repository.CreateLayer(string.Empty, "gameplay");
            var nestedLayerId = repository.CreateLayer(groupId, "Gameplay");
            var sharedRootId = repository.CreateLayer(string.Empty, "Shared");
            repository.CreateLayer(groupId, "Shared");

            Assert.That(repository.FindLayer(layerId).name, Is.EqualTo("gameplay 2"));
            Assert.That(repository.FindLayer(nestedLayerId).name, Is.EqualTo("Gameplay"));
            Assert.That(repository.GetEntryPath(nestedLayerId), Is.EqualTo("Gameplay/Gameplay"));
            Assert.Throws<ArchitectorDataException>(() => repository.RenameEntry(groupId, "GAMEPLAY 2"));
            var exception = Assert.Throws<ArchitectorDataException>(() =>
                repository.MoveEntries(new[] { sharedRootId }, groupId));
            Assert.That(exception.Message, Does.Contain("Duplicate group or layer name 'Shared' in group 'Gameplay'"));
            Assert.That(repository.FindLayer(sharedRootId).parentGroupId, Is.Empty);
        }

        [Test]
        public void SemanticPlanResolvesExistingEntriesByExplorerPath()
        {
            var coreId = repository.CreateGroup(string.Empty, "Core");
            var playerId = repository.CreateGroup(coreId, "Player");
            var movementId = repository.CreateLayer(playerId, "Movement");
            var plan = new ArchitectorSemanticPlan
            {
                layers = { new ArchitectorPlanLayer { reference = "camera", name = "Camera", parent = "Core/Player" } },
                nodes = { new ArchitectorPlanNode { reference = "body", layer = "core/player/movement", title = "Body" } }
            };

            var applied = repository.ApplySemanticPlan(plan, "Extend Player");

            Assert.That(repository.FindLayer(applied.References["camera"]).parentGroupId, Is.EqualTo(playerId));
            Assert.That(repository.LoadLayer(movementId).semantic.nodes.Single().title, Is.EqualTo("Body"));

            repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                renames = { new ArchitectorPlanRename { kind = "entry", target = "Core/Player/Movement", name = "Locomotion" } },
                deletions = { new ArchitectorPlanDeletion { kind = "entry", target = "Core/Player/Camera" } }
            });
            Assert.That(repository.FindLayer(movementId).name, Is.EqualTo("Locomotion"));
            Assert.That(repository.FindLayer(applied.References["camera"]), Is.Null);
        }

        [Test]
        public void CreatedLayersOwnMatchingSemanticAndLayoutDocuments()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var layer = repository.FindLayer(layerId);
            var semantic = ArchitectorJson.Read<ArchitectorStoredLayerDocument>(repository.Paths.ToAbsoluteDataPath(layer.semanticPath));
            var layout = ArchitectorJson.Read<ArchitectorStoredLayoutDocument>(repository.Paths.ToAbsoluteDataPath(layer.layoutPath));

            Assert.That(semantic.layerId, Is.EqualTo(layerId));
            Assert.That(semantic.name, Is.EqualTo("Architecture"));
            Assert.That(ArchitectorOrderKeys.IsValid(semantic.orderKey), Is.True);
            Assert.That(layout.layerId, Is.EqualTo(layerId));
            Assert.That(repository.Project.rootLayerIds.Single(), Is.EqualTo(layerId));
        }

        [Test]
        public void EmptyNodesPersistWithUniqueNamesAndUndo()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var firstNodeId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var secondNodeId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var documents = repository.LoadLayer(layerId);

            Assert.That(documents.semantic.nodes.Single(node => node.id == firstNodeId).title, Is.EqualTo("New Node"));
            Assert.That(documents.semantic.nodes.Single(node => node.id == secondNodeId).title, Is.EqualTo("New Node 2"));
            var firstLayout = documents.layout.nodes.Single(node => node.nodeId == firstNodeId);
            var secondLayout = documents.layout.nodes.Single(node => node.nodeId == secondNodeId);
            Assert.That(new[] { firstLayout.x, firstLayout.y }, Is.Not.EqualTo(new[] { secondLayout.x, secondLayout.y }));

            repository.DeleteNode(layerId, firstNodeId);
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Any(node => node.id == firstNodeId), Is.False);

            repository.Undo();
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Any(node => node.id == firstNodeId), Is.True);
        }

        [Test]
        public void LayerPortalsAreCreatedAsOnePairedUndoableOperation()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Gameplay");
            var targetLayerId = repository.CreateLayer(string.Empty, "Infrastructure");

            var pair = repository.CreateLayerPortalPair(sourceLayerId, targetLayerId, 150f, 225f);
            var sourcePortal = repository.LoadLayer(sourceLayerId).semantic.nodes.Single(node => node.id == pair.SourceNodeId);
            var targetPortal = repository.LoadLayer(targetLayerId).semantic.nodes.Single(node => node.id == pair.TargetNodeId);

            Assert.That(sourcePortal.kind, Is.EqualTo(ArchitectorNodeKinds.LayerPortal));
            Assert.That(sourcePortal.title, Is.EqualTo("Infrastructure"));
            Assert.That(sourcePortal.portal.pairId, Is.EqualTo(pair.PairId));
            Assert.That(sourcePortal.portal.targetLayerId, Is.EqualTo(targetLayerId));
            Assert.That(sourcePortal.portal.counterpartNodeId, Is.EqualTo(pair.TargetNodeId));
            Assert.That(sourcePortal.portal.role, Is.EqualTo(ArchitectorPortalRoles.Exit));
            Assert.That(targetPortal.title, Is.EqualTo("Gameplay"));
            Assert.That(targetPortal.portal.pairId, Is.EqualTo(pair.PairId));
            Assert.That(targetPortal.portal.targetLayerId, Is.EqualTo(sourceLayerId));
            Assert.That(targetPortal.portal.counterpartNodeId, Is.EqualTo(pair.SourceNodeId));
            Assert.That(targetPortal.portal.role, Is.EqualTo(ArchitectorPortalRoles.Entry));

            repository.Undo();
            Assert.That(repository.LoadLayer(sourceLayerId).semantic.nodes, Is.Empty);
            Assert.That(repository.LoadLayer(targetLayerId).semantic.nodes, Is.Empty);
            repository.Redo();
            Assert.That(repository.LoadLayer(sourceLayerId).semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.True);
            Assert.That(repository.LoadLayer(targetLayerId).semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.True);
        }

        [Test]
        public void DeletingEitherPortalRemovesItsCounterpartAndCrossLayerRelationships()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Gameplay");
            var targetLayerId = repository.CreateLayer(string.Empty, "Infrastructure");
            var pair = repository.CreateLayerPortalPair(sourceLayerId, targetLayerId, 0f, 0f);
            var sourceNodeId = repository.CreateEmptyNode(sourceLayerId, 600f, 0f);
            var targetNodeId = repository.CreateEmptyNode(targetLayerId, 600f, 0f);
            repository.CreateCrossLayerEdge(
                sourceLayerId,
                sourceNodeId,
                targetLayerId,
                targetNodeId,
                ArchitectorEdgeKinds.Event);

            repository.DeleteNode(sourceLayerId, pair.SourceNodeId);

            var source = repository.LoadLayer(sourceLayerId);
            var target = repository.LoadLayer(targetLayerId);
            Assert.That(source.semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.False);
            Assert.That(target.semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.False);
            Assert.That(source.semantic.edges, Is.Empty);
            Assert.That(target.semantic.edges, Is.Empty);

            repository.Undo();
            Assert.That(repository.LoadLayer(sourceLayerId).semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.True);
            Assert.That(repository.LoadLayer(targetLayerId).semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.True);
            Assert.That(repository.LoadLayer(sourceLayerId).semantic.edges, Has.Count.EqualTo(1));
            Assert.That(repository.LoadLayer(targetLayerId).semantic.edges, Is.Empty);
        }

        [Test]
        public void CrossLayerRelationshipsAreStoredOnceAndProjectedThroughThePortalPair()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Source");
            var targetLayerId = repository.CreateLayer(string.Empty, "Target");
            var firstSourceId = repository.CreateEmptyNode(sourceLayerId, 0f, 0f);
            var secondSourceId = repository.CreateEmptyNode(sourceLayerId, 0f, 300f);
            var firstTargetId = repository.CreateEmptyNode(targetLayerId, 600f, 0f);
            var secondTargetId = repository.CreateEmptyNode(targetLayerId, 600f, 300f);

            var first = repository.CreateCrossLayerEdge(sourceLayerId, firstSourceId, targetLayerId, firstTargetId, ArchitectorEdgeKinds.DataFlow);
            var second = repository.CreateCrossLayerEdge(sourceLayerId, firstSourceId, targetLayerId, secondTargetId, ArchitectorEdgeKinds.Event);
            var third = repository.CreateCrossLayerEdge(sourceLayerId, secondSourceId, targetLayerId, firstTargetId, ArchitectorEdgeKinds.Callback);

            Assert.That(repository.LoadLayer(sourceLayerId).semantic.edges.Select(edge => edge.id),
                Is.EquivalentTo(new[] { first, second, third }));
            Assert.That(repository.LoadLayer(targetLayerId).semantic.edges, Is.Empty);
            Assert.That(repository.LoadLayerView(sourceLayerId).semantic.edges.Select(edge => edge.id),
                Is.EquivalentTo(new[] { first, second, third }));
            Assert.That(repository.LoadLayerView(targetLayerId).semantic.edges.Select(edge => edge.id),
                Is.EquivalentTo(new[] { first, second, third }));
        }

        [Test]
        public void PortalRelationshipIndexIsSharedWithinRevisionAndRebuiltAfterVisibilityChange()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Source");
            var targetLayerId = repository.CreateLayer(string.Empty, "Target");
            var sourceNodeId = repository.CreateEmptyNode(sourceLayerId, 0f, 0f);
            var targetNodeId = repository.CreateEmptyNode(targetLayerId, 600f, 0f);
            repository.RenameEmptyNode(sourceLayerId, sourceNodeId, "Sender");
            repository.RenameEmptyNode(targetLayerId, targetNodeId, "Receiver");
            var edgeId = repository.CreateCrossLayerEdge(
                sourceLayerId,
                sourceNodeId,
                targetLayerId,
                targetNodeId,
                ArchitectorEdgeKinds.Event);
            var sourcePortalId = repository.LoadLayer(sourceLayerId).semantic.nodes.Single(node =>
                node.kind == ArchitectorNodeKinds.LayerPortal).id;
            var targetPortalId = repository.LoadLayer(targetLayerId).semantic.nodes.Single(node =>
                node.kind == ArchitectorNodeKinds.LayerPortal).id;

            var firstSourceSnapshot = repository.GetLayerPortalRelationshipRows(sourceLayerId);
            var secondSourceSnapshot = repository.GetLayerPortalRelationshipRows(sourceLayerId);
            var sourceRow = firstSourceSnapshot[sourcePortalId].Single();
            var targetRow = repository.GetLayerPortalRelationshipRows(targetLayerId)[targetPortalId].Single();

            Assert.That(secondSourceSnapshot, Is.SameAs(firstSourceSnapshot));
            Assert.That(sourceRow, Is.SameAs(targetRow));
            Assert.That(sourceRow.Relationship.id, Is.EqualTo(edgeId));
            Assert.That(sourceRow.Caption, Is.EqualTo("Sender → Receiver"));

            repository.SetRelationshipVisibility(sourceLayerId, ArchitectorRelationshipVisibility.Dependency);

            var refreshedSourceSnapshot = repository.GetLayerPortalRelationshipRows(sourceLayerId);
            Assert.That(refreshedSourceSnapshot, Is.Not.SameAs(firstSourceSnapshot));
            Assert.That(refreshedSourceSnapshot[sourcePortalId], Is.Empty);
            Assert.That(repository.GetLayerPortalRelationshipRows(sourceLayerId, false)[sourcePortalId].Count, Is.EqualTo(1));
            Assert.That(repository.GetLayerPortalRelationshipRows(targetLayerId)[targetPortalId].Count, Is.EqualTo(1));
        }

        [Test]
        public void DependencyCycleAcrossThreeLayersIsRejectedButRuntimeCycleIsAllowed()
        {
            var firstLayer = repository.CreateLayer(string.Empty, "First");
            var secondLayer = repository.CreateLayer(string.Empty, "Second");
            var thirdLayer = repository.CreateLayer(string.Empty, "Third");
            var firstNode = repository.CreateEmptyNode(firstLayer, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondLayer, 0f, 0f);
            var thirdNode = repository.CreateEmptyNode(thirdLayer, 0f, 0f);
            repository.CreateCrossLayerEdge(firstLayer, firstNode, secondLayer, secondNode, ArchitectorEdgeKinds.Dependency);
            repository.CreateCrossLayerEdge(secondLayer, secondNode, thirdLayer, thirdNode, ArchitectorEdgeKinds.Dependency);

            Assert.Throws<ArchitectorDataException>(() => repository.CreateCrossLayerEdge(
                thirdLayer, thirdNode, firstLayer, firstNode, ArchitectorEdgeKinds.Dependency));
            Assert.DoesNotThrow(() => repository.CreateCrossLayerEdge(
                thirdLayer, thirdNode, firstLayer, firstNode, ArchitectorEdgeKinds.Event));
        }

        [Test]
        public void RejectedDependencyCycleNamesItsPath()
        {
            var firstLayer = repository.CreateLayer(string.Empty, "First");
            var secondLayer = repository.CreateLayer(string.Empty, "Second");
            var firstNode = repository.CreateEmptyNode(firstLayer, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondLayer, 0f, 0f);
            repository.CreateCrossLayerEdge(firstLayer, firstNode, secondLayer, secondNode, ArchitectorEdgeKinds.Dependency);

            var exception = Assert.Throws<ArchitectorDataException>(() => repository.CreateCrossLayerEdge(
                secondLayer, secondNode, firstLayer, firstNode, ArchitectorEdgeKinds.Dependency));

            Assert.That(exception.Message, Does.Contain("Cycle: First/New Node → Second/New Node → First/New Node"));
            Assert.That(repository.FindDependencyCycles(), Is.Empty);
        }

        [Test]
        public void ReportedDependencyCyclesAreSavedWithinAndAcrossLayersAndCanBeApproved()
        {
            repository.UpdateSettings("Report Cycles", settings =>
                settings.rules.dependencyCycles = ArchitectorDependencyCyclePolicy.Report);
            var localLayer = repository.CreateLayer(string.Empty, "Local");
            var localFirst = repository.CreateEmptyNode(localLayer, 0f, 0f);
            var localSecond = repository.CreateEmptyNode(localLayer, 600f, 0f);
            repository.CreateEdge(localLayer, localFirst, localSecond, ArchitectorEdgeKinds.Dependency, "left");
            repository.CreateEdge(localLayer, localSecond, localFirst, ArchitectorEdgeKinds.Dependency, "right");
            var firstLayer = repository.CreateLayer(string.Empty, "First");
            var secondLayer = repository.CreateLayer(string.Empty, "Second");
            var thirdLayer = repository.CreateLayer(string.Empty, "Third");
            var firstNode = repository.CreateEmptyNode(firstLayer, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondLayer, 0f, 0f);
            var thirdNode = repository.CreateEmptyNode(thirdLayer, 0f, 0f);
            repository.CreateCrossLayerEdge(firstLayer, firstNode, secondLayer, secondNode, ArchitectorEdgeKinds.Dependency);
            repository.CreateCrossLayerEdge(secondLayer, secondNode, thirdLayer, thirdNode, ArchitectorEdgeKinds.Dependency);
            var closing = repository.CreateCrossLayerEdge(thirdLayer, thirdNode, firstLayer, firstNode, ArchitectorEdgeKinds.Dependency);

            var cycles = repository.FindDependencyCycles();
            Assert.That(cycles.Count, Is.EqualTo(2));
            var local = cycles.Single(cycle => cycle.LayerIds.Count == 1);
            var crossing = cycles.Single(cycle => cycle.LayerIds.Count == 3);
            Assert.That(local.Path.Count, Is.EqualTo(2));
            Assert.That(crossing.Members.Count, Is.EqualTo(3));
            Assert.That(crossing.Path.Count, Is.EqualTo(3));
            Assert.That(crossing.Path.Select(edge => edge.id), Does.Contain(closing));
            Assert.That(repository.DescribeDependencyCycle(crossing), Does.Contain("Third/New Node → First/New Node"));

            repository.ApproveLayers(new[] { localLayer, firstLayer, secondLayer, thirdLayer });
            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(reloaded.FindDependencyCycles().Count, Is.EqualTo(2));
            Assert.That(reloaded.GetLayerApprovalStatus(firstLayer).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
        }

        [Test]
        public void RejectRuleReturnsOnlyAfterTheCyclesAreRemoved()
        {
            repository.UpdateSettings("Report Cycles", settings =>
                settings.rules.dependencyCycles = ArchitectorDependencyCyclePolicy.Report);
            var layerId = repository.CreateLayer(string.Empty, "Local");
            var first = repository.CreateEmptyNode(layerId, 0f, 0f);
            var second = repository.CreateEmptyNode(layerId, 600f, 0f);
            repository.CreateEdge(layerId, first, second, ArchitectorEdgeKinds.Dependency, "left");
            var back = repository.CreateEdge(layerId, second, first, ArchitectorEdgeKinds.Dependency, "right");

            Assert.Throws<ArchitectorDataException>(() => repository.UpdateSettings("Reject Cycles", settings =>
                settings.rules.dependencyCycles = ArchitectorDependencyCyclePolicy.Reject));
            Assert.That(repository.Settings.rules.dependencyCycles, Is.EqualTo(ArchitectorDependencyCyclePolicy.Report));

            var settingsPath = Path.Combine(temporaryRoot, "Architector", ArchitectorPaths.SettingsFileName);
            var reported = File.ReadAllText(settingsPath);
            var rejected = JObject.Parse(reported);
            rejected["rules"]["dependencyCycles"] = 0;
            File.WriteAllText(settingsPath, rejected.ToString());
            Assert.Throws<ArchitectorDataException>(() => repository.ValidateExternalChanges());
            File.WriteAllText(settingsPath, reported);

            repository.DeleteEdge(layerId, back);
            repository.UpdateSettings("Reject Cycles", settings =>
                settings.rules.dependencyCycles = ArchitectorDependencyCyclePolicy.Reject);
            Assert.That(repository.Settings.rules.dependencyCycles, Is.EqualTo(ArchitectorDependencyCyclePolicy.Reject));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateEdge(
                layerId, second, first, ArchitectorEdgeKinds.Dependency, "right"));
        }

        [Test]
        public void SettingsWithoutRulesKeepRejectingCycles()
        {
            var settingsPath = Path.Combine(temporaryRoot, "Architector", ArchitectorPaths.SettingsFileName);
            var settings = JObject.Parse(File.ReadAllText(settingsPath));
            Assert.That(settings.Remove("rules"), Is.True);
            File.WriteAllText(settingsPath, settings.ToString());

            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));

            Assert.That(reloaded.Settings.rules.dependencyCycles, Is.EqualTo(ArchitectorDependencyCyclePolicy.Reject));
        }

        [Test]
        public void CrossLayerChangeAffectsOnlyTheOwnerApprovalAndCreationEnablesHiddenKind()
        {
            var sourceLayer = repository.CreateLayer(string.Empty, "Source");
            var targetLayer = repository.CreateLayer(string.Empty, "Target");
            var sourceNode = repository.CreateEmptyNode(sourceLayer, 0f, 0f);
            var targetNode = repository.CreateEmptyNode(targetLayer, 0f, 0f);
            repository.ApproveLayers(new[] { sourceLayer, targetLayer });
            repository.SetRelationshipVisibility(targetLayer, ArchitectorRelationshipVisibility.Dependency);

            repository.CreateCrossLayerEdge(
                sourceLayer, sourceNode, targetLayer, targetNode, ArchitectorEdgeKinds.Event);

            Assert.That(repository.GetLayerApprovalStatus(sourceLayer).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            Assert.That(repository.GetLayerApprovalStatus(targetLayer).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            Assert.That(repository.LoadLayer(targetLayer).layout.visibleRelationshipKinds
                        & ArchitectorRelationshipVisibility.Event, Is.Not.Zero);
        }

        [Test]
        public void RemovingTheLastRelationshipRemovesThePortalPair()
        {
            var firstLayerId = repository.CreateLayer(string.Empty, "First");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second");
            var firstNode = repository.CreateEmptyNode(firstLayerId, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondLayerId, 0f, 0f);
            var edgeId = repository.CreateCrossLayerEdge(firstLayerId, firstNode, secondLayerId, secondNode, ArchitectorEdgeKinds.Dependency);
            Assert.That(repository.LoadLayer(firstLayerId).semantic.nodes.Count(node => node.kind == ArchitectorNodeKinds.LayerPortal), Is.EqualTo(1));

            repository.DeleteEdge(firstLayerId, edgeId);

            Assert.That(repository.LoadLayer(firstLayerId).semantic.nodes.Any(node => node.kind == ArchitectorNodeKinds.LayerPortal), Is.False);
            Assert.That(repository.LoadLayer(secondLayerId).semantic.nodes.Any(node => node.kind == ArchitectorNodeKinds.LayerPortal), Is.False);
        }

        [Test]
        public void ManuallyCreatedPortalPairWaitsForItsFirstRelationship()
        {
            var firstLayerId = repository.CreateLayer(string.Empty, "First");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second");
            var pair = repository.CreateLayerPortalPair(firstLayerId, secondLayerId, 0f, 0f);

            repository.CreateEmptyNode(firstLayerId, 600f, 0f);

            Assert.That(repository.LoadLayer(firstLayerId).semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.True);
            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            reloaded.CreateEmptyNode(firstLayerId, 1200f, 0f);
            Assert.That(reloaded.LoadLayer(firstLayerId).semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.False, "An unconnected pair from an earlier session is lonely.");
        }

        [Test]
        public void PlanCannotLeaveAPortalPairWithoutRelationships()
        {
            var firstLayerId = repository.CreateLayer(string.Empty, "First");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second");

            repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                portals = { new ArchitectorPlanPortal { exitReference = "exit", entryReference = "entry", fromLayer = firstLayerId, toLayer = secondLayerId } }
            });

            Assert.That(repository.LoadLayer(firstLayerId).semantic.nodes.Any(node => node.kind == ArchitectorNodeKinds.LayerPortal), Is.False);
            Assert.That(repository.LoadLayer(secondLayerId).semantic.nodes.Any(node => node.kind == ArchitectorNodeKinds.LayerPortal), Is.False);
        }

        [Test]
        public void SemanticPlanEdgeCreatesTheMissingPortalPairOnce()
        {
            var firstLayerId = repository.CreateLayer(string.Empty, "First");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second");
            var firstNode = repository.CreateEmptyNode(firstLayerId, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondLayerId, 0f, 0f);
            var plan = new ArchitectorSemanticPlan
            {
                edges =
                {
                    new ArchitectorPlanEdge { sourceLayer = firstLayerId, source = firstNode, targetLayer = secondLayerId, target = secondNode, kind = ArchitectorEdgeKinds.Dependency },
                    new ArchitectorPlanEdge { sourceLayer = secondLayerId, source = secondNode, targetLayer = firstLayerId, target = firstNode, kind = ArchitectorEdgeKinds.Event }
                }
            };
            int PortalCount(string layerId) =>
                repository.LoadLayer(layerId).semantic.nodes.Count(node => node.kind == ArchitectorNodeKinds.LayerPortal);

            repository.DryRunSemanticPlan(plan);
            Assert.That(PortalCount(firstLayerId), Is.Zero);

            repository.ApplySemanticPlan(plan);

            Assert.That(PortalCount(firstLayerId), Is.EqualTo(1));
            Assert.That(PortalCount(secondLayerId), Is.EqualTo(1));
            Assert.That(repository.LoadLayer(firstLayerId).semantic.edges.Count(edge => edge.targetLayerId == secondLayerId), Is.EqualTo(1));
            Assert.That(repository.LoadLayer(secondLayerId).semantic.edges.Count(edge => edge.targetLayerId == firstLayerId), Is.EqualTo(1));
        }

        [Test]
        public void SemanticPlanPortalEntryPlacesAnExistingPair()
        {
            var firstLayerId = repository.CreateLayer(string.Empty, "First");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second");
            var firstNode = repository.CreateEmptyNode(firstLayerId, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondLayerId, 0f, 0f);
            repository.CreateCrossLayerEdge(firstLayerId, firstNode, secondLayerId, secondNode, ArchitectorEdgeKinds.Dependency);

            var result = repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                portals = { new ArchitectorPlanPortal { exitReference = "exit", entryReference = "entry", fromLayer = secondLayerId, toLayer = firstLayerId, exitX = 960f, entryX = 1280f } }
            });

            var firstPortal = repository.LoadLayer(firstLayerId).semantic.nodes.Single(node => node.kind == ArchitectorNodeKinds.LayerPortal);
            var secondPortal = repository.LoadLayer(secondLayerId).semantic.nodes.Single(node => node.kind == ArchitectorNodeKinds.LayerPortal);
            Assert.That(result.References["exit"], Is.EqualTo(secondPortal.id));
            Assert.That(result.References["entry"], Is.EqualTo(firstPortal.id));
            Assert.That(repository.LoadLayer(secondLayerId).layout.nodes.Single(node => node.nodeId == secondPortal.id).x, Is.EqualTo(960f));
            Assert.That(repository.LoadLayer(firstLayerId).layout.nodes.Single(node => node.nodeId == firstPortal.id).x, Is.EqualTo(1280f));
        }

        [Test]
        public void MovingNodesWritesOnlyLayoutAndSurvivesReloadAndUndo()
        {
            var firstLayerId = repository.CreateLayer(string.Empty, "First");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second");
            var firstNode = repository.CreateEmptyNode(firstLayerId, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(secondLayerId, 0f, 0f);
            repository.CreateCrossLayerEdge(firstLayerId, firstNode, secondLayerId, secondNode, ArchitectorEdgeKinds.Dependency);
            var portalId = repository.LoadLayer(firstLayerId).semantic.nodes.Single(node => node.kind == ArchitectorNodeKinds.LayerPortal).id;
            var semanticRevision = repository.SemanticRevision;
            var revision = repository.Revision;

            repository.MoveNodes(firstLayerId, new Dictionary<string, ArchitectorPoint>
            {
                [firstNode] = new ArchitectorPoint(960f, 640f),
                [portalId] = new ArchitectorPoint(1920f, 1280f)
            });

            Assert.That(repository.Revision, Is.EqualTo(revision + 1));
            Assert.That(repository.SemanticRevision, Is.EqualTo(semanticRevision));
            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot)).LoadLayer(firstLayerId).layout;
            Assert.That(reloaded.nodes.Single(node => node.nodeId == firstNode).x, Is.EqualTo(960f));
            Assert.That(reloaded.nodes.Single(node => node.nodeId == portalId).x, Is.EqualTo(1920f));

            repository.Undo();

            Assert.That(repository.LoadLayer(firstLayerId).layout.nodes.Single(node => node.nodeId == firstNode).x, Is.Not.EqualTo(960f));
            Assert.That(repository.LoadLayer(firstLayerId).layout.nodes.Single(node => node.nodeId == portalId).x, Is.Not.EqualTo(1920f));
        }

        [Test]
        public void DeletingPortalDestinationLayerCleansTheSurvivingCounterpart()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Gameplay");
            var targetLayerId = repository.CreateLayer(string.Empty, "Infrastructure");
            var pair = repository.CreateLayerPortalPair(sourceLayerId, targetLayerId, 0f, 0f);

            repository.DeleteEntry(targetLayerId);

            Assert.That(repository.FindLayer(targetLayerId), Is.Null);
            Assert.That(repository.LoadLayer(sourceLayerId).semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.False);
            repository.Undo();
            Assert.That(repository.FindLayer(targetLayerId), Is.Not.Null);
            Assert.That(repository.LoadLayer(sourceLayerId).semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.True);
            Assert.That(repository.LoadLayer(targetLayerId).semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.True);
        }

        [Test]
        public void PortalPairIsStoredInLinkDocumentWithoutRewritingLayerDocuments()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Gameplay");
            var targetLayerId = repository.CreateLayer(string.Empty, "Core");
            var sourceNodeId = repository.CreateEmptyNode(sourceLayerId, 0f, 0f);
            var targetNodeId = repository.CreateEmptyNode(targetLayerId, 0f, 0f);
            var sourceLayerFile = ReadData(ArchitectorPaths.LayerPath(sourceLayerId));
            var targetLayerFile = ReadData(ArchitectorPaths.LayerPath(targetLayerId));
            var targetLayoutFile = ReadData(ArchitectorPaths.LayoutPath(targetLayerId));

            var pair = repository.CreateLayerPortalPair(sourceLayerId, targetLayerId, 600f, 0f);
            var edgeId = repository.CreateCrossLayerEdge(
                sourceLayerId, sourceNodeId, targetLayerId, targetNodeId, ArchitectorEdgeKinds.Dependency);

            Assert.That(ReadData(ArchitectorPaths.LayerPath(sourceLayerId)), Is.EqualTo(sourceLayerFile));
            Assert.That(ReadData(ArchitectorPaths.LayerPath(targetLayerId)), Is.EqualTo(targetLayerFile));
            Assert.That(ReadData(ArchitectorPaths.LayoutPath(targetLayerId)), Is.EqualTo(targetLayoutFile));
            var link = ArchitectorJson.Deserialize<ArchitectorStoredLinkDocument>(ReadData(ArchitectorPaths.LinkPath(pair.PairId)));
            Assert.That(link.endpoints.Select(endpoint => endpoint.layerId), Is.EquivalentTo(new[] { sourceLayerId, targetLayerId }));
            Assert.That(link.relationships.Single().id, Is.EqualTo(edgeId));

            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            var target = repository.LoadLayer(targetLayerId).semantic;
            var portal = target.nodes.Single(node => node.kind == ArchitectorNodeKinds.LayerPortal);
            Assert.That(portal.id, Is.EqualTo(pair.TargetNodeId));
            Assert.That(portal.portal.role, Is.EqualTo(ArchitectorPortalRoles.Entry));
            Assert.That(repository.LoadLayer(sourceLayerId).semantic.edges.Single().id, Is.EqualTo(edgeId));
            Assert.That(repository.GetPortalRelationshipRows(targetLayerId, pair.TargetNodeId).Single().Relationship.id, Is.EqualTo(edgeId));
        }

        [Test]
        public void NodeResponsibilityExplainsNodeWithoutAffectingApproval()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var nodeId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var otherLayerId = repository.CreateLayer(string.Empty, "Other");
            var pair = repository.CreateLayerPortalPair(layerId, otherLayerId, 600f, 0f);
            repository.ApproveLayers(new[] { layerId });

            repository.UpdateNodeResponsibility(layerId, nodeId, "  Owns seat reservations.\n Host decides.  ");

            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == nodeId).responsibility,
                Is.EqualTo("Owns seat reservations. Host decides."));
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            Assert.Throws<ArchitectorDataException>(() =>
                repository.UpdateNodeResponsibility(layerId, pair.SourceNodeId, "Portal"));
            Assert.Throws<ArchitectorDataException>(() =>
                repository.UpdateNodeResponsibility(layerId, nodeId, new string('x', ArchitectorNodeResponsibility.MaxLength + 1)));

            var manualId = repository.ConvertEmptyNodeToManual(layerId, nodeId);
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == manualId).responsibility,
                Is.EqualTo("Owns seat reservations. Host decides."));

            var applied = repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                nodes = { new ArchitectorPlanNode { reference = "planned", layer = "Runtime", title = "Planned", responsibility = "Planned role.", x = 1200f } },
                nodeContracts = { new ArchitectorPlanNodeContract { layer = "Runtime", node = manualId, responsibility = "Changed role.", mode = "declared", typeName = "NewType" } }
            });
            var nodes = repository.LoadLayer(layerId).semantic.nodes;
            Assert.That(nodes.Single(node => node.id == applied.References["planned"]).responsibility, Is.EqualTo("Planned role."));
            Assert.That(nodes.Single(node => node.id == manualId).responsibility, Is.EqualTo("Changed role."));
            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == manualId).responsibility,
                Is.EqualTo("Changed role."));
        }

        [Test]
        public void ApprovalStoresBaselineDocumentAndRevokeRemovesIt()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            repository.CreateManualNode(layerId, 0f, 0f);
            var layerFile = ReadData(ArchitectorPaths.LayerPath(layerId));

            repository.ApproveLayers(new[] { layerId });

            Assert.That(ReadData(ArchitectorPaths.LayerPath(layerId)), Is.EqualTo(layerFile));
            Assert.That(layerFile, Does.Not.Contain("semanticHash"));
            var baseline = ArchitectorJson.Deserialize<ArchitectorStoredBaselineDocument>(ReadData(ArchitectorPaths.BaselinePath(layerId)));
            Assert.That(baseline.semanticHash, Is.EqualTo(repository.GetLayerApprovalStatus(layerId).CurrentHash));
            Assert.That(baseline.baseline.nodes.Single().title, Is.EqualTo("New Type"));

            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            repository.RevokeLayerApprovals(new[] { layerId });
            Assert.That(File.Exists(DataPath(ArchitectorPaths.BaselinePath(layerId))), Is.False);
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Draft));
        }

        [Test]
        public void BaselineDiffListsChangesAndRevertRestoresApprovedVersion()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var otherLayerId = repository.CreateLayer(string.Empty, "Consumers");
            var keptId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var removedId = repository.CreateEmptyNode(layerId, 600f, 0f);
            var consumerId = repository.CreateEmptyNode(otherLayerId, 0f, 0f);
            var pair = repository.CreateLayerPortalPair(otherLayerId, layerId, 600f, 300f);
            var incomingId = repository.CreateCrossLayerEdge(otherLayerId, consumerId, layerId, removedId, ArchitectorEdgeKinds.Dependency);
            repository.CreateEdge(layerId, keptId, removedId, ArchitectorEdgeKinds.DataFlow, "auto");
            repository.ApproveLayers(new[] { layerId });
            var approvedHash = repository.GetLayerApprovalStatus(layerId).CurrentHash;

            repository.RenameEntry(layerId, "Runtime Flow");
            repository.UpdateLayerPurpose(layerId, "Changed purpose.");
            repository.DeleteNode(layerId, removedId);
            var addedId = repository.CreateManualNode(layerId, 1200f, 0f);
            repository.RenameEmptyNode(layerId, keptId, "Kept Renamed");

            var changes = repository.GetBaselineChanges(layerId);
            var captions = changes.Select(change => change.Caption).ToArray();
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            Assert.That(captions, Does.Contain("Layer renamed: 'Runtime' → 'Runtime Flow'"));
            Assert.That(captions, Does.Contain("Purpose changed"));
            Assert.That(captions, Does.Contain("Node removed: New Node 2"));
            Assert.That(captions, Does.Contain("Node added: New Type"));
            Assert.That(captions, Does.Contain("Node changed: Kept Renamed (title)"));
            Assert.That(changes.Count(change => change.Subject == "relationship" && change.Change == ArchitectorBaselineChanges.Removed), Is.EqualTo(1));
            Assert.That(captions.Any(caption => caption.Contains("Consumers/")), Is.False, "Incoming relationships belong to the consumer layer.");

            repository.RevertLayerToApproved(layerId);

            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            Assert.That(repository.GetLayerApprovalStatus(layerId).CurrentHash, Is.EqualTo(approvedHash));
            Assert.That(repository.FindLayer(layerId).name, Is.EqualTo("Runtime"));
            var reverted = repository.LoadLayer(layerId).semantic;
            Assert.That(reverted.nodes.Any(node => node.id == removedId), Is.True);
            Assert.That(reverted.nodes.Any(node => node.id == addedId), Is.False);
            Assert.That(repository.LoadLayer(layerId).semantic.edges.Single().kind, Is.EqualTo(ArchitectorEdgeKinds.DataFlow));
            Assert.That(repository.LoadLayer(otherLayerId).semantic.edges, Is.Empty, "Revert does not recreate a consumer's relationship.");
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.False, "The consumer's lonely Portal was removed with its last relationship.");
            Assert.That(repository.NextUndoName, Is.EqualTo("Revert To Approved"));

            repository.Undo();
            Assert.That(repository.FindLayer(layerId).name, Is.EqualTo("Runtime Flow"));
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Any(node => node.id == addedId), Is.True);
        }

        [Test]
        public void RevertFailsWithoutWritingWhenRemoteEndpointIsGone()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var otherLayerId = repository.CreateLayer(string.Empty, "Providers");
            var sourceId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var providerId = repository.CreateEmptyNode(otherLayerId, 0f, 0f);
            repository.CreateCrossLayerEdge(layerId, sourceId, otherLayerId, providerId, ArchitectorEdgeKinds.Dependency);
            repository.ApproveLayers(new[] { layerId });
            repository.DeleteNode(otherLayerId, providerId);
            var revision = repository.Revision;

            var exception = Assert.Throws<ArchitectorDataException>(() => repository.RevertLayerToApproved(layerId));

            Assert.That(exception.Message, Does.Contain("no longer exists"));
            Assert.That(repository.Revision, Is.EqualTo(revision));
        }

        [Test]
        public void ConsumersDoNotChangeTheApprovalOfTheLayerTheyUse()
        {
            var coreId = repository.CreateLayer(string.Empty, "Core");
            var consumerLayerId = repository.CreateLayer(string.Empty, "Workspace");
            var coreNodeId = repository.CreateEmptyNode(coreId, 0f, 0f);
            var consumerId = repository.CreateEmptyNode(consumerLayerId, 0f, 0f);
            repository.ApproveLayers(new[] { coreId });

            repository.CreateCrossLayerEdge(consumerLayerId, consumerId, coreId, coreNodeId, ArchitectorEdgeKinds.Dependency);

            Assert.That(repository.GetLayerApprovalStatus(coreId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            Assert.That(repository.GetBaselineChanges(coreId), Is.Empty);
            repository.RenameEmptyNode(consumerLayerId, consumerId, "Renamed Consumer");
            Assert.That(repository.GetLayerApprovalStatus(coreId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));

            repository.RenameEmptyNode(coreId, coreNodeId, "Renamed Core");
            repository.RevertLayerToApproved(coreId);

            Assert.That(repository.GetLayerApprovalStatus(coreId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            Assert.That(repository.LoadLayer(consumerLayerId).semantic.edges.Single().targetNodeId, Is.EqualTo(coreNodeId));
            Assert.That(repository.LoadLayer(coreId).semantic.nodes.Count(node => node.kind == ArchitectorNodeKinds.LayerPortal), Is.EqualTo(1));
        }

        [Test]
        public void ApprovedLayerShowsOnlyPortalsToApprovedLayers()
        {
            var coreId = repository.CreateLayer(string.Empty, "Core");
            var draftId = repository.CreateLayer(string.Empty, "Draft");
            var coreNodeId = repository.CreateEmptyNode(coreId, 0f, 0f);
            var draftNodeId = repository.CreateEmptyNode(draftId, 0f, 0f);
            var pair = repository.CreateLayerPortalPair(draftId, coreId, 600f, 0f);
            var edgeId = repository.CreateCrossLayerEdge(draftId, draftNodeId, coreId, coreNodeId, ArchitectorEdgeKinds.Dependency);
            repository.ApproveLayers(new[] { coreId });

            var coreView = repository.LoadLayerView(coreId);
            var draftView = repository.LoadLayerView(draftId);

            Assert.That(coreView.semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.False);
            Assert.That(coreView.layout.nodes.Any(node => node.nodeId == pair.TargetNodeId), Is.False);
            Assert.That(coreView.semantic.edges.Any(edge => edge.id == edgeId), Is.False);
            Assert.That(repository.GetHiddenPortalCount(coreId), Is.EqualTo(1));
            Assert.That(draftView.semantic.nodes.Any(node => node.id == pair.SourceNodeId), Is.True);
            Assert.That(draftView.semantic.edges.Any(edge => edge.id == edgeId), Is.True);
            Assert.That(repository.LoadLayer(coreId).semantic.nodes.Any(node => node.id == pair.TargetNodeId), Is.True);

            repository.ApproveLayers(new[] { draftId });
            Assert.That(repository.LoadLayerView(coreId).semantic.edges.Any(edge => edge.id == edgeId), Is.True);
            Assert.That(repository.GetHiddenPortalCount(coreId), Is.Zero);

            repository.RenameEmptyNode(draftId, draftNodeId, "Changed Consumer");
            Assert.That(repository.GetLayerApprovalStatus(draftId).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            Assert.That(repository.GetHiddenPortalCount(coreId), Is.Zero, "A Changed layer keeps its baseline and counts as approved for visibility.");
        }

        [Test]
        public void ApprovingNotesPublishesChangedResponsibilitiesWithoutChangingTheContract()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var nodeId = repository.CreateEmptyNode(layerId, 0f, 0f);
            repository.UpdateNodeResponsibility(layerId, nodeId, "Old role.");
            repository.ApproveLayers(new[] { layerId });
            var hash = repository.GetLayerApprovalStatus(layerId).CurrentHash;

            repository.UpdateNodeResponsibility(layerId, nodeId, "New role.");

            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            Assert.That(repository.HasUnapprovedResponsibilities(layerId), Is.True);
            Assert.That(repository.GetBaselineChanges(layerId).Single().Caption, Does.Contain("responsibility"));

            repository.ApproveLayers(new[] { layerId });

            var digest = File.ReadAllText(repository.Paths.ToAbsoluteDataPath(ArchitectorPaths.DigestFileName));
            Assert.That(repository.HasUnapprovedResponsibilities(layerId), Is.False);
            Assert.That(repository.GetLayerApprovalStatus(layerId).CurrentHash, Is.EqualTo(hash));
            Assert.That(digest, Does.Contain("New role."));
            Assert.That(digest, Does.Not.Contain("Old role."));
        }

        [Test]
        public void DigestIsGeneratedFromApprovedBaselinesOnly()
        {
            var coreId = repository.CreateGroup(string.Empty, "Core");
            var layerId = repository.CreateLayer(coreId, "Units");
            var otherId = repository.CreateLayer(coreId, "Tick");
            repository.UpdateLayerPurpose(layerId, "Unit composition.");
            var unitId = repository.CreateManualNode(layerId, 0f, 0f);
            repository.UpdateManualNode(layerId, unitId, "Unit", "class", "Sample", "Unit", Array.Empty<string>(), new[] { "public" },
                new[] { new ArchitectorMemberRecord { id = "member_add", kind = "method", declaration = "public void Add(UnitModule module)" } });
            repository.UpdateNodeResponsibility(layerId, unitId, "Owns module lifecycle.");
            var moduleId = repository.CreateEmptyNode(layerId, 600f, 0f);
            repository.CreateEdge(layerId, unitId, moduleId, ArchitectorEdgeKinds.Dependency, "auto");
            var digestPath = repository.Paths.ToAbsoluteDataPath(ArchitectorPaths.DigestFileName);
            Assert.That(File.Exists(digestPath), Is.False);

            repository.ApproveLayers(new[] { layerId });

            var digest = File.ReadAllText(digestPath);
            Assert.That(digest, Does.Contain("## Core/Units (Planned)"));
            Assert.That(digest, Does.Contain("Unit composition."));
            Assert.That(digest, Does.Contain("- **Unit**: class `Sample.Unit` (manual) — Owns module lifecycle."));
            Assert.That(digest, Does.Contain("  - `public void Add(UnitModule module)`"));
            Assert.That(digest, Does.Contain("- Unit -> New Node (dependency)"));
            Assert.That(digest, Does.Not.Contain("Core/Tick"));

            repository.RenameEmptyNode(layerId, moduleId, "UnitModule");
            Assert.That(File.ReadAllText(digestPath), Is.EqualTo(digest), "Unapproved edits must not change the digest.");
            repository.ApproveLayers(new[] { layerId });
            Assert.That(File.ReadAllText(digestPath), Does.Contain("- Unit -> UnitModule (dependency)"));

            repository.RevokeLayerApprovals(new[] { layerId });
            Assert.That(File.Exists(digestPath), Is.False);
            repository.Undo();
            Assert.That(File.Exists(digestPath), Is.True);
        }

        [Test]
        public void LayerCodeScopeIsStoredWithoutAffectingApproval()
        {
            var layerId = repository.CreateLayer(string.Empty, "Units");
            repository.ApproveLayers(new[] { layerId });

            repository.UpdateLayerScope(layerId, new[] { " Assets\\Plugins\\Sample/Runtime/ ", "", "Assets/Plugins/Sample/Runtime" }, new[] { "Sample.Units" });

            var scope = repository.FindLayer(layerId).scope;
            Assert.That(scope.folders, Is.EqualTo(new[] { "Assets/Plugins/Sample/Runtime" }));
            Assert.That(scope.namespaces, Is.EqualTo(new[] { "Sample.Units" }));
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            Assert.That(ReadData(ArchitectorPaths.LayerPath(layerId)), Does.Contain("Assets/Plugins/Sample/Runtime"));
            Assert.Throws<ArchitectorDataException>(() => repository.UpdateLayerScope(layerId, new[] { "../Outside" }, null));
            Assert.Throws<ArchitectorDataException>(() => repository.UpdateLayerScope(layerId, null, new[] { "Sample..Units" }));

            repository.ApplySemanticPlan(new ArchitectorSemanticPlan
            {
                layerScopes = { new ArchitectorPlanLayerScope { layer = "Units", namespaces = { "Sample" } } }
            });
            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(repository.FindLayer(layerId).scope.folders, Is.Empty);
            Assert.That(repository.FindLayer(layerId).scope.namespaces, Is.EqualTo(new[] { "Sample" }));
        }

        [Test]
        public void KeptUnlockSurvivesReloadUntilThePasswordChanges()
        {
            var coreId = repository.CreateGroup(string.Empty, "Core");
            repository.LockGroup(coreId, "STOP", "Core is the canon.");
            Assert.That(repository.UnlockGroupForSession(coreId, "STOP", keepUntilEditorRestart: true), Is.True);
            var kept = repository.ExportKeptUnlocks();

            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            reloaded.RestoreKeptUnlocks(kept);
            Assert.That(reloaded.IsGroupLockedForEditing(coreId), Is.False);

            reloaded.LockGroup(coreId, "OTHER", "Core is the canon.");
            var changed = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            changed.RestoreKeptUnlocks(kept);
            Assert.That(changed.IsGroupLockedForEditing(coreId), Is.True);

            repository.UnlockGroupForSession(coreId, "STOP");
            Assert.That(repository.ExportKeptUnlocks(), Is.Empty, "An ordinary unlock does not survive a reload.");
        }

        [Test]
        public void LockedGroupRejectsChangesUntilUnlockedForTheSession()
        {
            var coreId = repository.CreateGroup(string.Empty, "Core");
            var coreLayerId = repository.CreateLayer(coreId, "Units");
            var coreNodeId = repository.CreateEmptyNode(coreLayerId, 0f, 0f);
            var workspaceLayerId = repository.CreateLayer(string.Empty, "Sandbox");
            var workspaceNodeId = repository.CreateEmptyNode(workspaceLayerId, 0f, 0f);

            repository.LockGroup(coreId, "STOP", "Core is the canon.");

            Assert.That(ReadData(ArchitectorPaths.GroupPath(coreId)), Does.Not.Contain("STOP"));
            var exception = Assert.Throws<ArchitectorDataException>(() => repository.RenameEmptyNode(coreLayerId, coreNodeId, "Changed"));
            Assert.That(exception.Message, Does.Contain("Core is the canon."));
            Assert.That(exception.Message, Does.Contain("Do not try to bypass the lock"));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateLayer(coreId, "Extra"));
            Assert.Throws<ArchitectorDataException>(() => repository.RenameEntry(coreId, "Kernel"));
            Assert.Throws<ArchitectorDataException>(() => repository.DeleteEntry(coreLayerId));
            Assert.Throws<ArchitectorDataException>(() => repository.ApproveLayers(new[] { coreLayerId }));
            Assert.Throws<ArchitectorDataException>(() => repository.MoveEntries(new[] { workspaceLayerId }, coreId));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateCrossLayerEdge(
                coreLayerId, coreNodeId, workspaceLayerId, workspaceNodeId, ArchitectorEdgeKinds.Dependency));
            Assert.DoesNotThrow(() => repository.CreateCrossLayerEdge(
                workspaceLayerId, workspaceNodeId, coreLayerId, coreNodeId, ArchitectorEdgeKinds.Dependency));
            Assert.DoesNotThrow(() => repository.CreateCrossLayerEdge(
                coreLayerId, coreNodeId, workspaceLayerId, workspaceNodeId, ArchitectorEdgeKinds.Event));

            Assert.Throws<ArchitectorDataException>(() => repository.RemoveGroupLock(coreId));
            Assert.That(repository.UnlockGroupForSession(coreId, "wrong"), Is.False);
            Assert.That(repository.UnlockGroupForSession(coreId, "STOP"), Is.True);
            repository.RenameEmptyNode(coreLayerId, coreNodeId, "Changed");
            repository.RelockGroup(coreId);
            Assert.Throws<ArchitectorDataException>(() => repository.RenameEmptyNode(coreLayerId, coreNodeId, "Again"));

            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(repository.IsGroupLockedForEditing(coreId), Is.True);
            Assert.That(repository.FindEffectiveLock(coreLayerId), Is.EqualTo(coreId));
            Assert.That(repository.UnlockGroupForSession(coreId, "STOP"), Is.True);
            repository.RemoveGroupLock(coreId);
            Assert.That(repository.IsGroupLocked(coreId), Is.False);
            Assert.DoesNotThrow(() => repository.RenameEmptyNode(coreLayerId, coreNodeId, "Free"));
        }

        [Test]
        public void ReorderingRewritesOnlyTheMovedEntry()
        {
            var firstId = repository.CreateLayer(string.Empty, "First");
            var secondId = repository.CreateLayer(string.Empty, "Second");
            var thirdId = repository.CreateLayer(string.Empty, "Third");
            var firstFile = ReadData(ArchitectorPaths.LayerPath(firstId));
            var secondFile = ReadData(ArchitectorPaths.LayerPath(secondId));
            var projectFile = ReadData(ArchitectorPaths.ProjectFileName);

            repository.MoveEntries(new[] { thirdId }, string.Empty, layerInsertionIndex: 0);

            Assert.That(repository.Project.rootLayerIds, Is.EqualTo(new[] { thirdId, firstId, secondId }));
            Assert.That(ReadData(ArchitectorPaths.LayerPath(firstId)), Is.EqualTo(firstFile));
            Assert.That(ReadData(ArchitectorPaths.LayerPath(secondId)), Is.EqualTo(secondFile));
            Assert.That(ReadData(ArchitectorPaths.ProjectFileName), Is.EqualTo(projectFile));
            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(repository.Project.rootLayerIds, Is.EqualTo(new[] { thirdId, firstId, secondId }));
        }

        [Test]
        public void OrderKeysInsertBetweenNeighboursAndKeepIncreasingKeys()
        {
            var middle = ArchitectorOrderKeys.Between("a", "b");
            Assert.That(string.CompareOrdinal("a", middle), Is.LessThan(0));
            Assert.That(string.CompareOrdinal(middle, "b"), Is.LessThan(0));
            Assert.That(string.CompareOrdinal(ArchitectorOrderKeys.Between(null, "1"), "1"), Is.LessThan(0));
            Assert.That(string.CompareOrdinal(ArchitectorOrderKeys.Between("z", null), "z"), Is.GreaterThan(0));

            var assigned = ArchitectorOrderKeys.Assign(new[]
            {
                ("moved", "V"), ("first", "G"), ("second", "N"), ("fresh", string.Empty)
            });

            Assert.That(assigned["first"], Is.EqualTo("G"));
            Assert.That(assigned["second"], Is.EqualTo("N"));
            Assert.That(string.CompareOrdinal(assigned["moved"], "G"), Is.LessThan(0));
            Assert.That(string.CompareOrdinal(assigned["fresh"], "N"), Is.GreaterThan(0));
            Assert.That(assigned.Values.All(ArchitectorOrderKeys.IsValid), Is.True);
        }

        [Test]
        public void ExternalChangeIsReloadedAndPendingWriteIsRejected()
        {
            var groupId = repository.CreateGroup(string.Empty, "Existing");
            string reloaded = null;
            repository.ExternalChangesReloaded += summary => reloaded = summary;
            WriteExternalGroup("group_external", "External Group");

            var exception = Assert.Throws<ArchitectorDataException>(() => repository.RenameEntry(groupId, "Renamed"));

            Assert.That(exception.Message, Does.Contain("reloaded"));
            Assert.That(reloaded, Does.Contain("External Group"));
            Assert.That(repository.FindGroup("group_external"), Is.Not.Null);
            Assert.That(repository.FindGroup(groupId).name, Is.EqualTo("Existing"));
            Assert.That(repository.CanUndo, Is.False);
            Assert.That(File.Exists(DataPath(ArchitectorPaths.GroupPath("group_external"))), Is.True);

            repository.RenameEntry(groupId, "Renamed");
            Assert.That(repository.FindGroup(groupId).name, Is.EqualTo("Renamed"));
            Assert.That(repository.FindGroup("group_external"), Is.Not.Null);
        }

        [Test]
        public void InvalidExternalChangeKeepsSessionReadOnlyUntilRepaired()
        {
            var groupId = repository.CreateGroup(string.Empty, "Existing");
            var groupPath = DataPath(ArchitectorPaths.GroupPath(groupId));
            var original = File.ReadAllText(groupPath);
            File.WriteAllText(groupPath, "<<<<<<< HEAD\n" + original);

            Assert.That(repository.RefreshExternalChanges(), Is.False);
            Assert.That(repository.ExternalChangeError, Does.Contain(groupId));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateGroup(string.Empty, "Blocked"));
            Assert.That(repository.FindGroup(groupId).name, Is.EqualTo("Existing"));

            File.WriteAllText(groupPath, original);
            repository.RefreshExternalChanges();
            Assert.That(repository.ExternalChangeError, Is.Empty);
            Assert.DoesNotThrow(() => repository.CreateGroup(string.Empty, "Allowed"));
        }

        [Test]
        public void LineEndingOnlyExternalChangeIsAdoptedWithoutReload()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var path = DataPath(ArchitectorPaths.LayerPath(layerId));
            var crlf = File.ReadAllText(path).Replace("\n", "\r\n");
            File.WriteAllText(path, crlf);
            var revision = repository.Revision;

            Assert.That(repository.RefreshExternalChanges(), Is.False);
            Assert.That(repository.Revision, Is.EqualTo(revision));
            Assert.That(repository.CanUndo, Is.True);
            repository.CreateGroup(string.Empty, "Next");
            Assert.That(File.ReadAllText(path), Is.EqualTo(crlf));
        }

        [Test]
        public void MergedDuplicatePortalPairsAreReportedOnBothLayers()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Gameplay");
            var targetLayerId = repository.CreateLayer(string.Empty, "Core");
            var pair = repository.CreateLayerPortalPair(sourceLayerId, targetLayerId, 0f, 0f);
            var link = ArchitectorJson.Deserialize<ArchitectorStoredLinkDocument>(ReadData(ArchitectorPaths.LinkPath(pair.PairId)));
            link.pairId = "portal-pair_merged";
            foreach (var endpoint in link.endpoints)
                endpoint.nodeId += "_merged";
            ArchitectorJson.WriteAtomic(
                DataPath(ArchitectorPaths.LinkPath(link.pairId)),
                link);

            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));

            var exception = Assert.Throws<ArchitectorDataException>(() => repository.LoadLayer(targetLayerId));
            Assert.That(exception.Message, Does.Contain("more than one Portal pair"));
            Assert.Throws<ArchitectorDataException>(() => repository.LoadLayer(sourceLayerId));
        }

        [Test]
        public void NewNodeBoundsFillWholeConfiguredGridCells()
        {
            repository.UpdateSettings("Set Test Grid", settings => settings.geometry.gridSize = 75f);
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var emptyNodeId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var manualId = repository.CreateManualNode(layerId, 600f, 0f);
            var layout = repository.LoadLayer(layerId).layout;
            var emptyNode = layout.nodes.Single(node => node.nodeId == emptyNodeId);
            var manual = layout.nodes.Single(node => node.nodeId == manualId);

            Assert.That((emptyNode.width, emptyNode.height), Is.EqualTo((300f, 150f)));
            Assert.That((manual.width, manual.height), Is.EqualTo((300f, 225f)));
            Assert.That(emptyNode.width % 75f, Is.Zero);
            Assert.That(emptyNode.height % 75f, Is.Zero);
            Assert.That(manual.width % 75f, Is.Zero);
            Assert.That(manual.height % 75f, Is.Zero);
        }

        [Test]
        public void EmptyNodeCanBeRenamedAndReplacedByManualTypeAtSameLayout()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var emptyNodeId = repository.CreateEmptyNode(layerId, 160f, 240f);
            repository.RenameEmptyNode(layerId, emptyNodeId, "Payment Gateway");
            var before = repository.LoadLayer(layerId);
            var beforeLayout = before.layout.nodes.Single(node => node.nodeId == emptyNodeId);

            var manualId = repository.ConvertEmptyNodeToManual(layerId, emptyNodeId);
            var converted = repository.LoadLayer(layerId);
            var manual = converted.semantic.nodes.Single(node => node.id == manualId);
            var afterLayout = converted.layout.nodes.Single(node => node.nodeId == manualId);

            Assert.That(manualId, Is.Not.EqualTo(emptyNodeId));
            Assert.That(converted.semantic.nodes.Any(node => node.id == emptyNodeId), Is.False);
            Assert.That(manual.title, Is.EqualTo("Payment Gateway"));
            Assert.That(manual.type.mode, Is.EqualTo("declared"));
            Assert.That(manual.type.name, Is.EqualTo("PaymentGateway"));
            Assert.That((afterLayout.x, afterLayout.y, afterLayout.width, afterLayout.height),
                Is.EqualTo((beforeLayout.x, beforeLayout.y, beforeLayout.width, beforeLayout.height)));

            repository.Undo();
            var restored = repository.LoadLayer(layerId);
            Assert.That(restored.semantic.nodes.Single(node => node.id == emptyNodeId).title, Is.EqualTo("Payment Gateway"));
            Assert.That(restored.semantic.nodes.Any(node => node.id == manualId), Is.False);
        }

        [Test]
        public void ArrangeFingerprintFollowsStructureNotPositionsAndOneUndoRevertsAnArrange()
        {
            var layerId = repository.CreateLayer(string.Empty, "Arranged");
            var first = repository.CreateEmptyNode(layerId, 0f, 0f);
            var second = repository.CreateEmptyNode(layerId, 900f, 900f);
            repository.CreateEdge(layerId, first, second, "auto");
            Vector2 Position(string nodeId)
            {
                var node = repository.LoadLayer(layerId).layout.nodes.Single(candidate => candidate.nodeId == nodeId);
                return new Vector2(node.x, node.y);
            }
            var fingerprint = repository.ArrangeFingerprint(layerId);
            var before = Position(second);

            repository.ArrangeNodes(layerId);
            Assert.That(Position(second), Is.Not.EqualTo(before), "Arrange moves the far node.");
            Assert.That(repository.ArrangeFingerprint(layerId), Is.EqualTo(fingerprint), "Positions are not part of the fingerprint.");
            repository.Undo();
            Assert.That(Position(second), Is.EqualTo(before), "One undo reverts one layer's arrange.");

            repository.CreateEmptyNode(layerId, 300f, 0f);
            Assert.That(repository.ArrangeFingerprint(layerId), Is.Not.EqualTo(fingerprint), "A new node changes the fingerprint.");

            var state = new ArchitectorUserStateStore(repository.Paths);
            state.SetArranged(layerId, fingerprint);
            Assert.That(new ArchitectorUserStateStore(repository.Paths).IsArranged(layerId, fingerprint), Is.True);
            state.SetArranged(layerId, string.Empty);
            Assert.That(new ArchitectorUserStateStore(repository.Paths).IsArranged(layerId, fingerprint), Is.False);
        }

        [Test]
        public void UserViewStateIsStoredSeparatelyForEveryLayer()
        {
            var firstLayerId = repository.CreateLayer(string.Empty, "First");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second");
            var state = new ArchitectorUserStateStore(repository.Paths);
            state.SaveLayerView("master", firstLayerId, 10f, 20f, 0.75f);
            state.SaveLayerView("master", secondLayerId, 300f, 400f, 1.5f);

            var reloaded = new ArchitectorUserStateStore(repository.Paths);
            var first = reloaded.GetLayerView("master", firstLayerId);
            var second = reloaded.GetLayerView("master", secondLayerId);

            Assert.That((first.panX, first.panY, first.zoom), Is.EqualTo((10f, 20f, 0.75f)));
            Assert.That((second.panX, second.panY, second.zoom), Is.EqualTo((300f, 400f, 1.5f)));
            Assert.That(repository.Paths.UserStatePath, Does.Contain(Path.Combine("Library", "Architector")));
        }

        [Test]
        public void MasterNavigationIsRestoredFromUserState()
        {
            var state = new ArchitectorUserStateStore(repository.Paths);
            state.SaveMasterNavigation(new ArchitectorWindowNavigationRecord
            {
                currentGroupId = "group_core",
                activeLayerId = "layer_b",
                openLayerIds = { "layer_a", "layer_b" },
                layerVisitHistory = { "layer_a", "layer_b", "layer_a" },
                layerVisitHistoryIndex = 1,
                layerTabsScroll = 42f
            });

            var navigation = new ArchitectorUserStateStore(repository.Paths).GetMasterNavigation();

            Assert.That(navigation.currentGroupId, Is.EqualTo("group_core"));
            Assert.That(navigation.activeLayerId, Is.EqualTo("layer_b"));
            Assert.That(navigation.openLayerIds, Is.EqualTo(new[] { "layer_a", "layer_b" }));
            Assert.That(navigation.layerVisitHistory, Is.EqualTo(new[] { "layer_a", "layer_b", "layer_a" }));
            Assert.That(navigation.layerVisitHistoryIndex, Is.EqualTo(1));
            Assert.That(navigation.layerTabsScroll, Is.EqualTo(42f));
        }

        [Test]
        public void UndoHistorySurvivesExportAndImportIntoANewRepository()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            repository.RenameEntry(layerId, "Renamed");
            repository.Undo();
            var history = repository.ExportHistory();

            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            Assert.That(reloaded.ImportHistory(history), Is.True);
            Assert.That(reloaded.NextUndoName, Is.EqualTo(repository.NextUndoName));
            Assert.That(reloaded.NextRedoName, Is.EqualTo(repository.NextRedoName));

            reloaded.Redo();
            Assert.That(reloaded.FindLayer(layerId).name, Is.EqualTo("Renamed"));
            reloaded.Undo();
            reloaded.Undo();
            Assert.That(reloaded.FindLayer(layerId), Is.Null);
        }

        [Test]
        public void UndoHistoryIsDroppedWhenFilesChangedWhileItWasExported()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var history = repository.ExportHistory();
            new ArchitectorRepository(new ArchitectorPaths(temporaryRoot)).RenameEntry(layerId, "Changed Outside");

            var reloaded = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));

            Assert.That(reloaded.ImportHistory(history), Is.False);
            Assert.That(reloaded.CanUndo, Is.False);
            Assert.That(reloaded.CanRedo, Is.False);
        }

        [Test]
        public void ConformanceFoldoutStateIsLocalPerWindowAndLayer()
        {
            var layerId = repository.CreateLayer(string.Empty, "Layer");
            var state = new ArchitectorUserStateStore(repository.Paths);
            state.SaveConformanceExpanded("master", layerId, true);

            var reloaded = new ArchitectorUserStateStore(repository.Paths);
            Assert.That(reloaded.GetConformanceExpanded("master", layerId), Is.True);
            Assert.That(reloaded.GetConformanceExpanded("workspace_other", layerId), Is.False);
        }

        [Test]
        public void SettingsPalettePersistsAndParticipatesInUndoRedo()
        {
            var originalRed = repository.Settings.palette.dark.node.r;
            var originalDataFlowBlue = repository.Settings.palette.dark.dataFlowEdge.b;
            var originalEntryPortalGreen = repository.Settings.palette.dark.entryPortalOutline.g;
            repository.UpdateSettings("Change Settings", settings =>
            {
                settings.palette.dark.node.r = 0.91f;
                settings.palette.dark.dataFlowEdge.b = 0.83f;
                settings.palette.dark.entryPortalOutline.g = 0.77f;
                settings.palette.light.exitPortalOutline.b = 0.66f;
            });

            Assert.That(repository.Settings.palette.dark.node.r, Is.EqualTo(0.91f));
            Assert.That(repository.Settings.palette.dark.dataFlowEdge.b, Is.EqualTo(0.83f));
            Assert.That(repository.Settings.palette.dark.entryPortalOutline.g, Is.EqualTo(0.77f));
            Assert.That(repository.Settings.palette.light.exitPortalOutline.b, Is.EqualTo(0.66f));
            Assert.That(ArchitectorJson.Read<ArchitectorSettingsDocument>(repository.Paths.SettingsPath).palette.dark.node.r, Is.EqualTo(0.91f));
            Assert.That(ArchitectorJson.Read<ArchitectorSettingsDocument>(repository.Paths.SettingsPath).palette.dark.dataFlowEdge.b, Is.EqualTo(0.83f));
            Assert.That(ArchitectorJson.Read<ArchitectorSettingsDocument>(repository.Paths.SettingsPath).palette.dark.entryPortalOutline.g, Is.EqualTo(0.77f));

            repository.Undo();
            Assert.That(repository.Settings.palette.dark.node.r, Is.EqualTo(originalRed));
            Assert.That(repository.Settings.palette.dark.dataFlowEdge.b, Is.EqualTo(originalDataFlowBlue));
            Assert.That(repository.Settings.palette.dark.entryPortalOutline.g, Is.EqualTo(originalEntryPortalGreen));

            repository.Redo();
            Assert.That(repository.Settings.palette.dark.node.r, Is.EqualTo(0.91f));
            Assert.That(repository.Settings.palette.dark.dataFlowEdge.b, Is.EqualTo(0.83f));
            Assert.That(repository.Settings.palette.dark.entryPortalOutline.g, Is.EqualTo(0.77f));
            Assert.That(repository.Settings.palette.light.exitPortalOutline.b, Is.EqualTo(0.66f));
        }

        [Test]
        public void DefaultThemePalettesMatchTheApprovedCurrentColors()
        {
            var dark = ArchitectorThemePaletteSettings.CreateDark();
            var light = ArchitectorThemePaletteSettings.CreateLight();

            Assert.That(dark.titleText.r, Is.EqualTo(1f));
            Assert.That(dark.exitPortalOutline.r, Is.EqualTo(0.87843138f));
            Assert.That(dark.selectedEdge.b, Is.EqualTo(0.82745099f));
            Assert.That(dark.incomingEdgeHighlight.g, Is.EqualTo(0.82056099f));
            Assert.That(light.entryPortalOutline.g, Is.EqualTo(0.72156864f));
            Assert.That(light.exitPortalOutline.r, Is.EqualTo(0.87843138f));
            Assert.That(light.selectedEdge.b, Is.EqualTo(0.82745099f));
            Assert.That(light.incomingEdgeHighlight.g, Is.EqualTo(0.64158475f));
        }

        [Test]
        public void MemberSearchScopePersistsAndParticipatesInUndoRedo()
        {
            Assert.That(repository.Settings.optimization.memberParentDepth, Is.Zero);
            Assert.That(repository.Settings.optimization.includeInterfaceMembers, Is.False);
            Assert.That(repository.Settings.optimization.includeFrameworkMembers, Is.False);

            repository.UpdateSettings("Expand Member Search Scope", settings =>
            {
                settings.optimization.memberParentDepth = 2;
                settings.optimization.includeInterfaceMembers = true;
                settings.optimization.includeFrameworkMembers = true;
            });

            var persisted = ArchitectorJson.Read<ArchitectorSettingsDocument>(repository.Paths.SettingsPath);
            Assert.That(persisted.optimization.memberParentDepth, Is.EqualTo(2));
            Assert.That(persisted.optimization.includeInterfaceMembers, Is.True);
            Assert.That(persisted.optimization.includeFrameworkMembers, Is.True);

            repository.Undo();
            Assert.That(repository.Settings.optimization.memberParentDepth, Is.Zero);
            Assert.That(repository.Settings.optimization.includeInterfaceMembers, Is.False);
            Assert.That(repository.Settings.optimization.includeFrameworkMembers, Is.False);

            repository.Redo();
            Assert.That(repository.Settings.optimization.memberParentDepth, Is.EqualTo(2));
            Assert.That(repository.Settings.optimization.includeInterfaceMembers, Is.True);
            Assert.That(repository.Settings.optimization.includeFrameworkMembers, Is.True);
        }

        [Test]
        public void ManualTypeAndContractMembersPersistAndCanBeUndone()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var nodeId = repository.CreateManualNode(layerId, 0f, 0f);
            var member = new ArchitectorMemberRecord
            {
                id = "member_test",
                kind = "method",
                declaration = "public void Execute()"
            };

            repository.UpdateManualNode(
                layerId,
                nodeId,
                "Command Handler",
                "interface",
                "Gameplay.Commands",
                "ICommandHandler",
                new[] { "TCommand" },
                new[] { "public" },
                new[] { member });

            var edited = repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == nodeId);
            Assert.That(edited.title, Is.EqualTo("Command Handler"));
            Assert.That(edited.type.mode, Is.EqualTo("declared"));
            Assert.That(edited.type.kind, Is.EqualTo("interface"));
            Assert.That(edited.type.typeNamespace, Is.EqualTo("Gameplay.Commands"));
            Assert.That(edited.type.name, Is.EqualTo("ICommandHandler"));
            Assert.That(edited.type.genericParameters, Is.EqualTo(new[] { "TCommand" }));
            Assert.That(edited.members.Single().declaration, Is.EqualTo("public void Execute()"));

            repository.Undo();
            var reverted = repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == nodeId);
            Assert.That(reverted.title, Is.EqualTo("New Type"));
            Assert.That(reverted.type.name, Is.EqualTo("NewType"));
            Assert.That(reverted.members, Is.Empty);
        }

        [Test]
        public void ManualTypeFactsAreDeterministicInvalidateApprovalAndUndoTogether()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var nodeId = repository.CreateManualNode(layerId, 0f, 0f);
            repository.ApproveLayers(new[] { layerId });

            repository.UpdateManualNode(
                layerId,
                nodeId,
                "Service",
                "class",
                "Game",
                "Service",
                Array.Empty<string>(),
                new[] { "public" },
                Array.Empty<ArchitectorMemberRecord>(),
                ArchitectorTypeReference.Declared("Game.ServiceBase", "class"),
                new[]
                {
                    ArchitectorTypeReference.Declared("Game.IZeta", "interface"),
                    ArchitectorTypeReference.Declared("Game.IAlpha", "interface")
                });

            var edited = repository.LoadLayer(layerId).semantic.nodes.Single();
            Assert.That(edited.type.baseType.name, Is.EqualTo("ServiceBase"));
            Assert.That(edited.type.interfaces.Select(ArchitectorTypeReference.FullName),
                Is.EqualTo(new[] { "Game.IAlpha", "Game.IZeta" }));
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            Assert.That(repository.NextUndoName, Is.EqualTo("Edit Manual Type"));

            repository.Undo();
            var reverted = repository.LoadLayer(layerId).semantic.nodes.Single();
            Assert.That(reverted.type.baseType, Is.Null);
            Assert.That(reverted.type.interfaces, Is.Empty);
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
        }

        [Test]
        public void ManualTypeFullNamesAreUniqueWithinLayer()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var firstNodeId = repository.CreateManualNode(layerId, 0f, 0f);
            var secondNodeId = repository.CreateManualNode(layerId, 1000f, 0f);

            repository.UpdateManualNode(layerId, firstNodeId, "First", "class", "Gameplay", "Service", Array.Empty<string>(), new[] { "public" }, Array.Empty<ArchitectorMemberRecord>());

            Assert.Throws<ArchitectorDataException>(() =>
                repository.UpdateManualNode(layerId, secondNodeId, "Second", "class", "gameplay", "service", Array.Empty<string>(), new[] { "public" }, Array.Empty<ArchitectorMemberRecord>()));
        }

        [Test]
        public void LinkedTypePersistsStableIdentityAndRejectsDuplicateInLayer()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var indexedType = CreateIndexedType();

            var nodeId = repository.CreateLinkedNode(layerId, 0f, 0f, indexedType);
            var linked = repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == nodeId);

            Assert.That(linked.type.mode, Is.EqualTo("linked"));
            Assert.That(linked.type.assemblyName, Is.EqualTo("Assembly-CSharp"));
            Assert.That(linked.type.metadataName, Is.EqualTo("Gameplay.PlayerController"));
            Assert.That(linked.type.sourceDeclarations.Single().repositoryPath, Is.EqualTo("Assets/Scripts/PlayerController.cs"));
            Assert.Throws<ArchitectorDataException>(() =>
                repository.CreateLinkedNode(layerId, 600f, 0f, indexedType));
        }

        [Test]
        public void LinkingManualTypePreservesAuthoredTypeFactsAndCanBeUndone()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var nodeId = repository.CreateManualNode(layerId, 0f, 0f);
            var draftMember = new ArchitectorMemberRecord
            {
                id = "member_execute",
                kind = "method",
                declaration = "public void Execute(Int32 count)"
            };
            repository.UpdateManualNode(
                layerId,
                nodeId,
                "Player Controller",
                "class",
                "Gameplay",
                "PlayerController",
                Array.Empty<string>(),
                new[] { "public" },
                new[] { draftMember },
                ArchitectorTypeReference.Declared("Gameplay.Actor", "class"),
                new[] { ArchitectorTypeReference.Declared("Gameplay.IActor", "interface") });

            var indexedMember = CreateIndexedMember();
            repository.LinkManualNode(layerId, nodeId, CreateIndexedType(), new[] { indexedMember });

            var linked = repository.LoadLayer(layerId).semantic.nodes.Single();
            Assert.That(linked.id, Is.EqualTo(nodeId));
            Assert.That(linked.type.mode, Is.EqualTo("linked"));
            Assert.That(ArchitectorTypeReference.FullName(linked.type.baseType), Is.EqualTo("Gameplay.Actor"));
            Assert.That(ArchitectorTypeReference.FullName(linked.type.interfaces.Single()), Is.EqualTo("Gameplay.IActor"));
            Assert.That(linked.members.Single().symbolKey, Is.EqualTo(indexedMember.SymbolKey));
            Assert.That(repository.NextUndoName, Is.EqualTo("Link Manual Type"));

            repository.Undo();
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single().type.mode, Is.EqualTo("declared"));
        }

        [Test]
        public void EmptyNodeCanBeReplacedByLinkedTypeAndUndone()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var emptyId = repository.CreateEmptyNode(layerId, 160f, 240f);
            var beforeLayout = repository.LoadLayer(layerId).layout.nodes.Single(node => node.nodeId == emptyId);

            var linkedId = repository.ConvertEmptyNodeToLinked(layerId, emptyId, CreateIndexedType());
            var converted = repository.LoadLayer(layerId);
            var afterLayout = converted.layout.nodes.Single(node => node.nodeId == linkedId);

            Assert.That(linkedId, Is.Not.EqualTo(emptyId));
            Assert.That(converted.semantic.nodes.Single().type.mode, Is.EqualTo("linked"));
            Assert.That((afterLayout.x, afterLayout.y, afterLayout.width, afterLayout.height),
                Is.EqualTo((beforeLayout.x, beforeLayout.y, beforeLayout.width, beforeLayout.height)));

            repository.Undo();
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single().id, Is.EqualTo(emptyId));
        }

        [Test]
        public void LinkedContractMemberCanBeAddedRemovedAndUndone()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var nodeId = repository.CreateLinkedNode(layerId, 0f, 0f, CreateIndexedType());
            var indexedMember = CreateIndexedMember();

            var memberId = repository.AddLinkedMember(layerId, nodeId, indexedMember);
            var selected = repository.LoadLayer(layerId).semantic.nodes.Single().members.Single();

            Assert.That(selected.id, Is.EqualTo(memberId));
            Assert.That(selected.kind, Is.EqualTo("method"));
            Assert.That(selected.declaration, Is.EqualTo(indexedMember.Declaration));
            Assert.That(selected.symbolKey, Is.EqualTo(indexedMember.SymbolKey));
            Assert.Throws<ArchitectorDataException>(() => repository.AddLinkedMember(layerId, nodeId, indexedMember));

            repository.RemoveLinkedMember(layerId, nodeId, memberId);
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single().members, Is.Empty);

            repository.Undo();
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single().members.Single().id, Is.EqualTo(memberId));
        }

        [Test]
        public void LinkedContractMemberRequiresStableSymbolKey()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var nodeId = repository.CreateLinkedNode(layerId, 0f, 0f, CreateIndexedType());
            var invalidMember = CreateIndexedMember();
            invalidMember.SymbolKey = string.Empty;

            Assert.Throws<ArchitectorDataException>(() => repository.AddLinkedMember(layerId, nodeId, invalidMember));
        }

        [Test]
        public void LinkedContractMembersCanBeAddedAsOneUndoOperation()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var nodeId = repository.CreateLinkedNode(layerId, 0f, 0f, CreateIndexedType());
            var first = CreateIndexedMember();
            var second = new ArchitectorIndexedMember
            {
                SymbolKey = "property|Assembly-CSharp:Gameplay.PlayerController|Count|",
                LooseKey = "property|Assembly-CSharp:Gameplay.PlayerController|Count",
                Kind = "property",
                Declaration = "public Int32 Count { get; }",
                DeclaringTypeName = "Gameplay.PlayerController"
            };

            var ids = repository.AddLinkedMembers(layerId, nodeId, new[] { first, second });

            Assert.That(ids.Count, Is.EqualTo(2));
            Assert.That(repository.NextUndoName, Is.EqualTo("Add Linked Contract Members"));
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single().members, Has.Count.EqualTo(2));
            repository.Undo();
            Assert.That(repository.LoadLayer(layerId).semantic.nodes.Single().members, Is.Empty);
        }

        [Test]
        public void ContractMembersUseCanonicalKindOrder()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var nodeId = repository.CreateManualNode(layerId, 0f, 0f);
            var kinds = new[] { "method", "field", "operator", "event", "property", "constant", "constructor", "indexer" };
            var members = kinds.Select((kind, index) => new ArchitectorMemberRecord
            {
                id = $"member_{index}",
                kind = kind,
                declaration = $"{kind} declaration"
            }).ToArray();

            repository.UpdateManualNode(layerId, nodeId, "Type", "class", string.Empty, "Type", Array.Empty<string>(), new[] { "public" }, members);

            var orderedKinds = repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == nodeId).members.Select(member => member.kind);
            Assert.That(orderedKinds, Is.EqualTo(new[] { "constant", "event", "field", "property", "indexer", "constructor", "method", "operator" }));
        }

        private string ReadData(string relativePath)
        {
            return File.ReadAllText(DataPath(relativePath));
        }

        private string DataPath(string relativePath)
        {
            var exact = repository.Paths.ToAbsoluteDataPath(relativePath);
            if (File.Exists(exact))
                return exact;
            var directory = Path.GetDirectoryName(exact);
            var fileName = Path.GetFileName(exact);
            var separator = fileName.IndexOf('.');
            var id = fileName.Substring(0, separator);
            var suffix = fileName.Substring(separator);
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, id + ".*" + suffix).SingleOrDefault() ?? exact
                : exact;
        }

        private void WriteExternalGroup(string id, string name)
        {
            ArchitectorJson.WriteAtomic(
                DataPath(ArchitectorPaths.GroupPath(id)),
                new ArchitectorStoredGroupDocument { id = id, name = name, orderKey = "z" });
        }

        private static ArchitectorIndexedType CreateIndexedType()
        {
            return new ArchitectorIndexedType
            {
                AssemblyName = "Assembly-CSharp",
                MetadataName = "Gameplay.PlayerController",
                DisplayName = "PlayerController",
                Namespace = "Gameplay",
                Kind = "class",
                Modifiers = new[] { "internal" },
                SourceDeclarations = new[]
                {
                    new ArchitectorSourceDeclarationRecord
                    {
                        scriptGuid = "0123456789abcdef0123456789abcdef",
                        repositoryPath = "Assets/Scripts/PlayerController.cs",
                        line = -1,
                        column = -1
                    }
                }
            };
        }

        private static ArchitectorIndexedMember CreateIndexedMember()
        {
            return new ArchitectorIndexedMember
            {
                SymbolKey = "method|Assembly-CSharp:Gameplay.PlayerController|Execute|0|System.Int32",
                LooseKey = "method|Assembly-CSharp:Gameplay.PlayerController|Execute",
                Kind = "method",
                Declaration = "public void Execute(Int32 count)",
                DeclaringTypeName = "Gameplay.PlayerController"
            };
        }

        [Test]
        public void SelectedNodesMoveAsOneUndoableGroup()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var firstId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var secondId = repository.CreateEmptyNode(layerId, 400f, 0f);
            var untouchedId = repository.CreateEmptyNode(layerId, 1200f, 0f);
            var before = repository.LoadLayer(layerId).layout;
            var firstBefore = before.nodes.Single(node => node.nodeId == firstId);
            var secondBefore = before.nodes.Single(node => node.nodeId == secondId);
            var untouchedBefore = before.nodes.Single(node => node.nodeId == untouchedId);

            repository.MoveNodes(layerId, new Dictionary<string, ArchitectorPoint>
            {
                [firstId] = new ArchitectorPoint(firstBefore.x + 160f, firstBefore.y + 80f),
                [secondId] = new ArchitectorPoint(secondBefore.x + 160f, secondBefore.y + 80f)
            });

            var moved = repository.LoadLayer(layerId).layout;
            var firstMoved = moved.nodes.Single(node => node.nodeId == firstId);
            var secondMoved = moved.nodes.Single(node => node.nodeId == secondId);
            var untouchedMoved = moved.nodes.Single(node => node.nodeId == untouchedId);
            Assert.That((firstMoved.x - firstBefore.x, firstMoved.y - firstBefore.y), Is.EqualTo((160f, 80f)));
            Assert.That((secondMoved.x - secondBefore.x, secondMoved.y - secondBefore.y), Is.EqualTo((160f, 80f)));
            Assert.That((untouchedMoved.x, untouchedMoved.y), Is.EqualTo((untouchedBefore.x, untouchedBefore.y)));

            repository.Undo();
            var reverted = repository.LoadLayer(layerId).layout;
            Assert.That((reverted.nodes.Single(node => node.nodeId == firstId).x, reverted.nodes.Single(node => node.nodeId == firstId).y), Is.EqualTo((firstBefore.x, firstBefore.y)));
            Assert.That((reverted.nodes.Single(node => node.nodeId == secondId).x, reverted.nodes.Single(node => node.nodeId == secondId).y), Is.EqualTo((secondBefore.x, secondBefore.y)));
        }

        [Test]
        public void SelectedNodesDeleteAsOneUndoableGroup()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var firstId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var secondId = repository.CreateEmptyNode(layerId, 400f, 0f);

            repository.DeleteNodes(layerId, new[] { firstId, secondId });
            Assert.That(repository.LoadLayer(layerId).semantic.nodes, Is.Empty);

            repository.Undo();
            var restoredIds = repository.LoadLayer(layerId).semantic.nodes.Select(node => node.id);
            Assert.That(restoredIds, Is.EquivalentTo(new[] { firstId, secondId }));
        }

        [Test]
        public void ClipboardPasteAcrossLayersRegeneratesIdsAndPreservesRelativeLayout()
        {
            var sourceLayerId = repository.CreateLayer(string.Empty, "Source");
            var targetLayerId = repository.CreateLayer(string.Empty, "Target");
            var firstId = repository.CreateEmptyNode(sourceLayerId, 0f, 0f);
            var secondId = repository.CreateManualNode(sourceLayerId, 600f, 150f);
            var source = repository.LoadLayer(sourceLayerId);
            var firstSourceLayout = source.layout.nodes.Single(node => node.nodeId == firstId);
            var secondSourceLayout = source.layout.nodes.Single(node => node.nodeId == secondId);
            var clipboard = new ArchitectorNodeClipboard();
            clipboard.Copy(repository, sourceLayerId, new[] { firstId, secondId });

            var insertedIds = repository.PasteNodes(targetLayerId, clipboard, 1200f, 900f);
            var target = repository.LoadLayer(targetLayerId);
            var insertedLayouts = insertedIds.Select(id => target.layout.nodes.Single(node => node.nodeId == id)).ToArray();
            var insertedNodes = insertedIds.Select(id => target.semantic.nodes.Single(node => node.id == id)).ToArray();

            Assert.That(insertedIds, Has.Count.EqualTo(2));
            Assert.That(insertedIds, Is.Not.EquivalentTo(new[] { firstId, secondId }));
            Assert.That(
                (insertedLayouts[1].x - insertedLayouts[0].x, insertedLayouts[1].y - insertedLayouts[0].y),
                Is.EqualTo((secondSourceLayout.x - firstSourceLayout.x, secondSourceLayout.y - firstSourceLayout.y)));
            Assert.That(insertedNodes.Single(node => node.type.mode == "declared").type.name, Is.EqualTo("NewType"));

            repository.Undo();
            Assert.That(repository.LoadLayer(targetLayerId).semantic.nodes, Is.Empty);
        }

        [Test]
        public void DuplicateManualNodeUsesUniqueIdentityAndOneUndoRecord()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var originalId = repository.CreateManualNode(layerId, 0f, 0f);

            var duplicateId = repository.DuplicateNodes(layerId, new[] { originalId }).Single();
            var duplicated = repository.LoadLayer(layerId).semantic.nodes.Single(node => node.id == duplicateId);

            Assert.That(duplicateId, Is.Not.EqualTo(originalId));
            Assert.That(duplicated.title, Is.EqualTo("New Type 2"));
            Assert.That(duplicated.type.name, Is.EqualTo("NewType2"));

            repository.Undo();
            var restored = repository.LoadLayer(layerId).semantic.nodes;
            Assert.That(restored.Select(node => node.id), Is.EqualTo(new[] { originalId }));
        }

        [Test]
        public void DirectedDependenciesRejectDuplicatesAndCyclesAndSupportUndo()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var firstId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var secondId = repository.CreateEmptyNode(layerId, 600f, 0f);
            var thirdId = repository.CreateEmptyNode(layerId, 1200f, 0f);

            var firstEdgeId = repository.CreateEdge(layerId, firstId, secondId, "left");
            repository.CreateEdge(layerId, secondId, thirdId, "left");
            var documents = repository.LoadLayer(layerId);

            Assert.That(documents.semantic.edges, Has.Count.EqualTo(2));
            Assert.That(documents.layout.edges.Single(edge => edge.edgeId == firstEdgeId).preferredTargetSide, Is.EqualTo("left"));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateEdge(layerId, firstId, secondId, "left"));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateEdge(layerId, thirdId, firstId, "right"));

            repository.DeleteEdge(layerId, firstEdgeId);
            Assert.That(repository.LoadLayer(layerId).semantic.edges.Any(edge => edge.id == firstEdgeId), Is.False);
            repository.Undo();
            Assert.That(repository.LoadLayer(layerId).semantic.edges.Any(edge => edge.id == firstEdgeId), Is.True);
        }

        [Test]
        public void TypedRelationshipsShareEndpointsButOnlyDependenciesMustRemainAcyclic()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var firstId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var secondId = repository.CreateEmptyNode(layerId, 600f, 0f);

            repository.CreateEdge(layerId, firstId, secondId, ArchitectorEdgeKinds.Dependency, "left");
            repository.CreateEdge(layerId, firstId, secondId, ArchitectorEdgeKinds.DataFlow, "left");
            repository.CreateEdge(layerId, firstId, secondId, ArchitectorEdgeKinds.Event, "left");
            repository.CreateEdge(layerId, secondId, firstId, ArchitectorEdgeKinds.Event, "right");
            var callbackId = repository.CreateEdge(layerId, secondId, firstId, ArchitectorEdgeKinds.Callback, "right");

            var edges = repository.LoadLayer(layerId).semantic.edges;
            Assert.That(edges.Select(edge => edge.kind), Is.EquivalentTo(new[]
            {
                ArchitectorEdgeKinds.Dependency,
                ArchitectorEdgeKinds.DataFlow,
                ArchitectorEdgeKinds.Event,
                ArchitectorEdgeKinds.Event,
                ArchitectorEdgeKinds.Callback
            }));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateEdge(
                layerId,
                firstId,
                secondId,
                ArchitectorEdgeKinds.DataFlow,
                "left"));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateEdge(
                layerId,
                secondId,
                firstId,
                ArchitectorEdgeKinds.Dependency,
                "right"));
            Assert.Throws<ArchitectorDataException>(() => repository.CreateEdge(
                layerId,
                firstId,
                secondId,
                "unsupported",
                "left"));

            repository.Undo();
            Assert.That(repository.LoadLayer(layerId).semantic.edges.Any(edge => edge.id == callbackId), Is.False);
        }

        [Test]
        public void RelationshipKindCanBeChangedWithValidationAndUndo()
        {
            var layerId = repository.CreateLayer(string.Empty, "Architecture");
            var sourceId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var targetId = repository.CreateEmptyNode(layerId, 600f, 0f);
            var dependencyId = repository.CreateEdge(layerId, sourceId, targetId, ArchitectorEdgeKinds.Dependency, "left");
            var eventId = repository.CreateEdge(layerId, sourceId, targetId, ArchitectorEdgeKinds.Event, "left");

            var duplicate = Assert.Throws<ArchitectorDataException>(() =>
                repository.UpdateEdgeKind(layerId, dependencyId, ArchitectorEdgeKinds.Event));
            Assert.That(duplicate.Message, Does.Contain("New Node").And.Contain("New Node 2"));
            repository.DeleteEdge(layerId, eventId);
            repository.UpdateEdgeKind(layerId, dependencyId, ArchitectorEdgeKinds.Callback);
            Assert.That(repository.LoadLayer(layerId).semantic.edges.Single().kind, Is.EqualTo(ArchitectorEdgeKinds.Callback));

            repository.Undo();
            Assert.That(repository.LoadLayer(layerId).semantic.edges.Single().kind, Is.EqualTo(ArchitectorEdgeKinds.Dependency));
        }

        [Test]
        public void ExplorerEntriesCanBeReorderedAndMovedAsOneUndoableOperation()
        {
            var destinationId = repository.CreateGroup(string.Empty, "Destination");
            var firstLayerId = repository.CreateLayer(string.Empty, "First");
            var secondLayerId = repository.CreateLayer(string.Empty, "Second");

            repository.MoveEntries(new[] { secondLayerId }, string.Empty, layerInsertionIndex: 0);
            Assert.That(repository.Project.rootLayerIds, Is.EqualTo(new[] { secondLayerId, firstLayerId }));

            repository.MoveEntries(new[] { secondLayerId, firstLayerId }, destinationId);
            Assert.That(repository.Project.rootLayerIds, Is.Empty);
            Assert.That(repository.FindGroup(destinationId).layerIds, Is.EqualTo(new[] { secondLayerId, firstLayerId }));
            Assert.That(repository.FindLayer(secondLayerId).parentGroupId, Is.EqualTo(destinationId));

            repository.Undo();
            Assert.That(repository.Project.rootLayerIds, Is.EqualTo(new[] { secondLayerId, firstLayerId }));
            Assert.That(repository.FindGroup(destinationId).layerIds, Is.Empty);
        }

        [Test]
        public void ExplorerGroupCannotBeMovedIntoItsDescendant()
        {
            var parentId = repository.CreateGroup(string.Empty, "Parent");
            var childId = repository.CreateGroup(parentId, "Child");

            Assert.Throws<ArchitectorDataException>(() => repository.MoveEntries(new[] { parentId }, childId));
            Assert.That(repository.FindGroup(parentId).parentGroupId, Is.Empty);
            Assert.That(repository.FindGroup(childId).parentGroupId, Is.EqualTo(parentId));
        }

        [Test]
        public void ExplorerMoveFromNestedFolderIncludesLastSelectedLayer()
        {
            var sourceId = repository.CreateGroup(string.Empty, "Source");
            var destinationId = repository.CreateGroup(sourceId, "Destination");
            var firstLayerId = repository.CreateLayer(sourceId, "First");
            repository.CreateLayer(sourceId, "Middle");
            var lastLayerId = repository.CreateLayer(sourceId, "Last");

            repository.MoveEntries(new[] { firstLayerId, lastLayerId }, destinationId);

            Assert.That(repository.FindGroup(destinationId).layerIds, Is.EqualTo(new[] { firstLayerId, lastLayerId }));
            Assert.That(repository.FindGroup(sourceId).layerIds, Is.EqualTo(new[]
            {
                repository.Project.layers.Single(layer => layer.name == "Middle").id
            }));
        }

        [Test]
        public void SelectionVisibilityPanUsesTheSmallestRequiredCorrection()
        {
            var method = typeof(ArchitectorLayerCanvas).GetMethod(
                "VisibilityPanDelta",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var alreadyVisible = (Vector2)method.Invoke(null, new object[]
            {
                new Rect(50f, 40f, 100f, 60f),
                new Vector2(400f, 300f),
                16f
            });
            var hiddenByRightInspector = (Vector2)method.Invoke(null, new object[]
            {
                new Rect(330f, 40f, 100f, 60f),
                new Vector2(400f, 300f),
                16f
            });

            Assert.That(alreadyVisible, Is.EqualTo(Vector2.zero));
            Assert.That(hiddenByRightInspector, Is.EqualTo(new Vector2(-46f, 0f)));
        }

        [Test]
        public void SelectedRelationshipActionUsesTheGeometricRouteCenter()
        {
            var method = typeof(ArchitectorLayerCanvas).GetMethod(
                "RouteMidpoint",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var midpoint = (Vector2)method.Invoke(null, new object[]
            {
                new List<Vector2>
                {
                    new(0f, 0f),
                    new(100f, 0f),
                    new(100f, 300f)
                }
            });

            Assert.That(midpoint, Is.EqualTo(new Vector2(100f, 100f)));
        }

        [Test]
        public void ConformanceDiagnosticStyleWrapsLongMessagesWithoutFixedHeight()
        {
            var window = ScriptableObject.CreateInstance<ArchitectorMasterWindow>();
            try
            {
                var ensureStyle = typeof(ArchitectorMasterWindow).GetMethod(
                    "EnsureConformanceDiagnosticStyle",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var styleField = typeof(ArchitectorMasterWindow).GetField(
                    "conformanceDiagnosticStyle",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(ensureStyle, Is.Not.Null);
                Assert.That(styleField, Is.Not.Null);

                ensureStyle.Invoke(window, null);
                var style = (GUIStyle)styleField.GetValue(window);
                var message = new GUIContent(
                    "Layer 'MCP Validation Flow' is Changed; conformance cannot be claimed until it is approved.");

                Assert.That(style.wordWrap, Is.True);
                Assert.That(style.fixedHeight, Is.Zero);
                Assert.That(style.CalcHeight(message, 268f), Is.GreaterThan(style.lineHeight));

                style.normal.textColor = Color.black;
                ensureStyle.Invoke(window, null);
                Assert.That(style.normal.textColor, Is.EqualTo(EditorStyles.wordWrappedLabel.normal.textColor));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void SemanticPlanCreatesPurposeManualTypeAndDraftMembersInOneUndo()
        {
            var plan = new ArchitectorSemanticPlan
            {
                layers = new List<ArchitectorPlanLayer>
                {
                    new() { reference = "runtime", name = "Runtime", purpose = "Owns the approved runtime boundary." }
                },
                nodes = new List<ArchitectorPlanNode>
                {
                    new()
                    {
                        reference = "service",
                        layer = "runtime",
                        title = "Audio Service",
                        mode = "declared",
                        typeKind = "interface",
                        typeNamespace = "Game.Audio",
                        typeName = "IAudioService",
                        members = new List<ArchitectorPlanMember>
                        {
                            new() { kind = "method", declaration = "public void Play(string cue)" }
                        }
                    }
                }
            };

            repository.ApplySemanticPlan(plan, "Create Audio Contract");

            var layer = repository.Project.layers.Single();
            var node = repository.LoadLayer(layer.id).semantic.nodes.Single();
            Assert.That(layer.purpose, Is.EqualTo("Owns the approved runtime boundary."));
            Assert.That(node.type.mode, Is.EqualTo("declared"));
            Assert.That(node.type.kind, Is.EqualTo("interface"));
            Assert.That(node.type.typeNamespace, Is.EqualTo("Game.Audio"));
            Assert.That(node.members.Single().declaration, Is.EqualTo("public void Play(string cue)"));
            Assert.That(repository.NextUndoName, Is.EqualTo("Create Audio Contract"));
        }

        [Test]
        public void SemanticPlanConvertsEmptyNodeAndUpdatesExistingManualContract()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            var nodeId = repository.CreateEmptyNode(layerId, 0f, 0f);
            var convert = new ArchitectorSemanticPlan
            {
                nodeContracts = new List<ArchitectorPlanNodeContract>
                {
                    new()
                    {
                        layer = layerId,
                        node = nodeId,
                        title = "Audio Service",
                        mode = "declared",
                        typeKind = "class",
                        typeNamespace = "Game.Audio",
                        typeName = "AudioService",
                        members = new List<ArchitectorPlanMember>
                        {
                            new() { kind = "method", declaration = "public void Play(string cue)" }
                        }
                    }
                }
            };

            var converted = repository.ApplySemanticPlan(convert, "Convert Contract");
            var replacementId = repository.LoadLayer(layerId).semantic.nodes.Single().id;
            Assert.That(replacementId, Is.Not.EqualTo(nodeId));

            var update = new ArchitectorSemanticPlan
            {
                nodeContracts = new List<ArchitectorPlanNodeContract>
                {
                    new()
                    {
                        layer = layerId,
                        node = replacementId,
                        mode = "declared",
                        typeKind = "class",
                        typeNamespace = "Game.Audio",
                        typeName = "AudioService",
                        members = new List<ArchitectorPlanMember>
                        {
                            new() { kind = "method", declaration = "public void Stop(string cue)" }
                        }
                    }
                }
            };
            repository.ApplySemanticPlan(update, "Update Contract");

            var node = repository.LoadLayer(layerId).semantic.nodes.Single();
            Assert.That(node.id, Is.EqualTo(replacementId));
            Assert.That(node.members.Single().declaration, Is.EqualTo("public void Stop(string cue)"));
            Assert.That(converted.References, Is.Empty);
        }

        [Test]
        public void ChecksOffSkipsConformanceBlocksApprovalAndSurvivesReload()
        {
            var groupId = repository.CreateGroup(string.Empty, "Sketches");
            var layerId = repository.CreateLayer(groupId, "Sketch");
            repository.CreateLinkedNode(layerId, 0f, 0f, CreateIndexedType());
            using var index = new ArchitectorTypeIndex(repository);
            Assert.That(ArchitectorConformance.Evaluate(repository, index, new[] { layerId }).ErrorCount, Is.GreaterThan(0));
            Assert.That(ArchitectorReviewWindow.PendingCount(repository), Is.EqualTo(1));

            repository.SetGroupChecks(groupId, false);

            var report = ArchitectorConformance.Evaluate(repository, index, new[] { layerId });
            Assert.That(report.ErrorCount, Is.Zero);
            Assert.That(report.UncheckedLayerCount, Is.EqualTo(1));
            Assert.That(report.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { ArchitectorConformanceCodes.LayersUnchecked }));
            Assert.Throws<ArchitectorDataException>(() => repository.ApproveLayers(new[] { layerId }));
            Assert.That(ArchitectorReviewWindow.PendingCount(repository), Is.Zero);
            Assert.That(new ArchitectorRepository(new ArchitectorPaths(temporaryRoot)).IsUnchecked(layerId), Is.True);

            repository.SetGroupChecks(groupId, true);
            Assert.That(ArchitectorConformance.Evaluate(repository, index, new[] { layerId }).ErrorCount, Is.GreaterThan(0));
        }

        [Test]
        public void ChecksStayOnForApprovedLockedAndInheritedGroups()
        {
            var approvedGroup = repository.CreateGroup(string.Empty, "Approved");
            repository.ApproveLayers(new[] { repository.CreateLayer(approvedGroup, "Done") });
            Assert.That(() => repository.SetGroupChecks(approvedGroup, false), Throws.TypeOf<ArchitectorDataException>().With.Message.Contains("Revoke"));

            var lockedGroup = repository.CreateGroup(string.Empty, "Core");
            var lockedChild = repository.CreateGroup(lockedGroup, "Inner");
            repository.LockGroup(lockedGroup, "secret", string.Empty);
            Assert.That(() => repository.SetGroupChecks(lockedChild, false), Throws.TypeOf<ArchitectorDataException>().With.Message.Contains("locked"));

            var parent = repository.CreateGroup(string.Empty, "Sketches");
            var child = repository.CreateGroup(parent, "Doors");
            repository.SetGroupChecks(parent, false);
            Assert.That(repository.IsUnchecked(child), Is.True);
            Assert.Throws<ArchitectorDataException>(() => repository.SetGroupChecks(child, false));
            Assert.Throws<ArchitectorDataException>(() => repository.SetGroupChecks(child, true));
        }

        [Test]
        public void CyclesInsideGroupsWithChecksOffAreAllowedUntilChecksReturn()
        {
            var groupId = repository.CreateGroup(string.Empty, "Sketches");
            var layerId = repository.CreateLayer(groupId, "Loop");
            var first = repository.CreateEmptyNode(layerId, 0f, 0f);
            var second = repository.CreateEmptyNode(layerId, 600f, 0f);
            repository.CreateEdge(layerId, first, second, ArchitectorEdgeKinds.Dependency, "left");
            Assert.Throws<ArchitectorDataException>(() => repository.CreateEdge(layerId, second, first, ArchitectorEdgeKinds.Dependency, "right"));

            repository.SetGroupChecks(groupId, false);
            Assert.DoesNotThrow(() => repository.CreateEdge(layerId, second, first, ArchitectorEdgeKinds.Dependency, "right"));

            Assert.That(() => repository.SetGroupChecks(groupId, true), Throws.TypeOf<ArchitectorDataException>().With.Message.Contains("cycle"));
            Assert.That(repository.IsUnchecked(layerId), Is.True);
        }

        [Test]
        public void RelationshipsWithLayersWithChecksOffAreErrorsAndBlockApproval()
        {
            var checkedLayer = repository.CreateLayer(string.Empty, "Doors");
            var groupId = repository.CreateGroup(string.Empty, "Sketches");
            var sketchLayer = repository.CreateLayer(groupId, "Lights");
            var door = repository.CreateEmptyNode(checkedLayer, 0f, 0f);
            var light = repository.CreateEmptyNode(sketchLayer, 0f, 0f);
            repository.CreateCrossLayerEdge(checkedLayer, door, sketchLayer, light, ArchitectorEdgeKinds.Dependency);
            repository.SetGroupChecks(groupId, false);
            using var index = new ArchitectorTypeIndex(repository);

            var report = ArchitectorConformance.Evaluate(repository, index, new[] { checkedLayer });

            var error = report.Diagnostics.Single(diagnostic => diagnostic.Code == ArchitectorConformanceCodes.LinkedToUncheckedLayer);
            Assert.That(error.Severity, Is.EqualTo(ArchitectorConformanceSeverities.Error));
            Assert.That(error.Message, Does.Contain("Lights"));
            Assert.That(report.UncheckedLayerCount, Is.Zero);
            Assert.That(() => repository.ApproveLayers(new[] { checkedLayer }), Throws.TypeOf<ArchitectorDataException>().With.Message.Contains("Lights"));

            var incoming = repository.CreateLayer(string.Empty, "Switches");
            var sketch = repository.CreateEmptyNode(sketchLayer, 600f, 0f);
            var lever = repository.CreateEmptyNode(incoming, 0f, 0f);
            repository.CreateCrossLayerEdge(sketchLayer, sketch, incoming, lever, ArchitectorEdgeKinds.Dependency);
            Assert.That(ArchitectorConformance.Evaluate(repository, index, new[] { incoming }).Diagnostics
                .Select(diagnostic => diagnostic.Code), Does.Contain(ArchitectorConformanceCodes.LinkedToUncheckedLayer));
        }

        [Test]
        public void FastLayerEditsKeepTheSessionEqualToAFreshLoadAndToCanonicalFiles()
        {
            var groupId = repository.CreateGroup(string.Empty, "Doors");
            var first = repository.CreateLayer(groupId, "Locks");
            var second = repository.CreateLayer(groupId, "Keys");
            var third = repository.CreateLayer(string.Empty, "Hall");
            var lockNode = repository.CreateEmptyNode(first, 0f, 0f);
            var keyNode = repository.CreateEmptyNode(second, 0f, 0f);
            var hallNode = repository.CreateEmptyNode(third, 0f, 0f);
            repository.CreateCrossLayerEdge(first, lockNode, second, keyNode, ArchitectorEdgeKinds.Dependency);
            repository.CreateCrossLayerEdge(third, hallNode, first, lockNode, ArchitectorEdgeKinds.Event);
            repository.ApproveLayers(new[] { first, second, third });
            AssertSessionMatchesDisk();

            repository.RenameEmptyNode(second, keyNode, "Master Key");
            AssertSessionMatchesDisk();
            Assert.That(repository.GetLayerApprovalStatus(first).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            Assert.That(repository.GetLayerApprovalStatus(third).State, Is.EqualTo(ArchitectorApprovalStates.Approved));

            var spare = repository.CreateEmptyNode(second, 600f, 0f);
            repository.CreateEdge(second, spare, keyNode, ArchitectorEdgeKinds.DataFlow, "auto");
            repository.MoveNode(second, spare, 900f, 300f);
            AssertSessionMatchesDisk();

            repository.DeleteNode(second, keyNode);
            AssertSessionMatchesDisk();
            Assert.That(repository.LoadLayer(first).semantic.nodes.Any(node => node.kind == ArchitectorNodeKinds.LayerPortal && node.portal.targetLayerId == second), Is.False);

            repository.RenameEntry(third, "Lobby");
            repository.DeleteNode(first, lockNode);
            AssertSessionMatchesDisk();
        }

        private static ArchitectorLayerDocuments Canonical(ArchitectorLayerDocuments documents)
        {
            documents.semantic.nodes.Sort((left, right) => string.CompareOrdinal(left.id, right.id));
            documents.semantic.edges.Sort((left, right) => string.CompareOrdinal(left.id, right.id));
            documents.layout.nodes.Sort((left, right) => string.CompareOrdinal(left.nodeId, right.nodeId));
            documents.layout.edges.Sort((left, right) => string.CompareOrdinal(left.edgeId, right.edgeId));
            return documents;
        }

        private void AssertSessionMatchesDisk()
        {
            var fresh = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            var layers = fresh.Project.layers.ToDictionary(layer => layer.id, layer => fresh.LoadLayer(layer.id), StringComparer.Ordinal);
            foreach (var layer in repository.Project.layers)
            {
                var session = Canonical(repository.LoadLayer(layer.id));
                var loaded = Canonical(fresh.LoadLayer(layer.id));
                Assert.That(ArchitectorJson.Serialize(session.semantic), Is.EqualTo(ArchitectorJson.Serialize(loaded.semantic)), layer.name);
                Assert.That(ArchitectorJson.Serialize(session.layout), Is.EqualTo(ArchitectorJson.Serialize(loaded.layout)), layer.name);
                Assert.That(repository.GetLayerApprovalStatus(layer.id).CurrentHash, Is.EqualTo(fresh.GetLayerApprovalStatus(layer.id).CurrentHash), layer.name);
            }
            var root = Path.Combine(temporaryRoot, ArchitectorPaths.DataDirectoryName);
            foreach (var file in ArchitectorStorageCodec.Encode(fresh.Project, layers))
                Assert.That(File.ReadAllText(Path.Combine(root, file.Key)), Is.EqualTo(file.Value), file.Key);
        }

        [Test]
        public void ReviewCountsDraftAndChangedLayers()
        {
            var approvedId = repository.CreateLayer(string.Empty, "Approved");
            repository.CreateLayer(string.Empty, "Draft");
            repository.ApproveLayers(new[] { approvedId });
            Assert.That(ArchitectorReviewWindow.PendingCount(repository), Is.EqualTo(1));

            repository.CreateEmptyNode(approvedId, 0f, 0f);
            Assert.That(ArchitectorReviewWindow.PendingCount(repository), Is.EqualTo(2));

            repository.ApproveLayers(repository.Project.layers.Select(layer => layer.id).ToArray());
            Assert.That(ArchitectorReviewWindow.PendingCount(repository), Is.Zero);
        }
    }
}
