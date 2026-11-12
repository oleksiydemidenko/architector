namespace Cysharp.Threading.Tasks
{
    public struct UniTask
    {
        public static UniTask Yield() => default;
        public static UniTask NextFrame() => default;
        public static UniTask WaitUntil(System.Func<bool> predicate) => default;
    }
}

namespace Lab.Stubs
{
    public static class Tweens
    {
        public static void Schedule(System.Action step) { }
    }
}
