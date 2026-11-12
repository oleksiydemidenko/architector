using System.Collections;
using Cysharp.Threading.Tasks;
using Lab.Stubs;

namespace Lab.Game
{
    public sealed class Poller : UnityEngine.MonoBehaviour
    {
        private bool ready;

        private void Start()
        {
            UniTask.WaitUntil(() => ready);
            Tweens.Schedule(() => { });
            UniTask.Yield();
        }

        private void Loop()
        {
            while (!ready)
                UniTask.Yield();
        }

        private IEnumerator Run()
        {
            while (true)
                yield return null;
        }
    }
}
