namespace Sample.Core
{
    public sealed class InjectAttribute : System.Attribute
    {
    }

    public sealed class Wiring
    {
        [Inject] private ITickable tickable;

        public T Get<T>()
        {
            return default;
        }

        public void Start()
        {
            var clock = Get<Clock>();
            tickable.Tick(clock);
        }
    }
}
