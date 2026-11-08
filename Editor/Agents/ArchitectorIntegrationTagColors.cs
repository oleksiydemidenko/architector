using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor.Agents
{
    internal static class ArchitectorIntegrationTagColors
    {
        private static readonly Dictionary<string, GUIStyle> Styles = new(StringComparer.OrdinalIgnoreCase);

        private static readonly (Color dark, Color light)[] Spare =
        {
            (new Color(0.36f, 0.86f, 0.70f), new Color(0.00f, 0.46f, 0.34f)),
            (new Color(1.00f, 0.62f, 0.30f), new Color(0.68f, 0.30f, 0.00f)),
            (new Color(1.00f, 0.50f, 0.72f), new Color(0.68f, 0.16f, 0.42f)),
            (new Color(0.70f, 0.86f, 0.36f), new Color(0.32f, 0.48f, 0.00f))
        };

        public static Color For(string tag)
        {
            if (string.Equals(tag, "Extension", StringComparison.OrdinalIgnoreCase))
                return Theme(new Color(0.78f, 0.58f, 1.00f), new Color(0.45f, 0.22f, 0.72f));
            if (string.Equals(tag, "MCP", StringComparison.OrdinalIgnoreCase))
                return Theme(new Color(0.40f, 0.74f, 1.00f), new Color(0.00f, 0.36f, 0.68f));
            var hash = 2166136261u;
            foreach (var character in tag.ToUpperInvariant())
                hash = (hash ^ character) * 16777619u;
            var pair = Spare[hash % (uint)Spare.Length];
            return Theme(pair.dark, pair.light);
        }

        public static string Text(string tag)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(For(tag))}>{tag}</color>";
        }

        public static GUIStyle ButtonStyle(string tag)
        {
            var key = (EditorGUIUtility.isProSkin ? "d:" : "l:") + tag;
            if (Styles.TryGetValue(key, out var cached))
                return cached;
            var style = new GUIStyle(EditorStyles.miniButton);
            Styles[key] = style;
            var color = For(tag);
            style.normal.textColor = color;
            style.onNormal.textColor = color;
            style.hover.textColor = color;
            style.onHover.textColor = color;
            style.active.textColor = color;
            style.onActive.textColor = color;
            style.focused.textColor = color;
            style.onFocused.textColor = color;
            return style;
        }

        private static Color Theme(Color dark, Color light)
        {
            return EditorGUIUtility.isProSkin ? dark : light;
        }
    }
}
