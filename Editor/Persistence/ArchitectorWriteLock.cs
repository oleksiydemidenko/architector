using System;
using System.IO;
using System.Threading;

namespace Architector.Editor
{
    // One writer at a time for the Architector files of a project. The open Editor (its windows, Undo and agent bridge)
    // and every command line process take the same lock file, Library/Architector/write.lock, from the check that the
    // disk still matches what they read to the last written file; the command line holds it for a whole edit. The
    // operating system frees the lock when its process ends, also after a crash.
    public static class ArchitectorWriteLock
    {
        public const string FileName = "write.lock";

        // How long a second writer waits for the first. One Architector commit takes well under a second.
        public static int WaitMilliseconds = 15000;

        private const int RetryMilliseconds = 50;

        [ThreadStatic] private static int depth;
        [ThreadStatic] private static FileStream held;

        public static string LockPath(ArchitectorPaths paths) => Path.Combine(paths.UserStateRoot, FileName);

        public static bool IsHeldHere => depth > 0;

        public static T Run<T>(ArchitectorPaths paths, Func<T> change)
        {
            if (depth > 0)
            {
                depth++;
                try
                {
                    return change();
                }
                finally
                {
                    depth--;
                }
            }
            held = Acquire(LockPath(paths));
            depth = 1;
            try
            {
                return change();
            }
            finally
            {
                depth = 0;
                held.Dispose();
                held = null;
            }
        }

        public static void Run(ArchitectorPaths paths, Action change) => Run(paths, () =>
        {
            change();
            return true;
        });

        private static FileStream Acquire(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var waited = 0;
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (waited < WaitMilliseconds)
                {
                    Thread.Sleep(RetryMilliseconds);
                    waited += RetryMilliseconds;
                }
                catch (IOException)
                {
                    throw new ArchitectorWriteBusyException(
                        $"Another Architector writer holds {FileName} for more than {WaitMilliseconds / 1000.0:0.#} s (the Editor or another command line). Nothing was changed; repeat the edit.");
                }
            }
        }
    }

    public sealed class ArchitectorWriteBusyException : ArchitectorDataException
    {
        public ArchitectorWriteBusyException(string message) : base(message)
        {
        }
    }
}
