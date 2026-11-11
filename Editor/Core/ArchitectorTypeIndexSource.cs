namespace Architector.Editor
{
    // A source file in the index scope, with what only the main thread can read about it.
    internal readonly struct ArchitectorTypeIndexSource
    {
        public ArchitectorTypeIndexSource(string assemblyName, string path, string guid)
        {
            AssemblyName = assemblyName;
            Path = path;
            Guid = guid;
        }

        public string AssemblyName { get; }
        public string Path { get; }
        public string Guid { get; }
    }
}
