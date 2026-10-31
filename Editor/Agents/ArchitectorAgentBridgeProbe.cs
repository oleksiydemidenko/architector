namespace Architector.Editor.Agents
{
    public sealed class ArchitectorAgentBridgeProbe : IArchitectorAgentIntegrationProbe
    {
        public ArchitectorAgentIntegrationDescriptor Descriptor { get; } = new()
        {
            id = ArchitectorAgentBridge.IntegrationId,
            displayName = "Architector MCP server",
            packageName = "com.oleksiydemidenko.architector",
            documentationUrl = "https://github.com/oleksiydemidenko/architector/blob/main/Documentation~/AgentSetup.md"
        };

        public ArchitectorAgentIntegrationProbeResult Probe(string packageVersion)
        {
            return ArchitectorAgentBridge.Port > 0
                ? new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Active,
                    packageVersion,
                    $"Local bridge is listening on port {ArchitectorAgentBridge.Port}.",
                    ArchitectorAgentBridge.Port)
                : new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Connected,
                    packageVersion,
                    "The local bridge is not running.");
        }

        public void OpenSetup()
        {
            ArchitectorMcpSetupWindow.Open();
        }
    }
}
