using System.Collections.Generic;
using System.Linq;
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

        // Declarations read like code in an IDE: keywords and modifiers in the keyword color, type names in the color of
        // their kind (ForTypeKind: class, struct, interface, enum, delegate), everything else (member and parameter
        // names, literals, punctuation) in the default text color of the style.
        public static Color KeywordColor => ThemeColor(new Color(0.34f, 0.61f, 0.84f), new Color(0.00f, 0.25f, 0.75f));

        private const int RichTextLimit = 4000;
        private static readonly Dictionary<(string text, bool pro), string> RichTexts = new();
        private static Dictionary<string, string> typeKinds;

        // A member declaration as IMGUI rich text (a style with richText = true); cached, because the canvas draws the
        // same rows every frame.
        public static string RichDeclaration(string declaration)
        {
            declaration ??= string.Empty;
            var key = (declaration, EditorGUIUtility.isProSkin);
            if (RichTexts.TryGetValue(key, out var rich))
                return rich;
            if (RichTexts.Count >= RichTextLimit)
                RichTexts.Clear();
            var text = new StringBuilder(declaration.Length * 2);
            foreach (var (part, role) in ArchitectorDeclarationSyntax.Classify(declaration))
                Append(text, part, role switch
                {
                    ArchitectorCodeRole.Keyword => KeywordColor,
                    ArchitectorCodeRole.Type => ForTypeKind(KindOfTypeName(part)),
                    _ => (Color?)null
                });
            rich = text.ToString();
            RichTexts[key] = rich;
            return rich;
        }

        // A type's own declaration as code shows it, "public sealed class HeldItemModule : UnitModule, IHeldItemRelease":
        // modifiers and the kind as keywords, the type in its kind's color, the base type in its kind's color (class
        // when unknown) and every interface in the interface color.
        public static List<(string Text, Color? Color)> TypeDeclarationSegments(ArchitectorTypeRecord type)
        {
            var segments = new List<(string Text, Color? Color)>();
            if (type == null)
                return segments;
            foreach (var modifier in type.modifiers ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(modifier))
                    segments.Add((modifier.Trim() + " ", KeywordColor));
            var kind = string.IsNullOrEmpty(type.kind) ? "class" : type.kind;
            segments.Add((kind + " ", KeywordColor));
            var generic = type.genericParameters != null && type.genericParameters.Count > 0
                ? $"<{string.Join(", ", type.genericParameters)}>"
                : string.Empty;
            // "record struct" is colored as a struct, "record class" as a class.
            segments.Add((type.name + generic, ForTypeKind(kind.EndsWith("struct", System.StringComparison.Ordinal) ? "struct" : kind)));
            var first = true;
            void Inherit(string name, string inheritedKind)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;
                segments.Add((first ? " : " : ", ", null));
                segments.Add((name, ForTypeKind(inheritedKind)));
                first = false;
            }
            if (type.baseType != null)
                Inherit(type.baseType.name, string.IsNullOrEmpty(type.baseType.kind) ? "class" : type.baseType.kind);
            foreach (var contract in type.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                Inherit(contract?.name, "interface");
            return segments;
        }

        // Segments as rich text, cut to the first visibleText characters when the text was shortened to fit (the
        // shortened text ends with an ellipsis, which is kept).
        public static string RichSegments(IReadOnlyList<(string Text, Color? Color)> segments, string visibleText = null)
        {
            var full = string.Concat(segments.Select(segment => segment.Text));
            var keep = visibleText == null || visibleText == full ? full.Length : Mathf.Max(0, visibleText.TrimEnd('…').Length);
            var text = new StringBuilder(full.Length * 2);
            foreach (var (part, color) in segments)
            {
                if (keep <= 0)
                    break;
                Append(text, part.Length <= keep ? part : part.Substring(0, keep), color);
                keep -= part.Length;
            }
            if (visibleText != null && visibleText != full && visibleText.EndsWith("…", System.StringComparison.Ordinal))
                text.Append('…');
            return text.ToString();
        }

        // The kind of a type by its name, from the loaded assemblies (Boolean and Vector3 are structs, IList an
        // interface); a name several kinds share takes the most common one, an unknown name is a class.
        public static string KindOfTypeName(string name)
        {
            if (typeKinds == null)
            {
                var counts = new Dictionary<string, Dictionary<string, int>>(System.StringComparer.Ordinal);
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (System.Reflection.ReflectionTypeLoadException exception)
                    {
                        types = exception.Types;
                    }
                    catch (System.Exception)
                    {
                        continue;
                    }
                    foreach (var candidate in types)
                    {
                        if (candidate == null || candidate.IsNested)
                            continue;
                        var tick = candidate.Name.IndexOf('`');
                        var shortName = tick >= 0 ? candidate.Name.Substring(0, tick) : candidate.Name;
                        var candidateKind = candidate.IsInterface ? "interface"
                            : candidate.IsEnum ? "enum"
                            : candidate.IsValueType ? "struct"
                            : typeof(System.MulticastDelegate).IsAssignableFrom(candidate) && candidate != typeof(System.MulticastDelegate) ? "delegate"
                            : "class";
                        if (!counts.TryGetValue(shortName, out var byKind))
                            counts[shortName] = byKind = new Dictionary<string, int>(System.StringComparer.Ordinal);
                        byKind[candidateKind] = byKind.TryGetValue(candidateKind, out var count) ? count + 1 : 1;
                    }
                }
                typeKinds = counts.ToDictionary(entry => entry.Key, entry => entry.Value.OrderByDescending(item => item.Value).First().Key, System.StringComparer.Ordinal);
            }
            return name != null && typeKinds.TryGetValue(name, out var kind) ? kind : "class";
        }

        private static void Append(StringBuilder text, string part, Color? color)
        {
            if (part.Length == 0)
                return;
            // A zero-width space after "<" keeps generic arguments from being read as rich text tags.
            var escaped = part.Replace("<", "<​");
            if (color == null || char.IsWhiteSpace(part[0]))
            {
                text.Append(escaped);
                return;
            }
            text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(color.Value)).Append('>')
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
