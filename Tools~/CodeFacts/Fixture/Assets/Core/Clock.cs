namespace Sample.Core
{
    public sealed class Clock
    {
        public float Now { get; private set; }

        public void Advance(float delta)
        {
            Now += delta;
        }
    }

    public interface ITickable
    {
        void Tick(Clock clock);
    }
}
