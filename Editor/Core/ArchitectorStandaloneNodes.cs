using System;
using System.Linq;

namespace Architector.Editor
{
    // A linked node marked as having no relationship on purpose: its missing relationship is a warning with the reason,
    // not an error, and does not block approval.
    public static class ArchitectorStandaloneNodes
    {
        public static string Reason(ArchitectorSettingsDocument settings, string layerId, string nodeId) =>
            settings?.rules?.standaloneNodes?.FirstOrDefault(item => item != null
                                                                     && string.Equals(item.layerId, layerId, StringComparison.Ordinal)
                                                                     && string.Equals(item.nodeId, nodeId, StringComparison.Ordinal))?.reason
            ?? string.Empty;
    }
}
