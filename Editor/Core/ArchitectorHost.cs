using System;
using System.Collections.Concurrent;
using System.IO;

namespace Architector.Editor
{
    // What the Architector core needs from the program that runs it. The Unity package is one host
    // (Editor/Unity/ArchitectorUnityHost: SessionState, EditorPrefs, a dialog, AssetDatabase, the Console); the command
    // line and the core tests are the other (ArchitectorProcessHost: values for this process only, nobody to ask).
    public interface IArchitectorHost
    {
        // The Unity project root: the folder that holds Assets and Architector.
        string ProjectRoot { get; }

        // Values that live for one Editor session and survive a script reload (Unity: SessionState).
        IArchitectorValueStore Session { get; }

        // Per-user values kept between sessions (Unity: EditorPrefs).
        IArchitectorValueStore Preferences { get; }

        // True when a person can answer a question now: the Editor with a window, not batch mode or a command line.
        bool CanAskPerson { get; }

        // Asks the person; false when nobody can answer.
        bool AskPerson(string title, string question, string allow, string refuse);

        // The asset GUID of a project file (Unity: AssetDatabase; elsewhere: the guid line of its .meta file).
        string AssetGuid(string projectRelativePath);

        void LogWarning(string message);
    }

    public interface IArchitectorValueStore
    {
        string GetString(string key, string fallback);
        void SetString(string key, string value);
        int GetInt(string key, int fallback);
        void SetInt(string key, int value);
        bool GetBool(string key, bool fallback);
        void SetBool(string key, bool value);
        void Erase(string key);
    }

    public static class ArchitectorHost
    {
#if UNITY_EDITOR
        private static IArchitectorHost current = new ArchitectorUnityHost();
#else
        private static IArchitectorHost current = new ArchitectorProcessHost(Directory.GetCurrentDirectory());
#endif

        public static IArchitectorHost Current
        {
            get => current;
            set => current = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    // A host without Unity: the values live in this process only, nobody can be asked, warnings go to stderr.
    public sealed class ArchitectorProcessHost : IArchitectorHost
    {
        public ArchitectorProcessHost(string projectRoot)
        {
            ProjectRoot = Path.GetFullPath(projectRoot);
        }

        public string ProjectRoot { get; }
        public IArchitectorValueStore Session { get; } = new ArchitectorMemoryValueStore();
        public IArchitectorValueStore Preferences { get; } = new ArchitectorMemoryValueStore();
        public bool CanAskPerson => false;

        public bool AskPerson(string title, string question, string allow, string refuse) => false;

        public string AssetGuid(string projectRelativePath)
        {
            try
            {
                var meta = Path.Combine(ProjectRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar)) + ".meta";
                if (!File.Exists(meta))
                    return string.Empty;
                foreach (var line in File.ReadLines(meta))
                {
                    if (line.StartsWith("guid:", StringComparison.Ordinal))
                        return line.Substring(5).Trim();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
            return string.Empty;
        }

        public void LogWarning(string message) => Console.Error.WriteLine(message);
    }

    public sealed class ArchitectorMemoryValueStore : IArchitectorValueStore
    {
        private readonly ConcurrentDictionary<string, object> values = new(StringComparer.Ordinal);

        public string GetString(string key, string fallback) => values.TryGetValue(key, out var value) && value is string text ? text : fallback;
        public void SetString(string key, string value) => values[key] = value ?? string.Empty;
        public int GetInt(string key, int fallback) => values.TryGetValue(key, out var value) && value is int number ? number : fallback;
        public void SetInt(string key, int value) => values[key] = value;
        public bool GetBool(string key, bool fallback) => values.TryGetValue(key, out var value) && value is bool flag ? flag : fallback;
        public void SetBool(string key, bool value) => values[key] = value;
        public void Erase(string key) => values.TryRemove(key, out _);
    }
}
