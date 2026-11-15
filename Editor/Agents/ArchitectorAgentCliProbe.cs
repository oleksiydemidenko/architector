namespace Architector.Editor.Agents
{
    // The command line of the package binary (architector-mcp query|edit|status ...). It shares the Editor bridge with
    // the MCP server, but the bridge answers it only while this integration is on.
    public sealed class ArchitectorAgentCliProbe : IArchitectorAgentIntegrationProbe
    {
        public ArchitectorAgentIntegrationDescriptor Descriptor { get; } = new()
        {
            id = ArchitectorAgentIntegrationRegistry.CliId,
            displayName = ArchitectorAgentIntegrationRegistry.CliName,
            packageName = "com.oleksiydemidenko.architector",
            documentationUrl = "https://github.com/oleksiydemidenko/architector/blob/main/Documentation~/AgentSetup.md",
            tags = new[] { "CLI" },
            builtIn = true
        };

        public ArchitectorAgentIntegrationProbeResult Probe(string packageVersion)
        {
            return ArchitectorAgentBridge.Port > 0
                ? new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Active,
                    packageVersion,
                    $"The command line reaches the Editor through the bridge on port {ArchitectorAgentBridge.Port}.",
                    ArchitectorAgentBridge.Port)
                : new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Connected,
                    packageVersion,
                    "The Editor bridge is not running.");
        }

        public void OpenSetup()
        {
            ArchitectorIntegrationsWindow.Open();
        }
    }
}
