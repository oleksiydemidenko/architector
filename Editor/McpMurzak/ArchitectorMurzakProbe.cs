using UnityEngine;

namespace Architector.Editor.Mcp.Murzak
{
    public sealed class ArchitectorMurzakProbe : IArchitectorAgentIntegrationProbe
    {
        public const string Id = "ivanmurzak-unity-mcp";

        public ArchitectorAgentIntegrationDescriptor Descriptor => ArchitectorAgentIntegrationRegistry.IvanMurzakUnityMcp;

        public ArchitectorAgentIntegrationProbeResult Probe(string packageVersion)
        {
            return new ArchitectorAgentIntegrationProbeResult(
                ArchitectorAgentIntegrationStates.Connected,
                packageVersion,
                "Package and Architector adapter are available.");
        }

        public void OpenSetup()
        {
            Application.OpenURL(Descriptor.documentationUrl);
        }
    }
}
