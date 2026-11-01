using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorHotkeysPopup : PopupWindowContent
    {
        private const float KeyColumnWidth = 130f;
        private const float RowHeight = 18f;

        private static readonly (string title, (string keys, string action)[] rows)[] Sections =
        {
            ("Canvas (pointer over the canvas)", new[]
            {
                ("F", "Focus the selection, or the whole layer"),
                ("Shift+F", "Focus the whole layer"),
                ("A", "Arrange the selection, or the whole layer"),
                ("Shift+A", "Arrange around the selected nodes"),
                ("Ctrl+A", "Select all nodes"),
                ("Esc", "Clear the selection"),
                ("1", "Zoom to 100%"),
                ("N", "Create an empty node at the pointer"),
                ("R", "Rename the selected node"),
                ("L", "Link or relink the type of the selected node"),
                ("Enter", "Open the layer of the selected Portal"),
                ("Arrows", "Move to the connected node in that direction"),
                ("Ctrl+C / V / D", "Copy, paste, duplicate"),
                ("Delete", "Delete the selection"),
                ("Wheel", "Zoom"),
                ("Middle or right drag", "Pan")
            }),
            ("Window", new[]
            {
                ("Ctrl+Z / Y", "Undo, redo"),
                ("Alt+Left / Right", "Previous and next layer")
            }),
            ("Review", new[]
            {
                ("Drag", "Mark every row under the pointer"),
                ("Ctrl+click", "Toggle one row"),
                ("Shift+click", "Mark a range"),
                ("Ctrl+A", "Select all"),
                ("Esc", "Clear")
            })
        };

        public override Vector2 GetWindowSize()
        {
            var height = 12f;
            foreach (var section in Sections)
                height += RowHeight + 6f + section.rows.Length * RowHeight;
            return new Vector2(400f, height);
        }

        public override void OnGUI(Rect rect)
        {
            GUILayout.Space(4f);
            foreach (var (title, rows) in Sections)
            {
                GUILayout.Label(title, EditorStyles.boldLabel, GUILayout.Height(RowHeight));
                foreach (var (keys, action) in rows)
                {
                    using (new EditorGUILayout.HorizontalScope(GUILayout.Height(RowHeight)))
                    {
                        GUILayout.Label(keys, EditorStyles.miniBoldLabel, GUILayout.Width(KeyColumnWidth));
                        GUILayout.Label(action, EditorStyles.miniLabel);
                    }
                }
                GUILayout.Space(6f);
            }
        }
    }
}
