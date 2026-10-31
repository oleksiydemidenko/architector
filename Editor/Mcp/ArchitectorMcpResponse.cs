using Architector.Editor.Agents;
using MCPForUnity.Editor.Helpers;

namespace Architector.Editor.Mcp
{
    internal static class ArchitectorMcpResponse
    {
        public static object From(ArchitectorAgentResult result)
        {
            return result.Success
                ? new SuccessResponse(result.Message, result.Data)
                : new ErrorResponse(result.Code, result.Data);
        }
    }
}
