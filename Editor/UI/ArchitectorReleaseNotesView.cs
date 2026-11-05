using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public static class ArchitectorReleaseNotesView
    {
        private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
        private static GUIStyle style;
        private static string formattedSource;
        private static string formatted;

        public static bool Draw(string notes, bool expanded)
        {
            if (string.IsNullOrEmpty(notes))
                return expanded;
            expanded = EditorGUILayout.Foldout(expanded, "Changes", true);
            if (!expanded)
                return false;
            style ??= new GUIStyle(EditorStyles.wordWrappedLabel) { richText = true };
            using (new EditorGUI.IndentLevelScope())
                GUILayout.Label(Format(notes), style);
            return true;
        }

        public static string Format(string notes)
        {
            if (ReferenceEquals(notes, formattedSource))
                return formatted;
            var text = new StringBuilder();
            foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith("#"))
                    line = "<b>" + line.TrimStart('#', ' ') + "</b>";
                else if (line.StartsWith("- "))
                    line = "•  " + line.Substring(2);
                line = Bold.Replace(line, "<b>$1</b>").Replace("`", string.Empty);
                if (text.Length > 0)
                    text.Append('\n');
                text.Append(line);
            }
            formattedSource = notes;
            formatted = text.ToString();
            return formatted;
        }
    }
}
