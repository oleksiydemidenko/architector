namespace Lab.Game
{
    public interface ITicker
    {
        void Update();
    }

    [System.Serializable]
    public sealed class Ticker : ITicker
    {
        public void Update() { }
    }
}
