using System;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Architector.Editor.Mcp.Tests
{
    public sealed class ArchitectorMcpToolTests
    {
        private string temporaryRoot;
        private ArchitectorRepository repository;
        private Func<string, bool> confirmation;

        [SetUp]
        public void SetUp()
        {
            temporaryRoot = Path.Combine(Path.GetTempPath(), "ArchitectorMcpTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
            confirmation = ArchitectorApprovalGate.Confirm;
            ArchitectorApprovalGate.Confirm = _ => true;
        }

        [TearDown]
        public void TearDown()
        {
            ArchitectorApprovalGate.Confirm = confirmation;
            if (!string.IsNullOrEmpty(temporaryRoot) && Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, true);
        }

        [Test]
        public void QueryReturnsCompactProjectSummary()
        {
            repository.CreateGroup(string.Empty, "Architecture");
            repository.CreateLayer(string.Empty, "Runtime");

            var response = ArchitectorMcpQueryTool.HandleCommand(
                repository,
                JObject.Parse("{ 'action': 'project_summary' }")) as SuccessResponse;

            Assert.That(response, Is.Not.Null);
            var data = JObject.FromObject(response.Data);
            Assert.That(data["revision"]?.Value<int>(), Is.EqualTo(repository.Revision));
            Assert.That(data["group_count"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(data["layer_count"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(data["root_groups"], Has.Count.EqualTo(1));
            Assert.That(data["root_layers"], Has.Count.EqualTo(1));
        }

        [Test]
        public void QueryCapabilitiesDescribesSemanticPlanContract()
        {
            var response = ArchitectorMcpQueryTool.HandleCommand(
                repository,
                JObject.Parse("{ 'action': 'capabilities' }")) as SuccessResponse;

            Assert.That(response, Is.Not.Null);
            var data = JObject.FromObject(response.Data);
            Assert.That(data["query_actions"]?.Values<string>(), Does.Contain("type_search"));
            Assert.That(data["query_actions"]?.Values<string>(), Does.Contain("type_contract"));
            Assert.That(data["query_actions"]?.Values<string>(), Does.Contain("conformance"));
            Assert.That(data["edit_actions"]?.Values<string>(), Does.Contain("apply_plan"));
            Assert.That(data["relationship_kinds"]?.Values<string>(), Does.Contain(ArchitectorEdgeKinds.Callback));
            Assert.That(data["semantic_plan"]?["nodeContracts"]?.Values<string>(), Does.Contain("node:string"));
            Assert.That(data["semantic_plan"]?["nodeContracts"]?.Values<string>(), Does.Contain("baseType:{typeName|typeKey}?"));
            Assert.That(data["semantic_plan"]?["layerPurposes"]?.Values<string>(), Does.Contain("purpose:string"));
            Assert.That(data["semantic_plan"]?["entryMoves"]?.Values<string>(), Does.Contain("entries:string[]"));
            Assert.That(data["semantic_plan"]?["entryMoves"]?.Values<string>(), Does.Contain("parent:string?"));
            Assert.That(data["semantic_plan"]?["portals"]?.Values<string>(), Does.Contain("entryReference:string"));
            Assert.That(data["semantic_plan"]?["deletions"]?.Values<string>(), Does.Contain("kind:entry|node|edge"));
            Assert.That(data["semantic_plan"]?["edges"]?.Values<string>(), Does.Contain("sourceLayer:string"));
        }

        [Test]
        public void ConformanceQueryReturnsBoundedProjectAggregate()
        {
            var response = ArchitectorMcpQueryTool.HandleCommand(
                repository,
                JObject.Parse("{ 'action': 'conformance', 'limit': 1 }")) as SuccessResponse;

            Assert.That(response, Is.Not.Null);
            var data = JObject.FromObject(response.Data);
            Assert.That(data["scope"]?.Value<string>(), Is.EqualTo("project"));
            Assert.That(data["state"]?.Value<string>(), Is.EqualTo(ArchitectorConformanceStates.Incomplete));
            Assert.That(data["counts"]?["layers"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(data["diagnostics"], Has.Count.EqualTo(1));
            Assert.That(data["diagnostics"]?[0]?["code"]?.Value<string>(), Is.EqualTo(ArchitectorConformanceCodes.NoLinkedContracts));
            Assert.That(data["truncated"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void FindReturnsLayerByPurposeAndContainedTypeWithoutSearchingMembers()
        {
            var layerId = repository.CreateLayer(string.Empty, "Runtime");
            repository.UpdateLayerPurpose(layerId, "Owns nightclub audio playback.");
            var nodeId = repository.CreateManualNode(layerId, 0f, 0f);
            repository.UpdateManualNode(
                layerId,
                nodeId,
                "Audio Player",
                "class",
                "NightClub.Audio",
                "AudioPlayer",
                Array.Empty<string>(),
                new[] { "public" },
                new[] { new ArchitectorMemberRecord { id = "member_play", kind = "method", declaration = "public void HiddenSearchToken()" } },
                null,
                new[] { ArchitectorTypeReference.Declared("NightClub.Audio.IAudioOutput", "interface") });

            var byPurpose = ArchitectorMcpQueryTool.HandleCommand(
                repository,
                JObject.Parse("{ 'action': 'find', 'query': 'nightclub audio', 'limit': 10 }")) as SuccessResponse;
            var byType = ArchitectorMcpQueryTool.HandleCommand(
                repository,
                JObject.Parse("{ 'action': 'find', 'query': 'NightClub.Audio.AudioPlayer', 'limit': 10 }")) as SuccessResponse;
            var byMember = ArchitectorMcpQueryTool.HandleCommand(
                repository,
                JObject.Parse("{ 'action': 'find', 'query': 'HiddenSearchToken', 'limit': 10 }")) as SuccessResponse;
            var byInterface = ArchitectorMcpQueryTool.HandleCommand(
                repository,
                JObject.Parse("{ 'action': 'find', 'query': 'IAudioOutput', 'limit': 10 }")) as SuccessResponse;

            var purposeResults = JObject.FromObject(byPurpose.Data)["results"];
            var typeResults = JObject.FromObject(byType.Data)["results"];
            var memberResults = JObject.FromObject(byMember.Data)["results"];
            var interfaceResults = JObject.FromObject(byInterface.Data)["results"];
            Assert.That(purposeResults?.Any(item => item["id"]?.Value<string>() == layerId), Is.True);
            Assert.That(typeResults?.Any(item => item["matched_node_types"]?.Values<string>().Contains("NightClub.Audio.AudioPlayer") == true), Is.True);
            Assert.That(memberResults, Is.Empty);
            Assert.That(interfaceResults?.Any(item => item["id"]?.Value<string>() == layerId), Is.True);
        }

        [Test]
        public void EditDryRunAndApplyUseRevisionAndOneUndoOperation()
        {
            const string layerId = "layer_mcp";
            var documents = new JObject
            {
                [ArchitectorPaths.LayerPath(layerId)] = ArchitectorJson.Serialize(new ArchitectorStoredLayerDocument
                {
                    layerId = layerId,
                    name = "MCP Layer"
                }),
                [ArchitectorPaths.LayoutPath(layerId)] = ArchitectorJson.Serialize(new ArchitectorStoredLayoutDocument { layerId = layerId })
            };
            var request = new JObject
            {
                ["action"] = "dry_run",
                ["expected_revision"] = repository.Revision,
                ["action_name"] = "MCP Architecture Plan",
                ["documents"] = documents
            };

            var preview = ArchitectorMcpEditTool.HandleCommand(repository, request) as SuccessResponse;

            Assert.That(preview, Is.Not.Null);
            Assert.That(repository.FindLayer(layerId), Is.Null);

            request["action"] = "apply";
            var applied = ArchitectorMcpEditTool.HandleCommand(repository, request) as SuccessResponse;

            Assert.That(applied, Is.Not.Null);
            Assert.That(repository.FindLayer(layerId), Is.Not.Null);
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Draft));
            Assert.That(repository.NextUndoName, Is.EqualTo("MCP Architecture Plan"));
            repository.Undo();
            Assert.That(repository.FindLayer(layerId), Is.Null);
        }

        [Test]
        public void EditRejectsStaleRevisionBeforeReadingDocuments()
        {
            var response = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "apply",
                ["expected_revision"] = repository.Revision + 1
            }) as ErrorResponse;

            Assert.That(response, Is.Not.Null);
            Assert.That(response.Code, Is.EqualTo("stale_revision"));
            var data = JObject.FromObject(response.Data);
            Assert.That(data["current_revision"]?.Value<int>(), Is.EqualTo(repository.Revision));
        }

        [Test]
        public void EditAppliesCompactSemanticPlanAndReturnsReferenceMap()
        {
            var request = JObject.Parse(@"{
                'action': 'apply_plan',
                'action_name': 'MCP Semantic Plan',
                'plan': {
                    'groups': [{ 'reference': 'root', 'name': 'Architecture' }],
                    'layers': [{ 'reference': 'runtime', 'name': 'Runtime', 'parent': 'root' }],
                    'nodes': [{ 'reference': 'entry', 'layer': 'runtime', 'title': 'Entry', 'x': 0, 'y': 0 }],
                    'portals': [],
                    'edges': []
                }
            }");
            request["expected_revision"] = repository.Revision;

            var response = ArchitectorMcpEditTool.HandleCommand(repository, request) as SuccessResponse;

            Assert.That(response, Is.Not.Null);
            var data = JObject.FromObject(response.Data);
            Assert.That(data["references"]?["root"]?.Value<string>(), Does.StartWith("group_"));
            Assert.That(data["references"]?["runtime"]?.Value<string>(), Does.StartWith("layer_"));
            Assert.That(data["references"]?["entry"]?.Value<string>(), Does.StartWith("node_"));
            Assert.That(repository.NextUndoName, Is.EqualTo("MCP Semantic Plan"));
            Assert.That(repository.Project.layers.Single().name, Is.EqualTo("Runtime"));
        }

        [Test]
        public void AiWorkSessionLocksMutationsAndReportsIndependentOverrides()
        {
            var aiWork = new ArchitectorAiWorkCoordinator();
            var begin = ArchitectorMcpEditTool.HandleCommand(repository, aiWork, new JObject
            {
                ["action"] = "begin_work",
                ["expected_revision"] = repository.Revision,
                ["work_label"] = "MCP test"
            }) as SuccessResponse;
            Assert.That(begin, Is.Not.Null);
            var token = JObject.FromObject(begin.Data)["work_token"]?.Value<string>();
            Assert.That(token, Does.StartWith("ai-work_"));
            Assert.That(JObject.FromObject(begin.Data)["started_revision"]?.Value<int>(), Is.EqualTo(repository.Revision));

            var planRequest = JObject.Parse(@"{
                'action': 'apply_plan',
                'action_name': 'Locked Plan',
                'plan': {
                    'groups': [{ 'reference': 'root', 'name': 'Architecture' }],
                    'layers': [], 'nodes': [], 'portals': [], 'edges': []
                }
            }");
            planRequest["expected_revision"] = repository.Revision;
            var rejected = ArchitectorMcpEditTool.HandleCommand(repository, aiWork, planRequest) as ErrorResponse;
            Assert.That(rejected?.Code, Is.EqualTo("validation_failed"));
            Assert.That(repository.Project.groups, Is.Empty);

            planRequest["work_token"] = token;
            var applied = ArchitectorMcpEditTool.HandleCommand(repository, aiWork, planRequest) as SuccessResponse;
            Assert.That(applied, Is.Not.Null);
            Assert.That(repository.Project.groups.Single().name, Is.EqualTo("Architecture"));

            aiWork.UnlockWorkspacesByUser();
            var status = ArchitectorMcpQueryTool.HandleCommand(
                repository,
                aiWork,
                JObject.Parse("{ 'action': 'work_status' }")) as SuccessResponse;
            var statusData = JObject.FromObject(status.Data);
            Assert.That(statusData["active"]?.Value<bool>(), Is.True);
            Assert.That(statusData["workspaces_locked"]?.Value<bool>(), Is.False);
            Assert.That(statusData["settings_locked"]?.Value<bool>(), Is.True);
            Assert.That(statusData["awaiting_agent_resume"]?.Value<bool>(), Is.False);

            var ended = ArchitectorMcpEditTool.HandleCommand(repository, aiWork, new JObject
            {
                ["action"] = "end_work",
                ["work_token"] = token
            }) as SuccessResponse;
            Assert.That(ended, Is.Not.Null);
            Assert.That(aiWork.IsActive, Is.False);
        }

        [Test]
        public void TypeSearchContractAndLinkedPlanUseTheSharedIndexIdentity()
        {
            repository.Settings.sourceIndex.includedRoots.Clear();
            repository.Settings.sourceIndex.includedRoots.Add(ArchitectorPackagePaths.Root + "/Tests/Mcp");
            repository.Settings.sourceIndex.excludedRoots.Clear();
            using var index = new ArchitectorTypeIndex(repository);
            var aiWork = new ArchitectorAiWorkCoordinator();

            var search = ArchitectorMcpQueryTool.HandleCommand(repository, aiWork, JObject.Parse(
                "{ 'action': 'type_search', 'query': 'ArchitectorMcpToolTests', 'limit': 5 }"), index) as SuccessResponse;
            var searchData = JObject.FromObject(search.Data);
            var type = (JObject)searchData["results"]?.Single();
            var typeKey = type?["key"]?.Value<string>();
            Assert.That(typeKey, Is.Not.Empty);

            var contract = ArchitectorMcpQueryTool.HandleCommand(repository, aiWork, new JObject
            {
                ["action"] = "type_contract",
                ["target"] = typeKey,
                ["limit"] = 100
            }, index) as SuccessResponse;
            var contractData = JObject.FromObject(contract.Data);
            var memberKey = contractData["members"]?.First?["symbol_key"]?.Value<string>();
            Assert.That(memberKey, Is.Not.Empty);
            Assert.That(contractData["members"]?.Any(member => member["framework_member"]?.Value<bool>() == true), Is.False);
            var filtered = ArchitectorMcpQueryTool.HandleCommand(repository, aiWork, new JObject
            {
                ["action"] = "type_contract",
                ["target"] = typeKey,
                ["query"] = "ToString"
            }, index) as SuccessResponse;
            Assert.That(JObject.FromObject(filtered.Data)["members"]?.All(member =>
                member["declaration"]?.Value<string>().Contains("ToString") == true), Is.True);

            var request = new JObject
            {
                ["action"] = "apply_plan",
                ["expected_revision"] = repository.Revision,
                ["action_name"] = "Create Linked Contract",
                ["plan"] = JObject.FromObject(new ArchitectorSemanticPlan
                {
                    layers = new System.Collections.Generic.List<ArchitectorPlanLayer>
                    {
                        new() { reference = "runtime", name = "Runtime", purpose = "MCP linked contract test." }
                    },
                    nodes = new System.Collections.Generic.List<ArchitectorPlanNode>
                    {
                        new()
                        {
                            reference = "consumer",
                            layer = "runtime",
                            title = "Consumer",
                            mode = "none",
                            x = 0f,
                            y = 0f
                        },
                        new()
                        {
                            reference = "linked",
                            layer = "runtime",
                            title = "MCP Tool Tests",
                            mode = "linked",
                            typeKey = typeKey,
                            x = 400f,
                            y = 0f,
                            members = new System.Collections.Generic.List<ArchitectorPlanMember>
                            {
                                new() { symbolKey = memberKey }
                            }
                        }
                    },
                    edges = new System.Collections.Generic.List<ArchitectorPlanEdge>
                    {
                        new()
                        {
                            sourceLayer = "runtime",
                            source = "consumer",
                            targetLayer = "runtime",
                            target = "linked",
                            kind = ArchitectorEdgeKinds.Dependency
                        }
                    }
                })
            };

            var applied = ArchitectorMcpEditTool.HandleCommand(repository, aiWork, request, index) as SuccessResponse;

            Assert.That(applied, Is.Not.Null);
            var layer = repository.Project.layers.Single();
            Assert.That(layer.purpose, Is.EqualTo("MCP linked contract test."));
            var node = repository.LoadLayer(layer.id).semantic.nodes.Single(candidate =>
                string.Equals(candidate.type.mode, "linked", StringComparison.Ordinal));
            Assert.That(node.type.mode, Is.EqualTo("linked"));
            Assert.That(node.members.Single().symbolKey, Is.EqualTo(memberKey));
            var approval = repository.GetLayerApprovalStatus(layer.id);
            Assert.That(approval.State, Is.EqualTo(ArchitectorApprovalStates.Draft));
            Assert.That(Approve(layer.id, true), Is.InstanceOf<SuccessResponse>());
            Assert.That(repository.GetLayerApprovalStatus(layer.id).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
        }
        [Test]
        public void DraftReviewApprovalAndSemanticChangesAreSeparateUndoSteps()
        {
            var layerId = repository.CreateLayer(string.Empty, "Draft");
            var nodeId = repository.CreateManualNode(layerId, 0f, 0f);
            Assert.That(Approve(layerId, false), Is.InstanceOf<ErrorResponse>());
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Draft));
            Assert.That(Approve(layerId, true), Is.InstanceOf<SuccessResponse>());
            var approvedHash = repository.FindLayer(layerId).approval.semanticHash;

            var move = new JObject
            {
                ["action"] = "apply_plan",
                ["expected_revision"] = repository.Revision,
                ["plan"] = JObject.Parse("{ 'moves': [{ 'layer': '" + layerId + "', 'node': '" + nodeId + "', 'x': 450, 'y': 0 }] }")
            };
            Assert.That(ArchitectorMcpEditTool.HandleCommand(repository, move), Is.InstanceOf<SuccessResponse>());
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));

            var rename = new JObject
            {
                ["action"] = "apply_plan",
                ["expected_revision"] = repository.Revision,
                ["plan"] = JObject.Parse("{ 'renames': [{ 'kind': 'node', 'layer': '" + layerId + "', 'target': '" + nodeId + "', 'name': 'Reviewed Later' }] }")
            };
            Assert.That(ArchitectorMcpEditTool.HandleCommand(repository, rename), Is.InstanceOf<SuccessResponse>());
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            Assert.That(repository.FindLayer(layerId).approval.semanticHash, Is.EqualTo(approvedHash));
            repository.Undo();
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            repository.Redo();
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            Assert.That(Approve(layerId, true), Is.InstanceOf<SuccessResponse>());
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Approved));
            repository.Undo();
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
        }

        [TestCase("apply")]
        [TestCase("apply_plan")]
        [TestCase("dry_run")]
        [TestCase("dry_run_plan")]
        public void CombinedSaveAndApprovalIsRejected(string action)
        {
            var revision = repository.Revision;
            var result = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = action,
                ["expected_revision"] = revision,
                ["approve_changes"] = true
            }) as ErrorResponse;
            Assert.That(result?.Code, Is.EqualTo("approval_is_separate"));
            Assert.That(repository.Revision, Is.EqualTo(revision));
            Assert.That(repository.Project.layers, Is.Empty);
        }

        [Test]
        public void NarrowQueriesAnswerWithoutTheWholeLayer()
        {
            var first = repository.CreateLayer(string.Empty, "First");
            var second = repository.CreateLayer(string.Empty, "Second");
            var third = repository.CreateLayer(string.Empty, "Third");
            var a = repository.CreateEmptyNode(first, 0f, 0f);
            var b = repository.CreateEmptyNode(second, 0f, 0f);
            var c = repository.CreateEmptyNode(third, 0f, 0f);
            repository.CreateCrossLayerEdge(first, a, second, b, ArchitectorEdgeKinds.Dependency);
            repository.CreateCrossLayerEdge(second, b, third, c, ArchitectorEdgeKinds.Dependency);
            JObject Query(JObject request)
            {
                var result = ArchitectorMcpQueryTool.HandleCommand(repository, new ArchitectorAiWorkCoordinator(), request) as SuccessResponse;
                Assert.That(result, Is.Not.Null, request.ToString());
                return JObject.FromObject(result.Data);
            }

            var node = Query(new JObject { ["action"] = "node", ["target"] = "Second", ["node"] = b });
            Assert.That(node["incoming"], Has.Count.EqualTo(1));
            Assert.That(node["outgoing"], Has.Count.EqualTo(1));

            var outgoing = Query(new JObject { ["action"] = "relationships", ["target"] = "Second", ["direction"] = "out", ["limit"] = 1 });
            Assert.That(outgoing["total"]?.Value<int>(), Is.EqualTo(1));

            var path = Query(new JObject { ["action"] = "dependency_path", ["target"] = "First", ["to"] = "Third" });
            Assert.That(path["connected"]?.Value<bool>(), Is.True);
            Assert.That(path["steps"], Has.Count.EqualTo(2));
            var reverse = Query(new JObject { ["action"] = "dependency_path", ["target"] = "Third", ["to"] = "First" });
            Assert.That(reverse["connected"]?.Value<bool>(), Is.False);

            var missing = ArchitectorMcpQueryTool.HandleCommand(repository, new ArchitectorAiWorkCoordinator(), new JObject { ["action"] = "layer_graph", ["target"] = "Sec" }) as ErrorResponse;
            Assert.That(missing?.Code, Is.EqualTo("layer_not_found"));
            Assert.That(JObject.FromObject(missing.Data)["candidates"]?.Values<string>(), Does.Contain("Second"));
        }

        [Test]
        public void FailedPlanNamesTheOperation()
        {
            var aiWork = new ArchitectorAiWorkCoordinator();
            var request = new JObject
            {
                ["action"] = "dry_run_plan",
                ["plan"] = new JObject
                {
                    ["layers"] = new JArray(new JObject { ["reference"] = "first", ["name"] = "First" }),
                    ["nodes"] = new JArray(
                        new JObject { ["reference"] = "good", ["layer"] = "first", ["title"] = "Good" },
                        new JObject { ["reference"] = "bad", ["layer"] = "missing", ["title"] = "Bad" })
                }
            };

            var failed = ArchitectorMcpEditTool.HandleCommand(repository, aiWork, request) as ErrorResponse;

            Assert.That(failed?.Code, Is.EqualTo("validation_failed"));
            Assert.That(JObject.FromObject(failed.Data)["operation"]?.Value<string>(), Is.EqualTo("nodes[1]"));
        }

        [Test]
        public void AgentApprovalWaitsForTheUserUnlessTheModeAllowsIt()
        {
            var layerId = repository.CreateLayer(string.Empty, "Draft");
            var aiWork = new ArchitectorAiWorkCoordinator();
            var token = aiWork.Begin("Review test", repository.Revision);
            var preferences = ArchitectorAgentPreferences.Current;
            var previousMode = preferences.WorkflowMode;
            JObject Request() => new()
            {
                ["action"] = "approve_layers", ["layers"] = new JArray(layerId), ["approve_changes"] = true,
                ["expected_revision"] = repository.Revision, ["work_token"] = token
            };

            ArchitectorApprovalGate.Confirm = _ => false;
            var refused = ArchitectorMcpEditTool.HandleCommand(repository, aiWork, Request()) as ErrorResponse;
            Assert.That(refused?.Code, Is.EqualTo("user_refused"));
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Draft));

            ArchitectorAgentWorkflowModes.Register(new ArchitectorAgentWorkflowMode("test", "Test", "Test", "Test", agentMayApprove: true, showsNotice: true));
            try
            {
                preferences.WorkflowMode = "test";
                Assert.That(ArchitectorMcpEditTool.HandleCommand(repository, aiWork, Request()), Is.Not.InstanceOf<ErrorResponse>());
                Assert.That(repository.FindLayer(layerId).approval.approvedByAgent, Is.True);
                repository.ApproveLayers(new[] { layerId });
                Assert.That(repository.FindLayer(layerId).approval.approvedByAgent, Is.False);
            }
            finally
            {
                preferences.WorkflowMode = previousMode;
            }
        }

        [Test]
        public void SeparateApprovalRequiresCurrentRevisionAndWorkToken()
        {
            var layerId = repository.CreateLayer(string.Empty, "Draft");
            var aiWork = new ArchitectorAiWorkCoordinator();
            var token = aiWork.Begin("Review test", repository.Revision);
            var request = new JObject
            {
                ["action"] = "approve_layers", ["layers"] = new JArray(layerId),
                ["approve_changes"] = true, ["expected_revision"] = repository.Revision - 1,
                ["work_token"] = token
            };
            var stale = ArchitectorMcpEditTool.HandleCommand(repository, aiWork, request) as ErrorResponse;
            Assert.That(stale?.Code, Is.EqualTo("stale_revision"));
            request["expected_revision"] = repository.Revision;
            request.Remove("work_token");
            Assert.That(ArchitectorMcpEditTool.HandleCommand(repository, aiWork, request), Is.InstanceOf<ErrorResponse>());
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Draft));
            request["work_token"] = token;
            Assert.That(ArchitectorMcpEditTool.HandleCommand(repository, aiWork, request), Is.InstanceOf<SuccessResponse>());
            aiWork.End(token);
        }

        [Test]
        public void CanonicalSemanticEditPreservesApprovalHashAndRejectsForgedApproval()
        {
            var layerId = repository.CreateLayer(string.Empty, "Reviewed");
            var nodeId = repository.CreateManualNode(layerId, 0f, 0f);
            repository.ApproveLayers(new[] { layerId });
            var oldHash = repository.FindLayer(layerId).approval.semanticHash;
            var layerPath = repository.FindLayer(layerId).semanticPath;
            var stored = ArchitectorJson.Read<ArchitectorStoredLayerDocument>(repository.Paths.ToAbsoluteDataPath(layerPath));
            stored.nodes.Single(node => node.id == nodeId).title = "Draft edit";
            var result = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "apply", ["expected_revision"] = repository.Revision,
                ["documents"] = new JObject { [layerPath] = ArchitectorJson.Serialize(stored) }
            });
            Assert.That(result, Is.InstanceOf<SuccessResponse>());
            Assert.That(repository.GetLayerApprovalStatus(layerId).State, Is.EqualTo(ArchitectorApprovalStates.Changed));
            Assert.That(repository.FindLayer(layerId).approval.semanticHash, Is.EqualTo(oldHash));

            var baselinePath = "Baselines/" + Path.GetFileName(Directory.GetFiles(Path.Combine(temporaryRoot, "Architector", "Baselines"), layerId + ".*").Single());
            var baseline = ArchitectorJson.Read<ArchitectorStoredBaselineDocument>(repository.Paths.ToAbsoluteDataPath(baselinePath));
            baseline.semanticHash = repository.GetLayerApprovalStatus(layerId).CurrentHash;
            var revision = repository.Revision;
            var forged = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "apply", ["expected_revision"] = revision,
                ["documents"] = new JObject { [baselinePath] = ArchitectorJson.Serialize(baseline) }
            }) as ErrorResponse;
            Assert.That(forged?.Code, Is.EqualTo("validation_failed"));
            Assert.That(repository.Revision, Is.EqualTo(revision));
            Assert.That(repository.FindLayer(layerId).approval.semanticHash, Is.EqualTo(oldHash));
        }

        [Test]
        public void DryRunPlanTokenAppliesTheSamePlanOnlyAtTheSameRevision()
        {
            var plan = JObject.Parse("{ 'layers': [{ 'reference': 'runtime', 'name': 'Tokened' }] }");
            var dryRun = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "dry_run_plan", ["expected_revision"] = repository.Revision, ["plan"] = plan
            }) as SuccessResponse;
            var token = JObject.FromObject(dryRun.Data)["plan_token"]?.Value<string>();
            Assert.That(token, Does.StartWith("plan_"));

            var applied = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "apply_plan", ["expected_revision"] = repository.Revision,
                ["plan"] = new JObject { ["plan_token"] = token }
            });
            Assert.That(applied, Is.InstanceOf<SuccessResponse>());
            Assert.That(repository.Project.layers.Single().name, Is.EqualTo("Tokened"));

            var second = JObject.FromObject((ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "dry_run_plan", ["expected_revision"] = repository.Revision,
                ["plan"] = JObject.Parse("{ 'layers': [{ 'reference': 'other', 'name': 'Other' }] }")
            }) as SuccessResponse).Data)["plan_token"]?.Value<string>();
            repository.CreateGroup(string.Empty, "Concurrent Change");
            var stale = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "apply_plan", ["expected_revision"] = repository.Revision,
                ["plan"] = new JObject { ["plan_token"] = second }
            }) as ErrorResponse;
            Assert.That(stale?.Code, Is.EqualTo("plan_token_stale"));
            var unknown = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "apply_plan", ["expected_revision"] = repository.Revision,
                ["plan"] = new JObject { ["plan_token"] = "plan_missing" }
            }) as ErrorResponse;
            Assert.That(unknown?.Code, Is.EqualTo("plan_token_unknown"));
        }

        [Test]
        public void InvalidDraftCycleDoesNotWriteOrAdvanceRevision()
        {
            var request = JObject.Parse(@"{
                'action': 'apply_plan',
                'plan': {
                    'layers': [{ 'reference': 'runtime', 'name': 'Draft' }],
                    'nodes': [
                        { 'reference': 'a', 'layer': 'runtime', 'title': 'A' },
                        { 'reference': 'b', 'layer': 'runtime', 'title': 'B', 'x': 450 }
                    ],
                    'edges': [
                        { 'sourceLayer': 'runtime', 'source': 'a', 'targetLayer': 'runtime', 'target': 'b', 'kind': 'dependency' },
                        { 'sourceLayer': 'runtime', 'source': 'b', 'targetLayer': 'runtime', 'target': 'a', 'kind': 'dependency' }
                    ]
                }
            }");
            var revision = repository.Revision;
            request["expected_revision"] = revision;
            var result = ArchitectorMcpEditTool.HandleCommand(repository, request) as ErrorResponse;
            Assert.That(result?.Code, Is.EqualTo("validation_failed"));
            Assert.That(repository.Revision, Is.EqualTo(revision));
            Assert.That(repository.Project.layers, Is.Empty);
        }

        [Test]
        public void DraftCreationDoesNotReserializeUnrelatedCrLfLayer()
        {
            var existing = repository.CreateLayer(string.Empty, "Existing");
            var record = repository.FindLayer(existing);
            var semanticPath = Path.Combine(temporaryRoot, "Architector", record.semanticPath);
            var layoutPath = Path.Combine(temporaryRoot, "Architector", record.layoutPath);
            var semantic = File.ReadAllText(semanticPath).Replace("\r\n", "\n").Replace("\n", "\r\n");
            var layout = File.ReadAllText(layoutPath).Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(semanticPath, semantic);
            File.WriteAllText(layoutPath, layout);
            var result = ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "apply_plan", ["expected_revision"] = repository.Revision,
                ["plan"] = JObject.Parse("{ 'layers': [{ 'reference': 'new', 'name': 'New Draft' }] }")
            }) as SuccessResponse;
            Assert.That(result, Is.Not.Null);
            Assert.That(JObject.FromObject(result.Data)["changed_file_count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(File.ReadAllText(semanticPath), Is.EqualTo(semantic));
            Assert.That(File.ReadAllText(layoutPath), Is.EqualTo(layout));
        }

        private object Approve(string layerId, bool confirmed)
        {
            return ArchitectorMcpEditTool.HandleCommand(repository, new JObject
            {
                ["action"] = "approve_layers", ["layers"] = new JArray(layerId),
                ["expected_revision"] = repository.Revision, ["approve_changes"] = confirmed
            });
        }

    }
}
