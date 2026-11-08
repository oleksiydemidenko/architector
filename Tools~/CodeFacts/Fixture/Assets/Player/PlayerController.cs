using Sample.Core;
using UnityEngine;

namespace Sample.Player
{
    public sealed class PlayerController : MonoBehaviour, ITickable
    {
        private readonly Clock clock = new Clock();

        public PlayerInventory Inventory { get; } = new PlayerInventory();

        public void Tick(Clock source)
        {
            clock.Advance(source.Now);
            Inventory.Refresh();
        }
    }
}
