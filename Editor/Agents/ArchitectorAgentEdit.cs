using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Architector.Editor.Agents
{
    public static class ArchitectorAgentEdit
    {
        private sealed class Parameters
        {
            public string action { get; set; }

            public int? expected_revision { get; set; }

            public string action_name { get; set; }

            public object documents { get; set; }

            public object plan { get; set; }

            public string work_token { get; set; }

            public string work_label { get; set; }

            public bool approve_changes { get; set; }

            public string[] layers { get; set; }
        }

        public static ArchitectorAgentResult Handle(
            ArchitectorRepository repository,
            ArchitectorAiWorkCoordinator aiWork,
            JObject @params,
            ArchitectorTypeIndex typeIndex = null)
        {
            if (repository == null)
                return ArchitectorAgentResult.Fail("repository_unavailable");
            if (aiWork == null)
                return ArchitectorAgentResult.Fail("ai_work_coordinator_unavailable");
            if (@params == null)
                return ArchitectorAgentResult.Fail("invalid_parameters", new { message = "Parameters cannot be null." });

            try
            {
                var parameters = @params.ToObject<Parameters>() ?? new Parameters();
                var action = parameters.action?.Trim().ToLowerInvariant();
                if (parameters.approve_changes && action != "approve_layers")
                    return ArchitectorAgentResult.Fail("approval_is_separate", new
                    {
                        message = "Save the draft with apply/apply_plan without approve_changes, end_work for user review, then use approve_layers separately."
                    });
                if (action == "begin_work")
                {
                    var stale = CheckRevision(repository, parameters.expected_revision, true);
                    if (stale != null)
                        return stale;
                    var token = aiWork.Begin(parameters.work_label, repository.Revision, ExistingEntries(repository));
                    return WorkSessionSuccess("AI work session started; Architector windows are locked.", repository, aiWork, token);
                }
                if (action == "end_work")
                {
                    aiWork.End(parameters.work_token);
                    return WorkSessionSuccess("AI work session ended; Architector windows are unlocked.", repository, aiWork, string.Empty);
                }
                if (action == "resume_work")
                {
                    aiWork.Resume(parameters.work_token);
                    return WorkSessionSuccess("AI work session resumed after script compilation.", repository, aiWork, string.Empty);
                }
                if (action == "revert_to_approved")
                {
                    var stale = CheckRevision(repository, parameters.expected_revision, true);
                    if (stale != null)
                        return stale;
                    aiWork.RequireMutationToken(parameters.work_token);
                    if (@params["layers"] is not JArray revertArray || revertArray.Count == 0
                        || revertArray.Any(item => item.Type != JTokenType.String))
                        return ArchitectorAgentResult.Fail("layers_required", new { message = "layers must be a non-empty array of layer IDs." });
                    var revertIds = revertArray.Values<string>().ToArray();
                    var revertAllowed = RequireApprovalAuthority(repository, action, revertIds);
                    if (revertAllowed != null)
                        return revertAllowed;
                    foreach (var layerId in revertIds)
                        repository.RevertLayerToApproved(layerId);
                    return ArchitectorAgentResult.Ok("Architector layers reverted to their approved baselines.", new
                    {
                        revision = repository.Revision,
                        layers = revertIds,
                        can_undo = repository.CanUndo,
                        next_undo = repository.NextUndoName
                    });
                }
                if (action == "approve_layers" || action == "revoke_approvals")
                {
                    var stale = CheckRevision(repository, parameters.expected_revision, true);
                    if (stale != null)
                        return stale;
                    aiWork.RequireMutationToken(parameters.work_token);
                    if (@params["layers"] is not JArray layerArray || layerArray.Count == 0
                        || layerArray.Any(item => item.Type != JTokenType.String))
                        return ArchitectorAgentResult.Fail("layers_required", new { message = "layers must be a non-empty array of layer IDs." });
                    var layerIds = layerArray.Values<string>().ToArray();
                    if (action == "approve_layers" && !parameters.approve_changes)
                        return ArchitectorAgentResult.Fail("approval_required", new
                        {
                            message = "Approval marks agreed canonical architecture. Pass approve_changes=true to request it.",
                            revision = repository.Revision,
                            layers = layerIds
                        });
                    var allowed = RequireApprovalAuthority(repository, action, layerIds);
                    if (allowed != null)
                        return allowed;
                    if (action == "approve_layers")
                        repository.ApproveLayers(layerIds, ArchitectorAgentPreferences.Current.Mode.AgentMayApprove);
                    else
                        repository.RevokeLayerApprovals(layerIds);
                    return ArchitectorAgentResult.Ok(
                        action == "approve_layers" ? "Architector layers approved." : "Architector layer approvals revoked.", new
                    {
                        revision = repository.Revision,
                        layers = layerIds,
                        can_undo = repository.CanUndo,
                        next_undo = repository.NextUndoName
                    });
                }
                if (action == "arrange_layer")
                {
                    var stale = CheckRevision(repository, parameters.expected_revision, true);
                    if (stale != null)
                        return stale;
                    aiWork.RequireMutationToken(parameters.work_token);
                    var layer = ArchitectorAgentQuery.ResolveLayer(repository, @params["target"]?.Value<string>());
                    if (layer == null)
                        return ArchitectorAgentQuery.LayerFailure(repository, @params["target"]?.Value<string>());
                    var nodes = @params["nodes"]?.Values<string>().ToArray();
                    var centers = @params["centers"]?.Values<string>().ToArray();
                    repository.ArrangeNodes(layer.id, nodes, centers);
                    return ArchitectorAgentResult.Ok("Architector layer arranged.", new { revision = repository.Revision, layer = layer.id });
                }
                if (action == "arrange_changed_layers")
                {
                    var stale = CheckRevision(repository, parameters.expected_revision, true);
                    if (stale != null)
                        return stale;
                    if (!aiWork.IsActive)
                        return ArchitectorAgentResult.Fail("work_session_required", new
                        {
                            message = "arrange_changed_layers arranges what this work session added; call it with the work_token before end_work."
                        });
                    aiWork.RequireMutationToken(parameters.work_token);
                    var (arranged, skipped) = ArrangeChangedLayers(repository, aiWork.KnownEntries);
                    return ArchitectorAgentResult.Ok("Architector layers changed in this work session arranged.", new
                    {
                        revision = repository.Revision,
                        arranged,
                        skipped
                    });
                }
                if (action == "relink_node")
                {
                    var stale = CheckRevision(repository, parameters.expected_revision, true);
                    if (stale != null)
                        return stale;
                    aiWork.RequireMutationToken(parameters.work_token);
                    var layer = ArchitectorAgentQuery.ResolveLayer(repository, @params["target"]?.Value<string>());
                    if (layer == null)
                        return ArchitectorAgentQuery.LayerFailure(repository, @params["target"]?.Value<string>());
                    var nodeId = @params["node"]?.Value<string>();
                    var typeKey = @params["type_key"]?.Value<string>();
                    var ownsRelinkIndex = typeIndex == null;
                    typeIndex ??= new ArchitectorTypeIndex(repository);
                    try
                    {
                        var indexed = typeIndex.Types.FirstOrDefault(type => string.Equals(type.Key, typeKey, StringComparison.Ordinal));
                        if (indexed == null)
                            return ArchitectorAgentResult.Fail("type_not_found", new { type_key = typeKey });
                        var members = typeIndex.GetMembers(new ArchitectorTypeRecord { mode = "linked", assemblyName = indexed.AssemblyName, metadataName = indexed.MetadataName });
                        var dropped = repository.RelinkNode(layer.id, nodeId, indexed, members.ToArray());
                        return ArchitectorAgentResult.Ok("Architector node relinked.", new { revision = repository.Revision, dropped_members = dropped });
                    }
                    finally
                    {
                        if (ownsRelinkIndex)
                            typeIndex.Dispose();
                    }
                }
                if (action == "validate_external")
                    return Success("External Architector documents are valid.", repository, repository.ValidateExternalChanges());
                if (action == "reload_external")
                {
                    var stale = CheckRevision(repository, parameters.expected_revision, true);
                    if (stale != null)
                        return stale;
                    aiWork.RequireMutationToken(parameters.work_token);
                    return Success("External Architector documents reloaded; session Undo/Redo was cleared.", repository, repository.ReloadExternalChanges());
                }
                if (action == "dry_run_plan" || action == "apply_plan")
                {
                    var apply = action == "apply_plan";
                    var stale = CheckRevision(repository, parameters.expected_revision, apply);
                    if (stale != null)
                        return stale;
                    if (apply)
                        aiWork.RequireMutationToken(parameters.work_token);
                    if (@params["plan"] is not JObject planObject)
                        return ArchitectorAgentResult.Fail("plan_required");
                    if (planObject.Count == 1 && planObject["plan_token"]?.Type == JTokenType.String)
                    {
                        var token = planObject["plan_token"].Value<string>();
                        if (!DryRunPlans.TryGetValue(token, out var cached))
                            return ArchitectorAgentResult.Fail("plan_token_unknown", new
                            {
                                message = "The plan token is unknown or expired (for example after script compilation); send the full plan again.",
                                revision = repository.Revision
                            });
                        if (cached.revision != repository.Revision)
                            return ArchitectorAgentResult.Fail("plan_token_stale", new
                            {
                                message = "The architecture changed after this dry run; run dry_run_plan again.",
                                token_revision = cached.revision,
                                revision = repository.Revision
                            });
                        planObject = cached.plan;
                    }
                    var plan = planObject.ToObject<ArchitectorSemanticPlan>();
                    var ownsTypeIndex = typeIndex == null;
                    typeIndex ??= new ArchitectorTypeIndex(repository);
                    try
                    {
                        var planResult = apply
                            ? repository.ApplySemanticPlan(plan, parameters.action_name, typeIndex)
                            : repository.DryRunSemanticPlan(plan, parameters.action_name, typeIndex);
                        if (apply)
                            return Success("Architector semantic plan applied.", repository, planResult);
                        var planToken = RememberDryRunPlan(planObject, repository.Revision);
                        return Success(
                            $"Architector semantic plan is valid; no files were written. Apply it with plan={{\"plan_token\": \"{planToken}\"}} instead of resending it.",
                            repository,
                            planResult,
                            planToken);
                    }
                    finally
                    {
                        if (ownsTypeIndex)
                            typeIndex.Dispose();
                    }
                }
                if (action != "dry_run" && action != "apply")
                {
                    return ArchitectorAgentResult.Fail("unsupported_action", new
                    {
                        message = "Valid actions: begin_work, resume_work, end_work, approve_layers, revoke_approvals, revert_to_approved, dry_run, apply, dry_run_plan, apply_plan, arrange_layer, arrange_changed_layers, relink_node, validate_external, reload_external."
                    });
                }

                var revisionError = CheckRevision(repository, parameters.expected_revision, action == "apply");
                if (revisionError != null)
                    return revisionError;
                if (action == "apply")
                    aiWork.RequireMutationToken(parameters.work_token);
                if (@params["documents"] is not JObject documentObject || !documentObject.HasValues)
                    return ArchitectorAgentResult.Fail("documents_required");

                var batch = new ArchitectorCanonicalBatch(parameters.action_name);
                foreach (var property in documentObject.Properties())
                {
                    if (property.Value.Type == JTokenType.Null)
                        batch.DeleteDocument(property.Name);
                    else if (property.Value.Type == JTokenType.String)
                        batch.SetDocument(property.Name, property.Value.Value<string>());
                    else
                        return ArchitectorAgentResult.Fail("invalid_document", new
                        {
                            path = property.Name,
                            message = "Document values must be JSON strings or null."
                        });
                }

                var result = action == "dry_run"
                    ? repository.DryRunCanonicalBatch(batch)
                    : repository.ApplyCanonicalBatch(batch);
                return Success(action == "dry_run" ? "Architector batch is valid; no files were written." : "Architector batch applied.", repository, result);
            }
            catch (ArchitectorDataException exception)
            {
                return ArchitectorAgentResult.Fail("validation_failed", new
                {
                    message = exception.Message,
                    operation = exception.Data[ArchitectorRepository.PlanOperationKey] as string,
                    revision = repository.Revision
                });
            }
            catch (Exception exception)
            {
                return ArchitectorAgentResult.Fail("edit_failed", new { message = exception.Message, revision = repository.Revision });
            }
        }

        private const int MaxDryRunPlans = 8;
        private static readonly Dictionary<string, (JObject plan, int revision)> DryRunPlans = new(StringComparer.Ordinal);
        private static readonly Queue<string> DryRunPlanOrder = new();

        private static string RememberDryRunPlan(JObject plan, int revision)
        {
            var text = plan.ToString(Newtonsoft.Json.Formatting.None);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{revision}\n{text}"));
            var token = "plan_" + string.Concat(hash.Take(8).Select(value => value.ToString("x2")));
            if (!DryRunPlans.ContainsKey(token))
            {
                DryRunPlanOrder.Enqueue(token);
                while (DryRunPlanOrder.Count > MaxDryRunPlans)
                    DryRunPlans.Remove(DryRunPlanOrder.Dequeue());
            }
            DryRunPlans[token] = ((JObject)plan.DeepClone(), revision);
            return token;
        }

        private static ArchitectorAgentResult RequireApprovalAuthority(ArchitectorRepository repository, string action, IReadOnlyList<string> layerIds)
        {
            if (ArchitectorAgentPreferences.Current.Mode.AgentMayApprove)
                return null;
            var names = layerIds.Select(id => repository.FindLayer(id)?.name ?? id);
            var question = action switch
            {
                "approve_layers" => "approve",
                "revoke_approvals" => "revoke the approval of",
                _ => "revert to the approved version"
            };
            if (ArchitectorApprovalGate.Confirm($"An agent wants to {question}:\n\n{string.Join("\n", names)}"))
                return null;
            return ArchitectorAgentResult.Fail("user_refused", new
            {
                message = "The user did not confirm this action in the Unity Editor. Do not repeat it unless the user asks.",
                revision = repository.Revision,
                layers = layerIds
            });
        }

        private static IEnumerable<string> ExistingEntries(ArchitectorRepository repository)
        {
            foreach (var layer in repository.Project.layers)
            {
                yield return layer.id + "/";
                if (repository.HasLayerLoadError(layer.id))
                    continue;
                foreach (var node in repository.LoadLayer(layer.id).semantic.nodes)
                    yield return layer.id + "/" + node.id;
            }
        }

        private static (List<object> arranged, List<object> skipped) ArrangeChangedLayers(
            ArchitectorRepository repository,
            IReadOnlyCollection<string> knownEntries)
        {
            var known = new HashSet<string>(knownEntries, StringComparer.Ordinal);
            var arranged = new List<object>();
            var skipped = new List<object>();
            foreach (var layer in repository.Project.layers.ToArray())
            {
                if (repository.HasLayerLoadError(layer.id))
                    continue;
                var newLayer = !known.Contains(layer.id + "/");
                var added = repository.LoadLayer(layer.id).semantic.nodes
                    .Where(node => !known.Contains(layer.id + "/" + node.id))
                    .Select(node => node.id)
                    .ToArray();
                if (!newLayer && added.Length == 0)
                    continue;
                try
                {
                    if (newLayer)
                        repository.ArrangeNodes(layer.id);
                    else
                        PlaceNearNeighbors(repository, layer.id, added);
                    arranged.Add(new { layer = layer.id, name = layer.name, whole_layer = newLayer, nodes = added.Length });
                }
                catch (ArchitectorDataException exception)
                {
                    skipped.Add(new { layer = layer.id, name = layer.name, reason = exception.Message });
                }
            }
            return (arranged, skipped);
        }

        private static void PlaceNearNeighbors(ArchitectorRepository repository, string layerId, IReadOnlyCollection<string> added)
        {
            foreach (var nodeId in added)
            {
                var view = repository.LoadLayerView(layerId);
                var layouts = view.layout.nodes.ToDictionary(node => node.nodeId, StringComparer.Ordinal);
                if (!layouts.TryGetValue(nodeId, out var own))
                    continue;
                var neighbors = view.semantic.edges
                    .Where(edge => edge.sourceNodeId == nodeId || edge.targetNodeId == nodeId)
                    .Select(edge => edge.sourceNodeId == nodeId ? edge.targetNodeId : edge.sourceNodeId)
                    .Where(id => id != nodeId && layouts.ContainsKey(id) && !added.Contains(id))
                    .Distinct(StringComparer.Ordinal)
                    .Select(id => layouts[id])
                    .ToArray();
                if (neighbors.Length == 0)
                    continue;
                var x = neighbors.Average(node => node.x + node.width * 0.5f) - own.width * 0.5f;
                var y = neighbors.Average(node => node.y + node.height * 0.5f) - own.height * 0.5f;
                repository.MoveNode(layerId, nodeId, x, y);
            }
        }

        private static ArchitectorAgentResult CheckRevision(ArchitectorRepository repository, int? expectedRevision, bool required)
        {
            if (required && !expectedRevision.HasValue)
                return ArchitectorAgentResult.Fail("expected_revision_required", new { current_revision = repository.Revision });
            if (expectedRevision.HasValue && expectedRevision.Value != repository.Revision)
            {
                return ArchitectorAgentResult.Fail("stale_revision", new
                {
                    expected_revision = expectedRevision.Value,
                    current_revision = repository.Revision
                });
            }
            return null;
        }

        private static ArchitectorAgentResult Success(
            string message,
            ArchitectorRepository repository,
            ArchitectorCanonicalBatchResult result)
        {
            return ArchitectorAgentResult.Ok(message, new
            {
                revision = repository.Revision,
                changed_file_count = result.ChangedFileCount,
                validated_file_count = result.ValidatedFileCount,
                layer_count = result.LayerCount,
                portal_pair_count = result.PortalPairCount,
                approval_impacts = ApprovalImpacts(result.ApprovalImpacts),
                can_undo = repository.CanUndo,
                next_undo = repository.NextUndoName
            });
        }

        private static ArchitectorAgentResult WorkSessionSuccess(
            string message,
            ArchitectorRepository repository,
            ArchitectorAiWorkCoordinator aiWork,
            string token)
        {
            return ArchitectorAgentResult.Ok(message, new
            {
                revision = repository.Revision,
                active = aiWork.IsActive,
                work_token = string.IsNullOrEmpty(token) ? null : token,
                label = aiWork.Label,
                workspaces_locked = aiWork.WorkspacesLocked,
                settings_locked = aiWork.SettingsLocked,
                workspaces_user_override = aiWork.WorkspacesUnlockedByUser,
                settings_user_override = aiWork.SettingsUnlockedByUser,
                awaiting_agent_resume = aiWork.IsAwaitingAgentResume,
                started_revision = aiWork.StartedRevision
            });
        }

        private static ArchitectorAgentResult Success(
            string message,
            ArchitectorRepository repository,
            ArchitectorSemanticPlanResult result,
            string planToken = null)
        {
            return ArchitectorAgentResult.Ok(message, new
            {
                revision = repository.Revision,
                plan_token = planToken,
                changed_file_count = result.Batch.ChangedFileCount,
                validated_file_count = result.Batch.ValidatedFileCount,
                layer_count = result.Batch.LayerCount,
                portal_pair_count = result.Batch.PortalPairCount,
                approval_impacts = ApprovalImpacts(result.Batch.ApprovalImpacts),
                references = result.References,
                warnings = result.Warnings.Count == 0 ? null : result.Warnings,
                can_undo = repository.CanUndo,
                next_undo = repository.NextUndoName
            });
        }

        private static object[] ApprovalImpacts(System.Collections.Generic.IEnumerable<ArchitectorApprovalImpact> impacts)
        {
            return (impacts ?? Array.Empty<ArchitectorApprovalImpact>()).Select(impact => (object)new
            {
                layer_id = impact.LayerId,
                layer_name = impact.LayerName,
                change = impact.Change,
                state = impact.State
            }).ToArray();
        }

    }
}
