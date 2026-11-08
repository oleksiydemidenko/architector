namespace Sample.Ui
{
    public sealed class ItemRegistry : Sample.Core.Registry<string>
    {
        public override void Add(string item)
        {
        }
    }

    public static class DebugMenu
    {
        [UnityEditor.MenuItem("Sample/Show Hud")]
        public static void ShowHud()
        {
        }
    }
}
