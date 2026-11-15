namespace Rules
{
    public enum Phase { Pre, Post }

    public class Ticker
    {
        public void Subscribe(System.Action action, Phase phase) { }

        public void Unsubscribe(System.Action action, Phase phase) { }
    }
}
