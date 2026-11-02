using System;

namespace Architector.Editor
{
    [Serializable]
    public sealed class ArchitectorIntegrationUpdate
    {
        public string installedVersion;
        public string availableVersion;
        public bool compatible = true;
        public string details;
        public string releaseNotes;

        public bool Available => compatible
            && ArchitectorUpdateVersion.TryParse(installedVersion, out var installed)
            && ArchitectorUpdateVersion.TryParse(availableVersion, out var available)
            && available.CompareTo(installed) > 0;
    }
}
