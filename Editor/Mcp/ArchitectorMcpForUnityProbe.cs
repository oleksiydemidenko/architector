using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Windows;

namespace Architector.Editor.Mcp
{
    public sealed class ArchitectorMcpForUnityProbe : IArchitectorAgentIntegrationProbe
    {
        public const string Id = "coplay-unity-mcp";

        public ArchitectorAgentIntegrationDescriptor Descriptor => ArchitectorAgentIntegrationRegistry.CoplayUnityMcp;

        public ArchitectorAgentIntegrationProbeResult Probe(string packageVersion)
        {
            var bridge = MCPServiceLocator.Bridge;
            return bridge.IsRunning
                ? new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Active,
                    packageVersion,
                    $"{bridge.ActiveMode} transport is running on port {bridge.CurrentPort}.",
                    bridge.CurrentPort)
                : new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Connected,
                    packageVersion,
                    "Package and Architector adapter are available; the MCP transport is not running.");
        }

        public void OpenSetup()
        {
            MCPForUnityEditorWindow.ShowWindow();
        }
    }
}
