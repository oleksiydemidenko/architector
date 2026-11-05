using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Architector.Editor.Agents
{
    public static class ArchitectorAgentQuery
    {
        private const int MaxCandidates = 5;

        private sealed class Parameters
        {
            public string action { get; set; }

            public string target { get; set; }

            public string query { get; set; }

            public int? limit { get; set; }

            public bool? include_layout { get; set; }

            public string node { get; set; }

            public string to { get; set; }

            public string direction { get; set; }

            public string detail { get; set; }

            public int? offset { get; set; }
        }

        public static ArchitectorAgentResult Handle(
            ArchitectorRepository repository,
            ArchitectorAiWorkCoordinator aiWork,
            JObject @params,
            ArchitectorTypeIndex typeIndex = null)
        {
            return (ArchitectorAgentResult)Dispatch(repository, aiWork, @params, typeIndex);
        }

        private static object Dispatch(
            ArchitectorRepository repository,
            ArchitectorAiWorkCoordinator aiWork,
            JObject @params,
            ArchitectorTypeIndex typeIndex = null)
        {
            if (repository == null)
                return ArchitectorAgentResult.Fail("repository_unavailable");
            if (@params == null)
                return ArchitectorAgentResult.Fail("invalid_parameters", new { message = "Parameters cannot be null." });

            try
            {
                var parameters = @params.ToObject<Parameters>() ?? new Parameters();
                switch (parameters.action?.Trim().ToLowerInvariant())
                {
                    case "capabilities":
                        return Capabilities(repository);
                    case "work_status":
                        return WorkStatus(repository, aiWork);
                    case "project_summary":
                        return ProjectSummary(repository);
                    case "group_contents":
                        return GroupContents(repository, parameters.target);
                    case "layer_graph":
                        return LayerGraph(repository, parameters.target, parameters.include_layout == true, parameters.detail);
                    case "node":
                        return NodeDetails(repository, parameters.target, parameters.node);
                    case "relationships":
                        return Relationships(repository, parameters.target, parameters.node, parameters.direction, parameters.limit ?? 50, parameters.offset ?? 0);
                    case "dependency_path":
                        return DependencyPath(repository, parameters.target, parameters.to);
                    case "setup_check":
                        return WithTypeIndex(repository, typeIndex, index => SetupCheck(repository, index));
                    case "approval_status":
                        return ApprovalStatus(repository, parameters.target, parameters.limit ?? 100);
                    case "baseline_diff":
                        return BaselineDiff(repository, parameters.target);
                    case "conformance":
                        return WithTypeIndex(repository, typeIndex, index => Conformance(repository, index, parameters.target, parameters.limit ?? 20));
                    case "find":
                        return Find(repository, parameters.query, parameters.limit ?? 20, parameters.offset ?? 0);
                    case "type_search":
                        return WithTypeIndex(repository, typeIndex, index => TypeSearch(repository, index, parameters.query, parameters.limit ?? 20, parameters.offset ?? 0));
                    case "type_contract":
                        return WithTypeIndex(repository, typeIndex, index => TypeContract(repository, index, parameters.target, parameters.query, parameters.limit ?? 100, parameters.offset ?? 0));
                    case "validate":
                        return Validation(repository);
                    default:
                        return ArchitectorAgentResult.Fail("unsupported_action", new
                        {
                            message = "Valid actions: capabilities, work_status, project_summary, group_contents, layer_graph, node, relationships, dependency_path, setup_check, approval_status, baseline_diff, conformance, find, type_search, type_contract, validate."
                        });
                }
            }
            catch (ArchitectorDataException exception)
            {
                return ArchitectorAgentResult.Fail("validation_failed", new { message = exception.Message });
            }
            catch (Exception exception)
            {
                return ArchitectorAgentResult.Fail("query_failed", new { message = exception.Message });
            }
        }

        private static object ProjectSummary(ArchitectorRepository repository)
        {
            var project = repository.Project;
            var approvals = repository.GetLayerApprovalStatuses();
            return ArchitectorAgentResult.Ok("Architector project summary.", new
            {
                project_id = project.projectId,
                revision = repository.Revision,
                group_count = project.groups.Count,
                layer_count = project.layers.Count,
                workflow_mode = ArchitectorAgentPreferences.Current.WorkflowMode,
                approval_counts = new
                {
                    draft = approvals.Count(item => item.State == ArchitectorApprovalStates.Draft),
                    approved = approvals.Count(item => item.State == ArchitectorApprovalStates.Approved),
                    changed = approvals.Count(item => item.State == ArchitectorApprovalStates.Changed)
                },
                root_groups = project.rootGroupIds.Select(id => Entry(repository, repository.FindGroup(id))).ToArray(),
                root_layers = project.rootLayerIds.Select(id => Entry(repository, repository.FindLayer(id))).ToArray()
            });
        }

        private static object Capabilities(ArchitectorRepository repository)
        {
            return ArchitectorAgentResult.Ok("Architector AI contract capabilities.", new
            {
                revision = repository.Revision,
                workflow_mode = ArchitectorAgentPreferences.Current.WorkflowMode,
                workflow_rule = ArchitectorAgentPreferences.Current.Mode.AgentRule,
                agent_may_approve = ArchitectorAgentPreferences.Current.Mode.AgentMayApprove,
                window_locking = ArchitectorAgentPreferences.Current.LockWindowsDuringAgentWork,
                dependency_cycles = CyclePolicyName(repository),
                query_actions = new[] { "capabilities", "work_status", "project_summary", "group_contents", "layer_graph", "node", "relationships", "dependency_path", "setup_check", "approval_status", "baseline_diff", "conformance", "find", "type_search", "type_contract", "validate" },
                edit_actions = new[] { "begin_work", "resume_work", "end_work", "approve_layers", "revoke_approvals", "revert_to_approved", "dry_run", "apply", "dry_run_plan", "apply_plan", "arrange_layer", "arrange_changed_layers", "relink_node", "validate_external", "reload_external" },
                relationship_kinds = ArchitectorEdgeKinds.All,
                semantic_plan = new
                {
                    groups = new[] { "reference:string", "name:string", "parent:string?" },
                    layers = new[] { "reference:string", "name:string", "parent:string?", "purpose:string?" },
                    nodes = new[] { "reference:string", "layer:string", "title:string", "responsibility:string?", "mode:none|declared|linked", "typeKind:string?", "typeNamespace:string?", "typeName:string?", "typeKey:string?", "baseType:{typeName|typeKey}?", "interfaces:array?", "members:array?", "x:number", "y:number" },
                    nodeContracts = new[] { "layer:string", "node:string", "title:string?", "responsibility:string?", "mode:none|declared|linked?", "typeKind:string?", "typeNamespace:string?", "typeName:string?", "typeKey:string?", "baseType:{typeName|typeKey}?", "interfaces:array?", "members:array" },
                    portals = new[]
                    {
                        "exitReference:string", "entryReference:string", "fromLayer:string", "toLayer:string",
                        "exitX:number", "exitY:number", "entryX:number", "entryY:number"
                    },
                    edges = new[] { "reference:string?", "sourceLayer:string", "source:string", "targetLayer:string", "target:string", "kind:string", "label:string?" },
                    renames = new[] { "kind:entry|node", "target:string", "layer:string?", "name:string" },
                    entryMoves = new[] { "entries:string[]", "parent:string?", "groupIndex:int?", "layerIndex:int?" },
                    moves = new[] { "layer:string", "node:string", "x:number", "y:number" },
                    edgeChanges = new[] { "edge:string", "kind:string", "label:string?" },
                    layerPurposes = new[] { "layer:string", "purpose:string" },
                    layerScopes = new[] { "layer:string", "folders:string[]", "namespaces:string[]" },
                    deletions = new[] { "kind:entry|node|edge", "target:string", "layer:string?" },
                    rules = new[]
                    {
                        "Every created entity has one plan-unique reference.",
                        "References may also be existing immutable IDs or Explorer paths such as Core/Player/Movement.",
                        "Group and layer names are unique among siblings; the path identifies an entry.",
                        "A layer with an approval baseline shows only Portals to other approved layers; layer_graph marks the others visible_in_layer=false. Hidden Portals remain canonical and validated.",
                        "Layer code scope (layerScopes: folders or single files, and namespaces; the most specific entry wins) maps code to layers for the dependency evidence tool; it does not affect approval. Run: dotnet run --project \"" + ArchitectorPackagePaths.DependencyEvidenceProject + "\" -- <repository root>.",
                        "A locked group (locked=true) rejects every change to its groups, layers, layouts and approvals, and new dependencies from inside it to outside. Portals and relationships into it are allowed. There is no unlock action: never try to bypass a lock or find its password; propose the change to the user.",
                        "type_search ranks runtime assemblies before Editor and Tests assemblies and returns key, name, kind, first source and existing occurrences.",
                        "dry_run_plan returns plan_token; apply_plan with plan={\"plan_token\": \"...\"} applies the same plan without resending it while the revision is unchanged.",
                        "type_contract hides framework members and interface declarations the type implements itself; pass query to filter members by declaration text, which also includes framework members.",
                        "Fill a node responsibility (one line, at most 240 characters) when its role is not obvious from the name; it explains the node and does not affect approval.",
                        "Raw dry_run/apply documents use the storage layout: architector.project.json, Groups/<id>.group.json, Layers/<id>.layer.json (header, own nodes and intra-layer edges), Layouts/<id>.layout.json and Links/<pair-id>.link.json (both Portal endpoints and cross-layer relationships). Prefer semantic plans.",
                        "Prefer narrow reads: layer_graph with detail=summary returns the layer as compact text with paths and names and no IDs (incoming relationships folded per source layer), node takes target (layer) and node (ID or title), relationships takes target, optional node and direction=in|out|both, dependency_path takes target and to (layers). find, type_search, type_contract and relationships take offset and report next_offset when more results exist.",
                        "Do not arrange while working: call arrange_changed_layers once before end_work; it lays out layers created in this work session and places nodes added to existing layers next to their neighbours, and reports layers in locked groups as skipped. arrange_layer takes target and optional nodes and centers (node IDs) for an explicit user request. relink_node takes target, node and type_key, keeps the node ID and relationships, and reports dropped_members. Both need expected_revision and work_token.",
                        "setup_check reports whether source roots, indexed types, the server, the bridge and Run In Background are ready.",
                        "A failed semantic plan reports operation, for example edges[3], when the failure belongs to one plan item. An unknown or ambiguous layer returns candidates.",
                        "Parent groups must exist or appear before their children.",
                        "A cross-layer edge creates the Portal pair between its layers when it is missing, and deleting the last relationship between two layers removes the pair. portals is optional: it only sets the position of a pair, new or existing, and a pair left without relationships is removed.",
                        "Nodes and portals are placed with normal grid and collision rules.",
                        "Entry deletion is recursive; Portal node/layer deletion removes reciprocal endpoints.",
                        "All creation and mutation arrays form one validated transaction and one Undo action.",
                        "begin_work requires expected_revision and returns a work_token.",
                        "Script compilation preserves the lock in an awaiting-agent state; resume_work or any authenticated mutation confirms the agent resumed.",
                        "Mutations during active work require its work_token; end_work releases the session.",
                        "apply_plan requires expected_revision; dry_run_plan never writes.",
                        "apply/apply_plan save designs without approval; do not pass approve_changes.",
                        "Approval means the design is agreed and canonical. New layers are Draft; semantic edits to approved layers make them Changed; layout-only edits preserve approval.",
                        "approve_layers only when the user explicitly asks: pass layer IDs, current expected_revision and approve_changes=true. revoke_approvals and revert_to_approved take the same layer IDs and revision. Unless agent_may_approve is true, Architector asks the user to confirm in the Editor and the call fails with user_refused when they decline. No workflow mode approves implicitly.",
                        "When agent_may_approve is true the agent may approve without asking; the baseline records that the agent approved the layer.",
                        "workflow_mode is the user's local preference and the agent cannot change it; workflow_rule says what it requires.",
                        "Rework an approved layer in place; it stays canonical as Changed until the user approves or reverts it. baseline_diff lists changes since approval; revert_to_approved restores the approved version only when the user asks.",
                        "dependency_cycles is the project rule from shared settings: reject refuses any plan that closes a dependency cycle and names the cycle; report saves source-backed cycles, reports them as dependency_cycle conformance warnings and in validate, and does not block approval. Do not relabel a real dependency as an event to avoid a cycle. The agent cannot change the rule.",
                        "End the work session before handing results to the user; validation, revision and work-token checks always apply."
                    }
                }
            });
        }

        private static object WorkStatus(ArchitectorRepository repository, ArchitectorAiWorkCoordinator aiWork)
        {
            return ArchitectorAgentResult.Ok("Architector AI work session status.", new
            {
                revision = repository.Revision,
                active = aiWork?.IsActive == true,
                label = aiWork?.Label ?? string.Empty,
                workspaces_locked = aiWork?.WorkspacesLocked == true,
                settings_locked = aiWork?.SettingsLocked == true,
                workspaces_user_override = aiWork?.WorkspacesUnlockedByUser == true,
                settings_user_override = aiWork?.SettingsUnlockedByUser == true,
                awaiting_agent_resume = aiWork?.IsAwaitingAgentResume == true,
                started_revision = aiWork?.StartedRevision ?? -1
            });
        }

        private static object GroupContents(ArchitectorRepository repository, string target)
        {
            var group = ResolveGroup(repository, target);
            if (group == null)
                return ArchitectorAgentResult.Fail("group_not_found", new { target });
            return ArchitectorAgentResult.Ok("Architector group contents.", new
            {
                revision = repository.Revision,
                group = new { group.id, group.name, group.parentGroupId },
                child_groups = group.childGroupIds.Select(id => Entry(repository, repository.FindGroup(id))).ToArray(),
                layers = group.layerIds.Select(id => Entry(repository, repository.FindLayer(id))).ToArray()
            });
        }

        private static object NodeDetails(ArchitectorRepository repository, string target, string nodeReference)
        {
            var layer = ResolveLayer(repository, target);
            if (layer == null)
                return LayerFailure(repository, target);
            var documents = repository.LoadLayer(layer.id);
            var node = FindNode(documents, nodeReference);
            if (node == null)
                return NodeFailure(documents, target, nodeReference);
            var edges = repository.GetRelationshipsForLayer(layer.id)
                .Where(edge => edge.sourceNodeId == node.id || edge.targetNodeId == node.id)
                .ToArray();
            return ArchitectorAgentResult.Ok("Architector node.", new
            {
                revision = repository.Revision,
                layer = new { layer.id, layer.name },
                node = new
                {
                    node.id,
                    node.title,
                    node.responsibility,
                    node.kind,
                    type_mode = node.type?.mode ?? "none",
                    type_name = FullTypeName(node.type),
                    base_type = TypeReference(node.type?.baseType),
                    interfaces = (node.type?.interfaces ?? new List<ArchitectorTypeReferenceRecord>()).Select(TypeReference).ToArray(),
                    members = node.members.Select(member => new { member.id, member.kind, member.declaration }).ToArray()
                },
                incoming = edges.Where(edge => edge.targetNodeId == node.id).Select(edge => EdgeView(repository, edge)).ToArray(),
                outgoing = edges.Where(edge => edge.sourceNodeId == node.id).Select(edge => EdgeView(repository, edge)).ToArray()
            });
        }

        private static object Relationships(
            ArchitectorRepository repository,
            string target,
            string nodeReference,
            string direction,
            int requestedLimit,
            int requestedOffset)
        {
            var layer = ResolveLayer(repository, target);
            if (layer == null)
                return LayerFailure(repository, target);
            var documents = repository.LoadLayer(layer.id);
            var node = string.IsNullOrWhiteSpace(nodeReference) ? null : FindNode(documents, nodeReference);
            if (!string.IsNullOrWhiteSpace(nodeReference) && node == null)
                return NodeFailure(documents, target, nodeReference);
            var mode = direction?.Trim().ToLowerInvariant() ?? "both";
            var edges = repository.GetRelationshipsForLayer(layer.id).Where(edge =>
            {
                var outgoing = node != null ? edge.sourceNodeId == node.id : edge.sourceLayerId == layer.id;
                var incoming = node != null ? edge.targetNodeId == node.id : edge.targetLayerId == layer.id;
                return mode == "out" ? outgoing : mode == "in" ? incoming : outgoing || incoming;
            }).ToArray();
            var limit = Math.Max(1, Math.Min(200, requestedLimit));
            var offset = Math.Max(0, requestedOffset);
            return ArchitectorAgentResult.Ok("Architector relationships.", new
            {
                revision = repository.Revision,
                total = edges.Length,
                relationships = edges.Skip(offset).Take(limit).Select(edge => EdgeView(repository, edge)).ToArray(),
                next_offset = offset + limit < edges.Length ? offset + limit : (int?)null
            });
        }

        private static object DependencyPath(ArchitectorRepository repository, string target, string to)
        {
            var source = ResolveLayer(repository, target);
            if (source == null)
                return LayerFailure(repository, target);
            var destination = ResolveLayer(repository, to);
            if (destination == null)
                return LayerFailure(repository, to);
            var edges = repository.Project.layers
                .SelectMany(layer => repository.LoadLayer(layer.id).semantic.edges)
                .Where(edge => edge.kind == ArchitectorEdgeKinds.Dependency && edge.sourceLayerId != edge.targetLayerId)
                .ToLookup(edge => edge.sourceLayerId, StringComparer.Ordinal);
            var previous = new Dictionary<string, ArchitectorEdgeRecord>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal) { source.id };
            var pending = new Queue<string>();
            pending.Enqueue(source.id);
            while (pending.Count > 0 && !visited.Contains(destination.id))
            {
                foreach (var edge in edges[pending.Dequeue()])
                {
                    if (!visited.Add(edge.targetLayerId))
                        continue;
                    previous[edge.targetLayerId] = edge;
                    pending.Enqueue(edge.targetLayerId);
                }
            }
            if (!visited.Contains(destination.id))
                return ArchitectorAgentResult.Ok("No dependency path exists between these layers.", new
                {
                    revision = repository.Revision,
                    connected = false
                });

            var path = new List<ArchitectorEdgeRecord>();
            for (var current = destination.id; current != source.id; current = previous[current].sourceLayerId)
                path.Add(previous[current]);
            path.Reverse();
            return ArchitectorAgentResult.Ok("Dependency path between layers.", new
            {
                revision = repository.Revision,
                connected = true,
                steps = path.Select(edge => EdgeView(repository, edge)).ToArray()
            });
        }

        private static object SetupCheck(ArchitectorRepository repository, ArchitectorTypeIndex typeIndex)
        {
            var items = ArchitectorSetupCheck.Run(repository, typeIndex);
            return ArchitectorAgentResult.Ok("Architector setup check.", new
            {
                ready = items.All(item => item.Passed),
                items = items.Select(item => new { name = item.Name, passed = item.Passed, details = item.Details }).ToArray()
            });
        }

        private static object EdgeView(ArchitectorRepository repository, ArchitectorEdgeRecord edge)
        {
            return new
            {
                edge.id,
                edge.kind,
                edge.label,
                source_layer = repository.GetEntryPath(edge.sourceLayerId),
                source = repository.LoadLayer(edge.sourceLayerId).semantic.nodes.First(node => node.id == edge.sourceNodeId).title,
                target_layer = repository.GetEntryPath(edge.targetLayerId),
                target = repository.LoadLayer(edge.targetLayerId).semantic.nodes.First(node => node.id == edge.targetNodeId).title
            };
        }

        private static ArchitectorNodeRecord FindNode(ArchitectorLayerDocuments documents, string reference)
        {
            var nodes = documents.semantic.nodes;
            return nodes.FirstOrDefault(node => node.id == reference)
                   ?? SingleOrNull(nodes.Where(node => string.Equals(node.title, reference, StringComparison.OrdinalIgnoreCase)));
        }

        private static object NodeFailure(ArchitectorLayerDocuments documents, string target, string reference)
        {
            return ArchitectorAgentResult.Fail("node_not_found", new
            {
                target,
                node = reference,
                candidates = documents.semantic.nodes
                    .Where(node => !string.IsNullOrEmpty(reference) && node.title.IndexOf(reference, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(node => new { node.id, node.title })
                    .Take(MaxCandidates)
                    .ToArray()
            });
        }

        internal static ArchitectorAgentResult LayerFailure(ArchitectorRepository repository, string target)
        {
            var text = target?.Trim() ?? string.Empty;
            var exact = repository.Project.layers
                .Where(layer => string.Equals(layer.name, text, StringComparison.OrdinalIgnoreCase))
                .Select(layer => repository.GetEntryPath(layer.id))
                .ToArray();
            if (exact.Length > 1)
                return ArchitectorAgentResult.Fail("layer_ambiguous", new { target, candidates = exact });
            return ArchitectorAgentResult.Fail("layer_not_found", new
            {
                target,
                candidates = repository.Project.layers
                    .Where(layer => text.Length > 0 && layer.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(layer => repository.GetEntryPath(layer.id))
                    .Take(MaxCandidates)
                    .ToArray()
            });
        }

        private static object LayerGraph(ArchitectorRepository repository, string target, bool includeLayout, string detail)
        {
            var layer = ResolveLayer(repository, target);
            if (layer == null)
                return LayerFailure(repository, target);
            if (string.Equals(detail?.Trim(), "summary", StringComparison.OrdinalIgnoreCase))
            {
                return ArchitectorAgentResult.Ok("Architector layer summary.", new
                {
                    revision = repository.Revision,
                    summary = ArchitectorLayerSummary.Describe(repository, layer.id)
                });
            }
            var documents = repository.LoadLayer(layer.id);
            var approval = repository.GetLayerApprovalStatus(layer.id);
            var layouts = documents.layout.nodes.ToDictionary(node => node.nodeId, StringComparer.Ordinal);
            var nodes = documents.semantic.nodes.Select(node => new
            {
                node.id,
                node.title,
                node.responsibility,
                node.kind,
                visible_in_layer = repository.IsPortalEndpointVisible(layer.id, node),
                type_mode = node.type?.mode ?? "none",
                type_kind = node.type?.kind,
                type_name = node.type == null || string.IsNullOrEmpty(node.type.name)
                    ? null
                    : string.IsNullOrEmpty(node.type.typeNamespace) ? node.type.name : $"{node.type.typeNamespace}.{node.type.name}",
                type_key = string.Equals(node.type?.mode, "linked", StringComparison.Ordinal)
                    ? ArchitectorTypeIdentity.Key(node.type.assemblyName, node.type.metadataName)
                    : null,
                base_type = TypeReference(node.type?.baseType),
                interfaces = (node.type?.interfaces ?? new System.Collections.Generic.List<ArchitectorTypeReferenceRecord>())
                    .Select(TypeReference).ToArray(),
                member_count = node.members.Count,
                members = node.members.Select(member => new
                {
                    member.id,
                    member.kind,
                    member.declaration,
                    symbol_key = string.IsNullOrEmpty(member.symbolKey) ? null : member.symbolKey
                }).ToArray(),
                portal = node.portal == null || string.IsNullOrEmpty(node.portal.targetLayerId) ? null : new
                {
                    node.portal.pairId,
                    node.portal.role,
                    node.portal.targetLayerId,
                    target_layer_name = repository.FindLayer(node.portal.targetLayerId)?.name,
                    node.portal.counterpartNodeId
                },
                layout = includeLayout && layouts.TryGetValue(node.id, out var rectangle) ? new
                {
                    rectangle.x,
                    rectangle.y,
                    rectangle.width,
                    rectangle.height
                } : null
            }).ToArray();
            var edges = repository.GetRelationshipsForLayer(layer.id).Select(edge => new
            {
                edge.id,
                edge.kind,
                edge.label,
                edge.sourceLayerId,
                edge.sourceNodeId,
                source_layer_name = repository.FindLayer(edge.sourceLayerId)?.name,
                source_title = repository.LoadLayer(edge.sourceLayerId).semantic.nodes
                    .First(node => node.id == edge.sourceNodeId).title,
                edge.targetLayerId,
                edge.targetNodeId,
                target_layer_name = repository.FindLayer(edge.targetLayerId)?.name,
                target_title = repository.LoadLayer(edge.targetLayerId).semantic.nodes
                    .First(node => node.id == edge.targetNodeId).title
            }).ToArray();
            return ArchitectorAgentResult.Ok("Architector layer graph.", new
            {
                revision = repository.Revision,
                layer = new
                {
                    layer.id,
                    layer.name,
                    layer.purpose,
                    approval = new { state = approval.State }
                },
                nodes,
                edges
            });
        }

        private static object BaselineDiff(ArchitectorRepository repository, string target)
        {
            var layer = ResolveLayer(repository, target);
            if (layer == null)
                return LayerFailure(repository, target);
            var status = repository.GetLayerApprovalStatus(layer.id);
            return ArchitectorAgentResult.Ok("Changes since the approved baseline.", new
            {
                revision = repository.Revision,
                layer = new { layer.id, layer.name, path = repository.GetEntryPath(layer.id) },
                state = status.State,
                changes = repository.GetBaselineChanges(layer.id).Select(change => new
                {
                    change = change.Change,
                    subject = change.Subject,
                    id = change.Id,
                    caption = change.Caption
                }).ToArray()
            });
        }

        private static object ApprovalStatus(ArchitectorRepository repository, string target, int requestedLimit)
        {
            var limit = Math.Max(1, Math.Min(100, requestedLimit));
            IEnumerable<string> layerIds;
            if (string.IsNullOrWhiteSpace(target))
            {
                layerIds = repository.Project.layers.Select(layer => layer.id);
            }
            else
            {
                var layer = ResolveLayer(repository, target);
                if (layer != null)
                    layerIds = new[] { layer.id };
                else
                {
                    var group = ResolveGroup(repository, target);
                    if (group == null)
                        return ArchitectorAgentResult.Fail("target_not_found", new { target });
                    var groupIds = new HashSet<string>(StringComparer.Ordinal) { group.id };
                    var pending = new Queue<string>();
                    pending.Enqueue(group.id);
                    while (pending.Count > 0)
                    {
                        var current = repository.FindGroup(pending.Dequeue());
                        foreach (var childId in current.childGroupIds)
                        {
                            if (!groupIds.Add(childId))
                                continue;
                            pending.Enqueue(childId);
                        }
                    }
                    layerIds = repository.Project.layers
                        .Where(candidate => groupIds.Contains(candidate.parentGroupId))
                        .Select(candidate => candidate.id);
                }
            }

            var ids = layerIds.Take(limit + 1).ToArray();
            var statuses = repository.GetLayerApprovalStatuses(ids.Take(limit));
            return ArchitectorAgentResult.Ok("Architector layer approval status.", new
            {
                revision = repository.Revision,
                workflow_mode = ArchitectorAgentPreferences.Current.WorkflowMode,
                layers = statuses.Select(item => new
                {
                    layer_id = item.LayerId,
                    layer_name = item.LayerName,
                    state = item.State,
                    responsibilities_changed = repository.HasUnapprovedResponsibilities(item.LayerId)
                }).ToArray(),
                truncated = ids.Length > limit
            });
        }

        private static object Conformance(
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            string target,
            int requestedLimit)
        {
            var limit = Math.Max(1, Math.Min(100, requestedLimit));
            string scope;
            string targetId;
            string targetName;
            IEnumerable<string> layerIds;
            if (string.IsNullOrWhiteSpace(target))
            {
                scope = "project";
                targetId = repository.Project.projectId;
                targetName = "Project";
                layerIds = repository.Project.layers.Select(layer => layer.id);
            }
            else
            {
                var layer = ResolveLayer(repository, target);
                if (layer != null)
                {
                    scope = "layer";
                    targetId = layer.id;
                    targetName = layer.name;
                    layerIds = new[] { layer.id };
                }
                else
                {
                    var group = ResolveGroup(repository, target);
                    if (group == null)
                        return ArchitectorAgentResult.Fail("target_not_found", new { target });
                    scope = "group";
                    targetId = group.id;
                    targetName = group.name;
                    var groupIds = DescendantGroupIds(repository, group.id);
                    layerIds = repository.Project.layers
                        .Where(candidate => groupIds.Contains(candidate.parentGroupId))
                        .Select(candidate => candidate.id);
                }
            }

            var report = ArchitectorConformance.Evaluate(repository, typeIndex, layerIds, limit);
            return ArchitectorAgentResult.Ok("Architector compiled conformance report.", new
            {
                revision = repository.Revision,
                scope,
                target_id = targetId,
                target_name = targetName,
                state = report.State,
                counts = new
                {
                    layers = report.LayerCount,
                    approved_layers = report.ApprovedLayerCount,
                    unapproved_layers = report.UnapprovedLayerCount,
                    linked_types = report.LinkedTypeCount,
                    selected_members = report.SelectedMemberCount,
                    selected_type_facts = report.SelectedTypeFactCount,
                    passed = report.PassedCount,
                    warnings = report.WarningCount,
                    errors = report.ErrorCount,
                    unverified_members = report.UnverifiedMemberCount,
                    unverified_type_facts = report.UnverifiedTypeFactCount
                },
                diagnostics = report.Diagnostics.Select(item => new
                {
                    severity = item.Severity,
                    code = item.Code,
                    layer_id = item.LayerId,
                    layer_name = item.LayerName,
                    node_id = string.IsNullOrEmpty(item.NodeId) ? null : item.NodeId,
                    node_title = string.IsNullOrEmpty(item.NodeTitle) ? null : item.NodeTitle,
                    member_id = string.IsNullOrEmpty(item.MemberId) ? null : item.MemberId,
                    expected = item.Expected,
                    observed = item.Observed,
                    message = item.Message
                }).ToArray(),
                diagnostic_count = report.TotalDiagnosticCount,
                truncated = report.DiagnosticsTruncated
            });
        }

        private static HashSet<string> DescendantGroupIds(ArchitectorRepository repository, string rootGroupId)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal) { rootGroupId };
            var pending = new Queue<string>();
            pending.Enqueue(rootGroupId);
            while (pending.Count > 0)
            {
                var group = repository.FindGroup(pending.Dequeue());
                foreach (var childId in group.childGroupIds)
                {
                    if (ids.Add(childId))
                        pending.Enqueue(childId);
                }
            }
            return ids;
        }

        private static object Find(ArchitectorRepository repository, string query, int requestedLimit, int requestedOffset)
        {
            if (string.IsNullOrWhiteSpace(query))
                return ArchitectorAgentResult.Fail("query_required");
            var limit = Math.Max(1, Math.Min(100, requestedLimit));
            var offset = Math.Max(0, requestedOffset);
            var wanted = offset + limit + 1;
            var matches = new List<object>();
            bool Matches(string value) => value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

            foreach (var group in repository.Project.groups.Where(group => Matches(group.name)))
                matches.Add(new { category = "group", group.id, group.name });
            foreach (var layer in repository.Project.layers)
            {
                var semantic = repository.LoadLayer(layer.id).semantic;
                var matchingNodes = semantic.nodes.Where(node => Matches(node.title)
                                                                 || Matches(node.type?.name)
                                                                 || Matches(node.type?.typeNamespace)
                                                                 || Matches(FullTypeName(node.type))
                                                                 || Matches(ArchitectorTypeReference.FullName(node.type?.baseType))
                                                                 || (node.type?.interfaces ?? new System.Collections.Generic.List<ArchitectorTypeReferenceRecord>())
                                                                 .Any(reference => Matches(ArchitectorTypeReference.FullName(reference)))).ToArray();
                if (Matches(layer.name) || Matches(layer.purpose) || matchingNodes.Length > 0)
                    matches.Add(new
                    {
                        category = "layer",
                        layer.id,
                        layer.name,
                        layer.purpose,
                        matched_node_types = matchingNodes.SelectMany(NodeTypeNames).Distinct().ToArray()
                    });
                if (matches.Count >= wanted)
                    break;
                foreach (var node in semantic.nodes.Where(node => Matches(node.title)))
                {
                    matches.Add(new
                    {
                        category = "node",
                        node.id,
                        node.title,
                        node.kind,
                        layer_id = layer.id,
                        layer_name = layer.name
                    });
                    if (matches.Count >= wanted)
                        break;
                }
                if (matches.Count >= wanted)
                    break;
            }

            return ArchitectorAgentResult.Ok("Architector search results.", new
            {
                revision = repository.Revision,
                query,
                limit,
                results = matches.Skip(offset).Take(limit).ToArray(),
                next_offset = matches.Count > offset + limit ? offset + limit : (int?)null
            });
        }

        private static object TypeSearch(
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            string query,
            int requestedLimit,
            int requestedOffset)
        {
            if (string.IsNullOrWhiteSpace(query))
                return ArchitectorAgentResult.Fail("query_required");
            var text = query.Trim();
            var limit = Math.Max(1, Math.Min(100, requestedLimit));
            var offset = Math.Max(0, requestedOffset);
            var occurrences = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (var layer in repository.Project.layers)
            {
                foreach (var node in repository.LoadLayer(layer.id).semantic.nodes)
                {
                    if (string.Equals(node.type?.mode, "linked", StringComparison.Ordinal))
                        AddTypeOccurrence(occurrences, ArchitectorTypeIdentity.Key(node.type.assemblyName, node.type.metadataName), layer, node, "node");
                    if (string.Equals(node.type?.baseType?.mode, "linked", StringComparison.Ordinal))
                        AddTypeOccurrence(occurrences, ArchitectorTypeReference.Key(node.type.baseType), layer, node, "base");
                    foreach (var interfaceType in (node.type?.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                                 .Where(reference => string.Equals(reference.mode, "linked", StringComparison.Ordinal)))
                        AddTypeOccurrence(occurrences, ArchitectorTypeReference.Key(interfaceType), layer, node, "interface");
                }
            }
            var ranked = typeIndex.Types
                .Select(type => new { type, rank = TypeSearchRank(type, text) })
                .Where(item => item.rank < int.MaxValue)
                .OrderBy(item => item.rank)
                .ThenBy(item => IsToolingAssembly(item.type.AssemblyName) ? 1 : 0)
                .ThenBy(item => item.type.FullDisplayName, StringComparer.OrdinalIgnoreCase)
                .Skip(offset)
                .Take(limit + 1)
                .ToArray();
            var results = ranked
                .Take(limit)
                .Select(item => new
                {
                    key = item.type.Key,
                    name = item.type.FullDisplayName,
                    kind = item.type.Kind,
                    source = item.type.SourceDeclarations.Select(source => source.repositoryPath).FirstOrDefault(),
                    occurrences = occurrences.TryGetValue(item.type.Key, out var placed) ? placed.ToArray() : null
                }).ToArray();
            return ArchitectorAgentResult.Ok("Architector indexed type search results.", new
            {
                revision = repository.Revision,
                query = text,
                limit,
                results,
                next_offset = ranked.Length > limit ? offset + limit : (int?)null
            });
        }

        private static void AddTypeOccurrence(
            IDictionary<string, List<object>> occurrences,
            string key,
            ArchitectorLayerRecord layer,
            ArchitectorNodeRecord node,
            string role)
        {
            if (!occurrences.TryGetValue(key, out var items))
                occurrences.Add(key, items = new List<object>());
            items.Add(new { layer_id = layer.id, layer_name = layer.name, node_id = node.id, node_title = node.title, role });
        }

        private static object TypeContract(
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            string target,
            string memberQuery,
            int requestedLimit,
            int requestedOffset)
        {
            if (string.IsNullOrWhiteSpace(target))
                return ArchitectorAgentResult.Fail("target_required");
            var candidates = typeIndex.Types.Where(type => string.Equals(type.Key, target, StringComparison.Ordinal)
                                                           || string.Equals(type.FullDisplayName, target, StringComparison.OrdinalIgnoreCase)
                                                           || string.Equals(type.DisplayName, target, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0)
                return ArchitectorAgentResult.Fail("type_not_found", new { target });
            if (candidates.Length > 1)
                return ArchitectorAgentResult.Fail("type_ambiguous", new { target, keys = candidates.Select(type => type.Key).ToArray() });
            var type = candidates[0];
            var linked = new ArchitectorTypeRecord { mode = "linked", assemblyName = type.AssemblyName, metadataName = type.MetadataName };
            var limit = Math.Max(1, Math.Min(500, requestedLimit));
            var allMembers = typeIndex.GetMembers(linked);
            var baseType = typeIndex.GetDirectBase(linked);
            var interfaces = typeIndex.GetInterfaces(linked);
            var filter = memberQuery?.Trim() ?? string.Empty;
            IReadOnlyList<ArchitectorIndexedMember> visible;
            if (filter.Length > 0)
            {
                visible = allMembers
                    .Where(member => member.Declaration.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();
            }
            else
            {
                var ownSignatures = allMembers
                    .Where(member => !member.IsInterfaceMember)
                    .Select(MemberSignature)
                    .ToHashSet(StringComparer.Ordinal);
                visible = allMembers
                    .Where(member => !member.IsFrameworkMember
                                     && !(member.IsInterfaceMember && ownSignatures.Contains(MemberSignature(member))))
                    .ToArray();
            }
            var offset = Math.Max(0, requestedOffset);
            var members = visible.Skip(offset).Take(limit).Select(member => new
            {
                symbol_key = member.SymbolKey,
                kind = member.Kind,
                declaration = member.Declaration,
                declaring_type = member.DeclaringTypeName,
                parent_depth = member.ParentDepth,
                interface_member = member.IsInterfaceMember,
                framework_member = member.IsFrameworkMember
            }).ToArray();
            return ArchitectorAgentResult.Ok("Architector indexed type contract.", new
            {
                revision = repository.Revision,
                type = new { key = type.Key, name = type.FullDisplayName, kind = type.Kind, assembly = type.AssemblyName },
                direct_base = baseType == null ? null : new { key = baseType.Key, name = baseType.FullDisplayName, kind = baseType.Kind },
                interfaces = interfaces.Select(item => new { key = item.Key, name = item.FullDisplayName, kind = item.Kind }).ToArray(),
                members,
                total_member_count = allMembers.Count,
                hidden_member_count = allMembers.Count - visible.Count,
                next_offset = visible.Count > offset + members.Length ? offset + members.Length : (int?)null
            });
        }

        private static string MemberSignature(ArchitectorIndexedMember member)
        {
            var parts = member.SymbolKey.Split('|');
            return parts.Length < 2 ? member.SymbolKey : string.Join("|", parts.Where((_, index) => index != 1));
        }

        private static bool IsToolingAssembly(string assemblyName)
        {
            return (assemblyName ?? string.Empty).Split('.', '-')
                .Any(part => string.Equals(part, "Editor", StringComparison.Ordinal)
                             || string.Equals(part, "Tests", StringComparison.Ordinal));
        }

        private static int TypeSearchRank(ArchitectorIndexedType type, string query)
        {
            if (string.Equals(type.DisplayName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.FullDisplayName, query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (type.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
            if (type.FullDisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
            if (type.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (type.FullDisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return 4;
            if (type.SourceDeclarations.Any(source => source.repositoryPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) return 5;
            return int.MaxValue;
        }

        private static object WithTypeIndex(
            ArchitectorRepository repository,
            ArchitectorTypeIndex existing,
            Func<ArchitectorTypeIndex, object> action)
        {
            if (existing != null)
                return action(existing);
            using var temporary = new ArchitectorTypeIndex(repository);
            return action(temporary);
        }

        private static string FullTypeName(ArchitectorTypeRecord type)
        {
            if (type == null || string.IsNullOrEmpty(type.name))
                return string.Empty;
            return string.IsNullOrEmpty(type.typeNamespace) ? type.name : $"{type.typeNamespace}.{type.name}";
        }

        private static IEnumerable<string> NodeTypeNames(ArchitectorNodeRecord node)
        {
            var names = new List<string> { FullTypeName(node?.type), ArchitectorTypeReference.FullName(node?.type?.baseType) };
            names.AddRange((node?.type?.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                .Select(ArchitectorTypeReference.FullName));
            return names.Where(value => !string.IsNullOrEmpty(value));
        }

        private static object TypeReference(ArchitectorTypeReferenceRecord reference)
        {
            if (reference == null)
                return null;
            return new
            {
                mode = reference.mode,
                kind = reference.kind,
                name = ArchitectorTypeReference.FullName(reference),
                key = string.Equals(reference.mode, "linked", StringComparison.Ordinal)
                    ? ArchitectorTypeReference.Key(reference)
                    : null
            };
        }

        private static object Validation(ArchitectorRepository repository)
        {
            var result = repository.ValidateExternalChanges();
            var cycles = repository.FindDependencyCycles();
            return ArchitectorAgentResult.Ok(cycles.Count == 0
                ? "All canonical Architector documents are valid."
                : $"All canonical Architector documents are valid; the graph has {cycles.Count} dependency cycles.", new
            {
                revision = repository.Revision,
                changed_file_count = result.ChangedFileCount,
                validated_file_count = result.ValidatedFileCount,
                layer_count = result.LayerCount,
                portal_pair_count = result.PortalPairCount,
                dependency_cycle_policy = CyclePolicyName(repository),
                dependency_cycles = cycles.Select(cycle => new
                {
                    layers = cycle.LayerIds.Select(id => repository.FindLayer(id)?.name ?? id).ToArray(),
                    node_count = cycle.Members.Count,
                    path = repository.DescribeDependencyCycle(cycle),
                    relationships = cycle.Path.Select(edge => edge.id).ToArray()
                }).ToArray()
            });
        }

        private static string CyclePolicyName(ArchitectorRepository repository) =>
            repository.Settings.rules.dependencyCycles == ArchitectorDependencyCyclePolicy.Report ? "report" : "reject";

        private static ArchitectorGroupRecord ResolveGroup(ArchitectorRepository repository, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return null;
            return repository.FindGroup(target)
                   ?? repository.FindGroup(ArchitectorEntryPaths.Resolve(repository.Project, target))
                   ?? SingleOrNull(repository.Project.groups.Where(group =>
                       string.Equals(group.name, target, StringComparison.OrdinalIgnoreCase)));
        }

        internal static ArchitectorLayerRecord ResolveLayer(ArchitectorRepository repository, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return null;
            return repository.FindLayer(target)
                   ?? repository.FindLayer(ArchitectorEntryPaths.Resolve(repository.Project, target))
                   ?? SingleOrNull(repository.Project.layers.Where(layer =>
                       string.Equals(layer.name, target, StringComparison.OrdinalIgnoreCase)));
        }

        private static T SingleOrNull<T>(IEnumerable<T> candidates) where T : class
        {
            var matches = candidates.Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static object Entry(ArchitectorRepository repository, ArchitectorGroupRecord group)
        {
            return group == null
                ? null
                : new
                {
                    group.id,
                    group.name,
                    path = repository.GetEntryPath(group.id),
                    locked = repository.IsGroupLockedForEditing(group.id),
                    lock_notice = group.lockState?.locked == true ? group.lockState.notice : null,
                    checks_off = repository.IsUnchecked(group.id) ? true : (bool?)null
                };
        }

        private static object Entry(ArchitectorRepository repository, ArchitectorLayerRecord layer)
        {
            return layer == null
                ? null
                : new
                {
                    layer.id,
                    layer.name,
                    layer.purpose,
                    path = repository.GetEntryPath(layer.id),
                    scope_folders = layer.scope?.folders ?? new List<string>(),
                    scope_namespaces = layer.scope?.namespaces ?? new List<string>(),
                    checks_off = repository.IsUnchecked(layer.id) ? true : (bool?)null
                };
        }

        private static object Result(ArchitectorRepository repository, ArchitectorCanonicalBatchResult result)
        {
            return new
            {
                revision = repository.Revision,
                changed_file_count = result.ChangedFileCount,
                validated_file_count = result.ValidatedFileCount,
                layer_count = result.LayerCount,
                portal_pair_count = result.PortalPairCount
            };
        }
    }
}
