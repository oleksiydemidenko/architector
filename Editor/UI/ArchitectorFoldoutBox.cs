using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Architector.Editor
{
    public readonly struct ArchitectorFoldoutBox : IDisposable
    {
        private const float HeaderHeight = 26f;
        private const float FoldoutHeight = 20f;
        private const float FoldoutInset = 11f;
        private const float BoxSpacing = 6f;
        private static GUIStyle headerStyle;
        private static GUIStyle bodyStyle;
        private static GUIStyle contentStyle;

        public ArchitectorFoldoutBox(string title, ref bool expanded)
            : this(new GUIContent(title), ref expanded)
        {
        }

        public ArchitectorFoldoutBox(GUIContent title, ref bool expanded, Texture trailingIcon = null)
        {
            EnsureStyles();
            var rect = GUILayoutUtility.GetRect(0f, HeaderHeight, GUILayout.ExpandWidth(true));
            DrawHeaderBackground(rect);
            var foldoutRect = new Rect(
                rect.x + FoldoutInset,
                rect.y + (rect.height - FoldoutHeight) * 0.5f,
                rect.width - FoldoutInset - 4f,
                FoldoutHeight);
            var previousIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            expanded = EditorGUI.Foldout(foldoutRect, expanded, title, true, headerStyle);
            if (trailingIcon != null)
            {
                var iconX = foldoutRect.x + headerStyle.CalcSize(title).x + 4f;
                GUI.DrawTexture(new Rect(Mathf.Min(iconX, rect.xMax - 22f), rect.y + 5f, 16f, 16f), trailingIcon, ScaleMode.ScaleToFit);
            }
            EditorGUI.indentLevel = previousIndent;
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && rect.Contains(Event.current.mousePosition))
            {
                expanded = !expanded;
                Event.current.Use();
            }
            Expanded = expanded;
            if (expanded)
                EditorGUILayout.BeginVertical(bodyStyle);
        }

        public bool Expanded { get; }

        public static string WarningText(string text)
        {
            return $"<color={(EditorGUIUtility.isProSkin ? "#F0C040" : "#8A5A00")}>{text}</color>";
        }

        public static GUIStyle ContentStyle
        {
            get
            {
                EnsureStyles();
                return contentStyle;
            }
        }

        public void Dispose()
        {
            if (Expanded)
                EditorGUILayout.EndVertical();
            EditorGUILayout.Space(BoxSpacing);
        }

        private static void DrawHeaderBackground(Rect rect)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            var background = EditorGUIUtility.isProSkin ? new Color(0.205f, 0.205f, 0.205f) : new Color(0.76f, 0.76f, 0.76f);
            var border = EditorGUIUtility.isProSkin ? new Color(0.105f, 0.105f, 0.105f) : new Color(0.46f, 0.46f, 0.46f);
            EditorGUI.DrawRect(rect, background);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), border);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), border);
        }

        private static void EnsureStyles()
        {
            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(EditorStyles.foldout) { richText = true };
                headerStyle.focused.textColor = headerStyle.normal.textColor;
                headerStyle.onFocused.textColor = headerStyle.onNormal.textColor;
            }
            bodyStyle ??= new GUIStyle(ReorderableList.defaultBehaviours.boxBackground)
            {
                padding = new RectOffset(10, 10, 8, 10),
                margin = new RectOffset(0, 0, 0, 0),
                fixedHeight = 0f,
                stretchHeight = false
            };
            contentStyle ??= new GUIStyle { padding = new RectOffset(8, 8, 8, 8) };
        }
    }
}
