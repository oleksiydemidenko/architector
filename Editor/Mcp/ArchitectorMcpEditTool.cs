using Architector.Editor.Agents;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Architector.Editor.Mcp
{
    [McpForUnityTool(
        "architector_edit",
        Description = "Manage an AI work session, approval, layout, or dry-run/apply canonical documents and compact semantic plans with revision checks and one Undo operation. Actions: begin_work, resume_work, end_work, approve_layers, revoke_approvals, revert_to_approved, dry_run, apply, dry_run_plan, apply_plan, arrange_layer, arrange_changed_layers, validate_external, reload_external.",
        AutoRegister = true,
        Group = "core")]
    public static class ArchitectorMcpEditTool
    {
        public sealed class Parameters
        {
            [ToolParameter("Edit action: begin_work, resume_work, end_work, approve_layers, revoke_approvals, dry_run, apply, dry_run_plan, apply_plan, arrange_layer, arrange_changed_layers, validate_external, or reload_external.")]
            public string action { get; set; }

            [ToolParameter("Expected repository revision. Required for apply, arrange_layer, arrange_changed_layers and reload_external.", Required = false)]
            public int? expected_revision { get; set; }

            [ToolParameter("Layer ID or Explorer path for arrange_layer.", Required = false)]
            public string target { get; set; }

            [ToolParameter("Node IDs to arrange; omit to arrange the whole layer.", Required = false)]
            public string[] nodes { get; set; }

            [ToolParameter("Node IDs to use as Arrange centers; omit to choose automatically.", Required = false)]
            public string[] centers { get; set; }

            [ToolParameter("Undo/Redo action name for apply.", Required = false)]
            public string action_name { get; set; }

            [ToolParameter("Object mapping canonical relative paths to JSON strings; null deletes a document.", Required = false)]
            public object documents { get; set; }

            [ToolParameter("Compact semantic plan with creation arrays plus renames, Explorer entryMoves, node moves, edgeChanges, and deletions.", Required = false)]
            public object plan { get; set; }

            [ToolParameter("Token returned by begin_work. Required for mutations while an AI work session is active.", Required = false)]
            public string work_token { get; set; }

            [ToolParameter("Short user-visible description for begin_work.", Required = false)]
            public string work_label { get; set; }

            [ToolParameter("Confirmation for approve_layers only: true after the user reviewed these exact layers. apply/apply_plan never approve.", Required = false)]
            public bool approve_changes { get; set; }

            [ToolParameter("Array of layer IDs for approve_layers or revoke_approvals.", Required = false)]
            public string[] layers { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return ArchitectorMcpResponse.From(ArchitectorAgentApi.Edit(@params, ArchitectorMcpForUnityProbe.Id));
        }

        public static object HandleCommand(ArchitectorRepository repository, JObject @params)
        {
            return HandleCommand(repository, new ArchitectorAiWorkCoordinator(), @params);
        }

        public static object HandleCommand(
            ArchitectorRepository repository,
            ArchitectorAiWorkCoordinator aiWork,
            JObject @params,
            ArchitectorTypeIndex typeIndex = null)
        {
            return ArchitectorMcpResponse.From(ArchitectorAgentEdit.Handle(repository, aiWork, @params, typeIndex));
        }
    }
}
