using System;
using System.Collections.Generic;

namespace Architector.Editor
{
    internal sealed class ArchitectorTypeIndexBuildResult
    {
        public ArchitectorTypeIndexBuildResult(IReadOnlyList<ArchitectorIndexedType> types, Dictionary<string, Type> loadedTypes)
        {
            Types = types;
            LoadedTypes = loadedTypes;
        }

        public IReadOnlyList<ArchitectorIndexedType> Types { get; }
        public Dictionary<string, Type> LoadedTypes { get; }
    }
}
