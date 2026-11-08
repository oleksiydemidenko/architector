namespace Sample.Core
{
    public sealed class TrackAttribute : System.Attribute
    {
    }

    // Repeats a project attribute and a project generic call ten times each, so code facts suggest them as rules.
    public sealed class Tracker
    {
        [Track] private Clock first;
        [Track] private Clock second;
        [Track] private Clock third;
        [Track] private Clock fourth;
        [Track] private Clock fifth;
        [Track] private Clock sixth;
        [Track] private Clock seventh;
        [Track] private Clock eighth;
        [Track] private Clock ninth;
        [Track] private Clock tenth;

        public T Fetch<T>()
        {
            return default;
        }

        public void Poll()
        {
            Fetch<Clock>();
            Fetch<Clock>();
            Fetch<Clock>();
            Fetch<Clock>();
            Fetch<Clock>();
            Fetch<Clock>();
            Fetch<Clock>();
            Fetch<Clock>();
            Fetch<Clock>();
            Fetch<Clock>();
        }
    }
}
