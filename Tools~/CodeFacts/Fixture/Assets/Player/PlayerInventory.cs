using Sample.Ui;

namespace Sample.Player
{
    public sealed class PlayerInventory
    {
        public void Refresh()
        {
            HudView.Show(this);
        }

        public sealed class Slot
        {
            public Sample.Core.Clock Stamp;
        }
    }
}
