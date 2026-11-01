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
        private const float KeyColumnWidth = 130f;
        private const float RowHeight = 18f;
        private const float SectionSpacing = 6f;
        private const float ButtonHeight = 24f;

        private static readonly (string title, string conflictGroup, (string shortcut, string keys, string action)[] rows)[] Sections =
        {
            ("Canvas (pointer over the canvas)", "main", new[]
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
                (ArchitectorShortcuts.NeighbourUp, null, "Move to the connected node above"),
                (ArchitectorShortcuts.NeighbourDown, null, "Move to the connected node below"),
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
            var height = 16f + ButtonHeight;
            foreach (var section in Sections)
                height += RowHeight + SectionSpacing + section.rows.Length * RowHeight;
            return new Vector2(440f, height);
        }

        public override void OnGUI(Rect rect)
        {
            GUILayout.Space(4f);
            foreach (var (title, _, rows) in Sections)
            {
                GUILayout.Label(title, EditorStyles.boldLabel, GUILayout.Height(RowHeight));
                foreach (var (shortcut, fixedKeys, action) in rows)
                    DrawRow(shortcut, fixedKeys, action);
                GUILayout.Space(SectionSpacing);
            }
            if (GUILayout.Button("Open Shortcuts", GUILayout.Height(ButtonHeight))
                && !EditorApplication.ExecuteMenuItem("Edit/Shortcuts..."))
                EditorApplication.ExecuteMenuItem("Unity/Shortcuts...");
        }

        private void DrawRow(string shortcut, string fixedKeys, string action)
        {
            var problem = string.Empty;
            var text = fixedKeys;
            if (shortcut != null)
            {
                keys.TryGetValue(shortcut, out text);
                problems.TryGetValue(shortcut, out problem);
            }
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(RowHeight)))
            {
                var previousColor = GUI.contentColor;
                if (!string.IsNullOrEmpty(problem))
                    GUI.contentColor = new Color(1f, 0.76f, 0.20f);
                GUILayout.Label(string.IsNullOrEmpty(text) ? "—" : text, EditorStyles.miniBoldLabel, GUILayout.Width(KeyColumnWidth));
                GUILayout.Label(
                    new GUIContent(string.IsNullOrEmpty(problem) ? action : $"{action}. {problem}", problem),
                    EditorStyles.miniLabel);
                GUI.contentColor = previousColor;
            }
        }
    }
}
