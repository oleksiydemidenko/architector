using System.Collections.Generic;
using Lab.Core;

namespace Lab.Game
{
    public sealed class InstallerList : UnityEngine.MonoBehaviour
    {
        [UnityEngine.SerializeReference] private List<IInstaller> installers = new List<IInstaller>();
        public ValueInstaller fallback;
    }

    [System.Serializable]
    public sealed class InputInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [System.Serializable]
    public sealed class AudioInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [System.Serializable]
    public sealed class UnusedInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [System.Serializable]
    public sealed class ValueInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [System.Serializable]
    public sealed class NewInstaller : IInstaller
    {
        public void Install(Container container) { }

        public static IInstaller Make() => new NewInstaller();
    }
}
