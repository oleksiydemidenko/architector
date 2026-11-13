namespace Lab.Game
{
    // 0.3.10: a container call that adds a component on a new GameObject creates it.
    public sealed class PlatformService : UnityEngine.MonoBehaviour
    {
    }

    public sealed class ContainerBuilder
    {
        public void RegisterComponentOnNewGameObject<T>() { }
    }

    public static class PlatformInstaller
    {
        public static void Install(ContainerBuilder builder) => builder.RegisterComponentOnNewGameObject<PlatformService>();
    }

    // 0.3.10: member_ref names a generic method with its arity; an in parameter keeps its in: prefix.
    public sealed class Catalog
    {
        public int GetAll() => 0;
        public T GetAll<T>() => default;
        public float Distance(in Spot spot) => 0f;
    }

    public readonly struct Spot
    {
    }
}
