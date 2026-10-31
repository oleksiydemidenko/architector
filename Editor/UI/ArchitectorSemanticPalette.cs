using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    internal static class ArchitectorSemanticPalette
    {
        public static Color ForTypeKind(string kind)
        {
            return kind switch
            {
                "interface" => ThemeColor(new Color(0.72f, 0.56f, 1.00f), new Color(0.42f, 0.25f, 0.67f)),
                "struct" => ThemeColor(new Color(0.30f, 0.78f, 1.00f), new Color(0.00f, 0.43f, 0.62f)),
                "enum" => ThemeColor(new Color(1.00f, 0.70f, 0.20f), new Color(0.65f, 0.37f, 0.00f)),
                "delegate" => ThemeColor(new Color(1.00f, 0.47f, 0.78f), new Color(0.66f, 0.18f, 0.48f)),
                _ => ThemeColor(new Color(0.42f, 0.84f, 0.48f), new Color(0.08f, 0.48f, 0.17f))
            };
        }

        public static Color ForMemberKind(string kind)
        {
            return kind switch
            {
                "method" => ThemeColor(new Color(1.00f, 0.82f, 0.32f), new Color(0.58f, 0.40f, 0.00f)),
                "constructor" => ThemeColor(new Color(1.00f, 0.65f, 0.20f), new Color(0.65f, 0.32f, 0.00f)),
                "property" or "indexer" => ThemeColor(new Color(0.58f, 0.86f, 0.35f), new Color(0.25f, 0.48f, 0.05f)),
                "field" => ThemeColor(new Color(0.38f, 0.66f, 1.00f), new Color(0.04f, 0.32f, 0.72f)),
                "constant" => ThemeColor(new Color(1.00f, 0.55f, 0.20f), new Color(0.72f, 0.28f, 0.00f)),
                "event" => ThemeColor(new Color(1.00f, 0.42f, 0.36f), new Color(0.72f, 0.13f, 0.10f)),
                "operator" => ThemeColor(new Color(0.82f, 0.52f, 1.00f), new Color(0.50f, 0.22f, 0.70f)),
                _ => ThemeColor(new Color(0.72f, 0.72f, 0.72f), new Color(0.30f, 0.30f, 0.30f))
            };
        }

        public static void DrawBadge(Rect rect, string caption, Color color)
        {
            var background = color;
            background.a = EditorGUIUtility.isProSkin ? 0.27f : 0.18f;
            EditorGUI.DrawRect(rect, background);
            var style = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip
            };
            style.normal.textColor = color;
            GUI.Label(rect, caption, style);
        }

        private static Color ThemeColor(Color dark, Color light)
        {
            return EditorGUIUtility.isProSkin ? dark : light;
        }
    }
}
