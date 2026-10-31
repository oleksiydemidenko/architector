using System.ComponentModel;
using Architector.Editor.Agents;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;

namespace Architector.Editor.Mcp.Murzak
{
    [AiToolType]
    public sealed class ArchitectorMurzakTools
    {
        [AiTool("architector-query", Title = "Architector query", ReadOnlyHint = true)]
        [Description("Read compact, validated Architector project, group, layer, approval, conformance, type search, AI work status and contract data. Actions: capabilities, work_status, project_summary, group_contents, layer_graph, approval_status, baseline_diff, conformance, find, type_search, type_contract, validate.")]
        public string Query(
            [Description("JSON object with action and its parameters, for example {\"action\":\"layer_graph\",\"target\":\"Core/Player\"}. Call capabilities for the contract.")]
            string parameters)
        {
            return MainThread.Instance.Run(() => ArchitectorAgentApi.QueryJson(parameters, ArchitectorMurzakProbe.Id));
        }

        [AiTool("architector-edit", Title = "Architector edit")]
        [Description("Manage an AI work session, approval, or dry-run/apply canonical documents and compact semantic plans with revision checks and one Undo operation. Actions: begin_work, resume_work, end_work, approve_layers, revoke_approvals, revert_to_approved, dry_run, apply, dry_run_plan, apply_plan, validate_external, reload_external.")]
        public string Edit(
            [Description("JSON object with action and its parameters, for example {\"action\":\"begin_work\",\"expected_revision\":1,\"work_label\":\"Add a layer\"}.")]
            string parameters)
        {
            return MainThread.Instance.Run(() => ArchitectorAgentApi.EditJson(parameters, ArchitectorMurzakProbe.Id));
        }
    }
}
