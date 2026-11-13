using System;

namespace Lab.Game
{
    // Two components with OnEnable and no OnDisable: one only resets a field, the other subscribes and never
    // unsubscribes. Only the second one is in unpaired_subscriptions.
    public sealed class ResetOnEnable : UnityEngine.MonoBehaviour
    {
        private int count;

        private void OnEnable()
        {
            count = 0;
        }
    }

    public sealed class LeakyListener : UnityEngine.MonoBehaviour
    {
        public static event Action Ticked;

        private void OnEnable()
        {
            Ticked += Handle;
        }

        private void Handle() { }
    }
}
