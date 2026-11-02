namespace Architector.Editor
{
    public sealed class ArchitectorIntegrationUpdateState
    {
        public ArchitectorIntegrationUpdate Update { get; internal set; }
        public bool Checking { get; internal set; }
        public bool Installing { get; internal set; }
        public string Error { get; internal set; }
        public string Notice { get; internal set; }
    }
}
