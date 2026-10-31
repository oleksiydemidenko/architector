using System;

namespace Architector.Editor
{
    [Flags]
    internal enum ArchitectorExplorerEntryStatus
    {
        None = 0,
        Error = 1,
        Changed = 2
    }
}
