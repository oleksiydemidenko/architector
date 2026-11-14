using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public static class ArchitectorSemanticPalette
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

        // Colors of declaration parts. Type names take the class color of ForTypeKind and member names the color of
        // their member kind, so a declaration reads with the same colors as the type and member pickers.
        public static Color ForCodeRole(ArchitectorCodeRole role, string memberKind)
        {
            return role switch
            {
                ArchitectorCodeRole.Keyword => ThemeColor(new Color(0.34f, 0.61f, 0.84f), new Color(0.00f, 0.25f, 0.75f)),
                ArchitectorCodeRole.Type => ForTypeKind("class"),
                ArchitectorCodeRole.Member => ForMemberKind(memberKind),
                ArchitectorCodeRole.Parameter => ThemeColor(new Color(0.61f, 0.86f, 1.00f), new Color(0.12f, 0.35f, 0.55f)),
                ArchitectorCodeRole.Literal => ThemeColor(new Color(0.81f, 0.57f, 0.47f), new Color(0.64f, 0.08f, 0.08f)),
                _ => ThemeColor(new Color(0.78f, 0.78f, 0.78f), new Color(0.20f, 0.20f, 0.20f))
            };
        }

        private const int RichTextLimit = 4000;
        private static readonly Dictionary<(string text, string kind, bool pro), string> RichTexts = new();

        // A declaration as IMGUI rich text (a style with richText = true) in the code colors; cached, because the canvas
        // draws the same rows every frame. Kind "base" and "interface" color a whole row as a type name after its label.
        public static string RichDeclaration(string declaration, string memberKind)
        {
            declaration ??= string.Empty;
            var key = (declaration, memberKind ?? string.Empty, EditorGUIUtility.isProSkin);
            if (RichTexts.TryGetValue(key, out var rich))
                return rich;
            if (RichTexts.Count >= RichTextLimit)
                RichTexts.Clear();
            var text = new StringBuilder(declaration.Length * 2);
            if (memberKind is "base" or "interface")
            {
                var separator = declaration.IndexOf(" · ", System.StringComparison.Ordinal);
                var label = separator >= 0 ? declaration.Substring(0, separator + 3) : string.Empty;
                Append(text, label, ArchitectorCodeRole.Plain, memberKind);
                Append(text, declaration.Substring(label.Length), ArchitectorCodeRole.Type, memberKind);
            }
            else
                foreach (var (part, role) in ArchitectorDeclarationSyntax.Classify(declaration))
                    Append(text, part, role, memberKind);
            rich = text.ToString();
            RichTexts[key] = rich;
            return rich;
        }

        private static void Append(StringBuilder text, string part, ArchitectorCodeRole role, string memberKind)
        {
            if (part.Length == 0)
                return;
            // A zero-width space after "<" keeps generic arguments from being read as rich text tags.
            var escaped = part.Replace("<", "<​");
            if (role == ArchitectorCodeRole.Plain && char.IsWhiteSpace(part[0]))
            {
                text.Append(escaped);
                return;
            }
            text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(ForCodeRole(role, memberKind))).Append('>')
                .Append(escaped).Append("</color>");
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
