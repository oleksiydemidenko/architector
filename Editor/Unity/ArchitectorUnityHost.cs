using System.IO;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    // The Unity Editor as the host of the Architector core: SessionState, EditorPrefs, a dialog for the person,
    // AssetDatabase and the Console. Files under Editor/Unity are the Unity interface; the core does not compile them.
    public sealed class ArchitectorUnityHost : IArchitectorHost
    {
        public string ProjectRoot { get; } = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public IArchitectorValueStore Session { get; } = new SessionStore();
        public IArchitectorValueStore Preferences { get; } = new PreferenceStore();
        public bool CanAskPerson => !Application.isBatchMode;

        public bool AskPerson(string title, string question, string allow, string refuse) =>
            CanAskPerson && EditorUtility.DisplayDialog(title, question, allow, refuse);

        public string AssetGuid(string projectRelativePath) => AssetDatabase.AssetPathToGUID(projectRelativePath);

        public void LogWarning(string message) => Debug.LogWarning(message);

        private sealed class SessionStore : IArchitectorValueStore
        {
            public string GetString(string key, string fallback) => SessionState.GetString(key, fallback);
            public void SetString(string key, string value) => SessionState.SetString(key, value);
            public int GetInt(string key, int fallback) => SessionState.GetInt(key, fallback);
            public void SetInt(string key, int value) => SessionState.SetInt(key, value);
            public bool GetBool(string key, bool fallback) => SessionState.GetBool(key, fallback);
            public void SetBool(string key, bool value) => SessionState.SetBool(key, value);

            public void Erase(string key)
            {
                SessionState.EraseString(key);
                SessionState.EraseInt(key);
                SessionState.EraseBool(key);
            }
        }

        private sealed class PreferenceStore : IArchitectorValueStore
        {
            public string GetString(string key, string fallback) => EditorPrefs.GetString(key, fallback);
            public void SetString(string key, string value) => EditorPrefs.SetString(key, value);
            public int GetInt(string key, int fallback) => EditorPrefs.GetInt(key, fallback);
            public void SetInt(string key, int value) => EditorPrefs.SetInt(key, value);
            public bool GetBool(string key, bool fallback) => EditorPrefs.GetBool(key, fallback);
            public void SetBool(string key, bool value) => EditorPrefs.SetBool(key, value);
            public void Erase(string key) => EditorPrefs.DeleteKey(key);
        }
    }
}
