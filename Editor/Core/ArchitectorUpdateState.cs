using System;

namespace Architector.Editor
{
    [Serializable]
    public sealed class ArchitectorUpdateState
    {
        public string installedVersion;
        public string unityVersion;
        public string serverAsset;
        public bool includePrereleases;
        public string checkedUtc;
        public string latestTag;
        public string availableTag;
        public string releaseNotes;
        public string notice;
        public string error;
        public string pendingVersion;
    }
}
