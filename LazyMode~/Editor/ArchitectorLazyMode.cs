using UnityEditor;

namespace Architector.LazyMode.Editor
{
    [InitializeOnLoad]
    internal static class ArchitectorLazyMode
    {
        static ArchitectorLazyMode()
        {
            Architector.Editor.ArchitectorAgentWorkflowModes.Register(new Architector.Editor.ArchitectorAgentWorkflowMode(
                "lazy",
                "Lazy",
                "Lazy: the agent writes and approves layers on its own. You do not need to understand the architecture in this mode. Architector is less effective without your control.",
                "The user does not review the architecture in this mode. Design, implement and approve layers in unlocked groups on your own, and keep the graph in step with the code.",
                agentMayApprove: true,
                showsNotice: true));
        }
    }
}
