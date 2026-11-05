using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorHotkeysPopup : PopupWindowContent
    {
        private const float WindowWidth = 460f;
        private const float Padding = 12f;
        private const float KeyColumnWidth = 140f;
        private const float RowHeight = 18f;
        private const float TitleHeight = 22f;
        private const float SectionSpacing = 8f;
        private const float ButtonHeight = 24f;

        private static readonly (string title, string conflictGroup, (string shortcut, string keys, string action)[] rows)[] Sections =
        {
            ("Canvas", "main", new[]
            {
                (ArchitectorShortcuts.FocusView, null, "Focus the selection, or the whole layer"),
                (ArchitectorShortcuts.FocusLayer, null, "Focus the whole layer"),
                (ArchitectorShortcuts.Arrange, null, "Arrange the selection, or the whole layer"),
                (ArchitectorShortcuts.ArrangeAsCenters, null, "Arrange around the selected nodes"),
                (ArchitectorShortcuts.SelectAll, null, "Select all nodes"),
                (ArchitectorShortcuts.ActualSize, null, "Zoom to 100%"),
                (ArchitectorShortcuts.NewNode, null, "Create an empty node at the pointer"),
                (ArchitectorShortcuts.Rename, null, "Rename the selected node"),
                (ArchitectorShortcuts.LinkType, null, "Link or relink the type of the selected node"),
                (ArchitectorShortcuts.NeighbourLeft, null, "Move to the connected node on the left"),
                (ArchitectorShortcuts.NeighbourRight, null, "Move to the connected node on the right"),
                (ArchitectorShortcuts.NeighbourUp, null, "Move to the connected node above; after a click in Explorer, open the previous layer"),
                (ArchitectorShortcuts.NeighbourDown, null, "Move to the connected node below; after a click in Explorer, open the next layer"),
                ((string)null, "Esc", "Clear the selection"),
                (null, "Enter", "Open the layer of the selected Portal"),
                (null, "Ctrl+C / V / D", "Copy, paste, duplicate"),
                (null, "Delete", "Delete the selection"),
                (null, "Wheel", "Zoom"),
                (null, "Middle or right drag", "Pan")
            }),
            ("Window", "main", new[]
            {
                (ArchitectorShortcuts.PreviousLayer, null, "Previous layer"),
                (ArchitectorShortcuts.NextLayer, null, "Next layer"),
                ((string)null, "Ctrl+Z / Y", "Undo, redo")
            }),
            ("Review", "review", new[]
            {
                (ArchitectorShortcuts.ReviewSelectAll, null, "Select all"),
                ((string)null, "Esc", "Clear"),
                (null, "Click", "Toggle one row"),
                (null, "Drag", "Toggle every row under the pointer"),
                (null, "Shift+click", "Mark a range")
            })
        };

        private readonly Dictionary<string, string> keys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> problems = new(StringComparer.Ordinal);
        private GUIStyle titleStyle;
        private GUIStyle keyStyle;
        private GUIStyle actionStyle;
        private GUIStyle problemStyle;

        public override void OnOpen()
        {
            keys.Clear();
            problems.Clear();
            var manager = ShortcutManager.instance;
            foreach (var group in Sections.GroupBy(section => section.conflictGroup))
            {
                var bound = group
                    .SelectMany(section => section.rows)
                    .Where(row => row.shortcut != null)
                    .Select(row => (row.shortcut, row.action, binding: manager.GetShortcutBinding(row.shortcut).ToString()))
                    .ToArray();
                foreach (var row in bound)
                {
                    keys[row.shortcut] = row.binding;
                    if (string.IsNullOrEmpty(row.binding))
                    {
                        problems[row.shortcut] = "Not assigned";
                        continue;
                    }
                    var other = bound.FirstOrDefault(candidate => candidate.shortcut != row.shortcut && candidate.binding == row.binding);
                    if (other.shortcut != null)
                        problems[row.shortcut] = $"Same key as: {other.action}";
                }
            }
        }

        public override Vector2 GetWindowSize()
        {
            var height = Padding * 2f + ButtonHeight;
            foreach (var section in Sections)
                height += TitleHeight + section.rows.Length * RowHeight + SectionSpacing;
            return new Vector2(WindowWidth, height);
        }

        public override void OnGUI(Rect rect)
        {
            EnsureStyles();
            var width = rect.width - Padding * 2f;
            var y = Padding;
            foreach (var (title, _, rows) in Sections)
            {
                GUI.Label(new Rect(Padding, y, width, TitleHeight), title, titleStyle);
                y += TitleHeight;
                foreach (var (shortcut, fixedKeys, action) in rows)
                {
                    DrawRow(new Rect(Padding, y, width, RowHeight), shortcut, fixedKeys, action);
                    y += RowHeight;
                }
                y += SectionSpacing;
            }
            if (GUI.Button(new Rect(Padding, y, width, ButtonHeight), "Open Shortcuts")
                && !EditorApplication.ExecuteMenuItem("Edit/Shortcuts..."))
                EditorApplication.ExecuteMenuItem("Unity/Shortcuts...");
        }

        private void DrawRow(Rect rect, string shortcut, string fixedKeys, string action)
        {
            var problem = string.Empty;
            var text = fixedKeys;
            if (shortcut != null)
            {
                keys.TryGetValue(shortcut, out text);
                problems.TryGetValue(shortcut, out problem);
            }
            var hasProblem = !string.IsNullOrEmpty(problem);
            GUI.Label(
                new Rect(rect.x, rect.y, KeyColumnWidth, rect.height),
                string.IsNullOrEmpty(text) ? "—" : text,
                hasProblem ? problemStyle : keyStyle);
            GUI.Label(
                new Rect(rect.x + KeyColumnWidth, rect.y, rect.width - KeyColumnWidth, rect.height),
                new GUIContent(hasProblem ? $"{action}. {problem}" : action, problem),
                hasProblem ? problemStyle : actionStyle);
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
                return;
            var title = EditorGUIUtility.isProSkin ? Color.white : Color.black;
            var row = EditorGUIUtility.isProSkin ? new Color(0.70f, 0.70f, 0.70f) : new Color(0.30f, 0.30f, 0.30f);
            titleStyle = CreateStyle(FontStyle.Bold, title, 12);
            keyStyle = CreateStyle(FontStyle.Bold, row, 11);
            actionStyle = CreateStyle(FontStyle.Normal, row, 11);
            problemStyle = CreateStyle(FontStyle.Normal, new Color(1f, 0.76f, 0.20f), 11);
        }

        private static GUIStyle CreateStyle(FontStyle fontStyle, Color color, int fontSize)
        {
            return new GUIStyle(EditorStyles.label)
            {
                fontStyle = fontStyle,
                fontSize = fontSize,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal = { textColor = color },
                hover = { textColor = color }
            };
        }
    }
}
