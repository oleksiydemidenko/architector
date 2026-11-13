using Lab.Core;

namespace Lab.Game
{
    // Second review: a static member reached through a qualified name, a singleton field of the type itself, and a static
    // void method that keeps what it creates. All three types are created by code.
    public static class ReviewCreations2
    {
        public static void Build()
        {
            Lab.Game.StaticCache<QualifiedInstaller>.Make();
            var instance = SingletonInstaller.Instance;
            InitInstaller.Init();
        }
    }

    [System.Serializable] public sealed class QualifiedInstaller : IInstaller { public void Install(Container container) { } }

    [System.Serializable]
    public sealed class SingletonInstaller : IInstaller
    {
        public static readonly SingletonInstaller Instance = InstallerFactory.Create<SingletonInstaller>();

        public void Install(Container container) { }
    }

    [System.Serializable]
    public sealed class InitInstaller : IInstaller
    {
        private static InitInstaller current;

        public static void Init() { current = InstallerFactory.Create<InitInstaller>(); }

        public void Install(Container container) { }
    }
}
