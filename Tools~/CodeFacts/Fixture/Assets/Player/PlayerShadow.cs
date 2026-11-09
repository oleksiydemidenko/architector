using Sample.Core;
using UnityEngine;

namespace Sample.Player
{
    // The base class is invisible to the analysis here, as engine classes are without Unity's project files. Clock below
    // is meant as a member inherited from it; it must not count as a reference to the project type Sample.Core.Clock.
    public sealed class PlayerShadow : MonoBehaviour
    {
        public object Read() => Clock;
    }
}

namespace Sample.Player
{
    public sealed class PlayerSignals
    {
        public event System.Action Changed;
        public System.Action<int> Scored;
        public int Score;
    }

    // Behaviour facts: subscribes in OnEnable without an OnDisable, assigns another type's field, runs every frame.
    public sealed class PlayerListener : UnityEngine.MonoBehaviour
    {
        private readonly PlayerSignals signals = new PlayerSignals();

        private void OnEnable()
        {
            signals.Changed += Refresh;
            signals.Scored += OnScored;
        }

        private void Update()
        {
            signals.Score = 1;
        }

        private void Refresh()
        {
        }

        private void OnScored(int value)
        {
        }
    }
}
