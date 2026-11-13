using Lab.Core;

namespace Lab.Game
{
    // Only a prefab variant stores it, through a modification whose property path Unity wrote in quotes.
    [System.Serializable]
    public sealed class QuotedInstaller : IInstaller
    {
        public void Install(Container container) { }
    }
}
