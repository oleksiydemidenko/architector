using System;
using Lab.Core;

namespace Lab.Game
{
    // A generic type that creates its type argument: constructing it, or deriving from it, creates the argument.
    public class InstallerPool<T> where T : IInstaller, new()
    {
        public T Take() => new T();
    }

    public sealed class PooledInstallerHost
    {
        private readonly InstallerPool<PooledInstaller> pool = new InstallerPool<PooledInstaller>();
    }

    public sealed class InheritedInstallerPool : InstallerPool<InheritedInstaller>
    {
    }

    [Serializable]
    public sealed class PooledInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [Serializable]
    public sealed class InheritedInstaller : IInstaller
    {
        public void Install(Container container) { }
    }
}

namespace Lab.Game
{
    // A type that passes itself to a creating base or method creates nothing: SelfInstaller stays unwired.
    public abstract class SelfCloning<TSelf> where TSelf : SelfCloning<TSelf>, new()
    {
        public TSelf Clone() => new TSelf();
    }

    [System.Serializable]
    public sealed class SelfInstaller : SelfCloning<SelfInstaller>, IInstaller
    {
        static SelfInstaller() => InstallerFactory.Create<SelfInstaller>();

        public void Install(Container container) { }
    }
}
