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
                    var token = aiWork.Begin(parameters.work_label, repository.Revision);
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
                        message = "Valid actions: begin_work, resume_work, end_work, approve_layers, revoke_approvals, revert_to_approved, dry_run, apply, dry_run_plan, apply_plan, validate_external, reload_external."
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
                return ArchitectorAgentResult.Fail("validation_failed", new { message = exception.Message, revision = repository.Revision });
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
