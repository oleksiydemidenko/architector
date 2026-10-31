using Architector.Editor.Agents;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Architector.Editor.Mcp
{
    [McpForUnityTool(
        "architector_query",
        Description = "Read compact, validated Architector project, group, layer, approval, conformance, type search, AI work status and contract data. Actions: capabilities, work_status, project_summary, group_contents, layer_graph, approval_status, baseline_diff, conformance, find, type_search, type_contract, validate.",
        AutoRegister = true,
        Group = "core")]
    public static class ArchitectorMcpQueryTool
    {
        public sealed class Parameters
        {
            [ToolParameter("Query action: capabilities, work_status, project_summary, group_contents, layer_graph, approval_status, conformance, find, type_search, type_contract, or validate.")]
            public string action { get; set; }

            [ToolParameter("Group/layer ID or name, or indexed type key/name for type_contract.", Required = false)]
            public string target { get; set; }

            [ToolParameter("Case-insensitive text for find or type_search, or a member filter for type_contract.", Required = false)]
            public string query { get; set; }

            [ToolParameter("Maximum find/type results or conformance diagnostics; type_contract clamps to 500.", Required = false, DefaultValue = "20")]
            public int? limit { get; set; }

            [ToolParameter("Include node rectangles in layer_graph.", Required = false, DefaultValue = "false")]
            public bool? include_layout { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return ArchitectorMcpResponse.From(ArchitectorAgentApi.Query(@params, ArchitectorMcpForUnityProbe.Id));
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
            return ArchitectorMcpResponse.From(ArchitectorAgentQuery.Handle(repository, aiWork, @params, typeIndex));
        }
    }
}
