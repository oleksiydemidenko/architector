using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorTypePickerWindow : EditorWindow
    {
        private const float ToolbarHeight = 22f;
        private const float CountHeight = 20f;
        private const float RowHeight = 42f;

        private IReadOnlyList<ArchitectorIndexedType> allTypes = Array.Empty<ArchitectorIndexedType>();
        private IReadOnlyList<ArchitectorIndexedType> matches = Array.Empty<ArchitectorIndexedType>();
        private Action<ArchitectorIndexedType> onSelected;
        private string search = string.Empty;
        private string appliedSearch;
        private readonly ArchitectorPickerPages pages = new();
        private bool focusSearch = true;

        public static void Show(
            Vector2 screenPosition,
            IReadOnlyList<ArchitectorIndexedType> types,
            Action<ArchitectorIndexedType> onSelected)
        {
            var window = CreateInstance<ArchitectorTypePickerWindow>();
            window.titleContent = new GUIContent("Select C# Type");
            window.allTypes = types ?? Array.Empty<ArchitectorIndexedType>();
            window.onSelected = onSelected;
            window.focusSearch = true;
            window.RebuildMatches();
            window.ShowAsDropDown(new Rect(screenPosition, Vector2.zero), new Vector2(520f, 420f));
            window.Focus();
        }

        private void OnGUI()
        {
            var searchRect = new Rect(0f, 0f, position.width, ToolbarHeight);
            GUI.SetNextControlName("ArchitectorTypeSearch");
            var nextSearch = EditorGUI.TextField(searchRect, search, EditorStyles.toolbarSearchField);
            if (!string.Equals(nextSearch, search, StringComparison.Ordinal))
            {
                search = nextSearch;
                pages.Reset();
                RebuildMatches();
            }
            if (focusSearch)
            {
                EditorGUI.FocusTextInControl("ArchitectorTypeSearch");
                EditorGUIUtility.editingTextField = true;
                Repaint();
                if (Event.current.type == EventType.Repaint)
                    focusSearch = false;
            }
            GUI.Label(
                new Rect(6f, ToolbarHeight, position.width - 12f, CountHeight),
                $"{matches.Count} type{(matches.Count == 1 ? string.Empty : "s")}",
                EditorStyles.miniLabel);

            var listRect = new Rect(0f, ToolbarHeight + CountHeight, position.width, Mathf.Max(0f, position.height - ToolbarHeight - CountHeight));
            if (matches.Count == 0)
            {
                EditorGUI.HelpBox(listRect, "No indexed type matches the search. Only exact MonoScript mappings inside configured source roots are listed.", MessageType.Info);
                return;
            }

            if (IsSubmit(Event.current))
            {
                Event.current.Use();
                Select(matches[Mathf.Min(pages.FirstVisibleIndex, matches.Count - 1)]);
                GUIUtility.ExitGUI();
            }
            pages.Draw(listRect, matches.Count, RowHeight, (index, row) => DrawRow(matches[index], row), Repaint);
            if (Event.current.type == EventType.MouseMove && listRect.Contains(Event.current.mousePosition))
                Repaint();
        }

        private static bool IsSubmit(Event current)
        {
            return current.type == EventType.KeyDown
                   && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
        }

        private void DrawRow(ArchitectorIndexedType type, Rect rowRect)
        {
            var contentWidth = rowRect.width;
            var current = Event.current;
            {
                var hovered = rowRect.Contains(current.mousePosition);
                if (hovered && current.type == EventType.Repaint)
                    EditorGUI.DrawRect(rowRect, EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.06f));
                if (hovered && current.type == EventType.MouseDown && current.button == 0)
                {
                    current.Use();
                    Select(type);
                    GUIUtility.ExitGUI();
                }

                ArchitectorSemanticPalette.DrawBadge(
                    new Rect(6f, rowRect.y + 4f, 72f, 16f),
                    type.Kind,
                    ArchitectorSemanticPalette.ForTypeKind(type.Kind));
                GUI.Label(new Rect(86f, rowRect.y + 3f, contentWidth - 92f, 18f), type.FullDisplayName, EditorStyles.label);
                var source = type.SourceDeclarations.FirstOrDefault()?.repositoryPath ?? "Source unavailable";
                GUI.Label(new Rect(86f, rowRect.y + 21f, contentWidth - 92f, 18f), $"{type.AssemblyName} · {source}", EditorStyles.miniLabel);
                EditorGUI.DrawRect(new Rect(0f, rowRect.yMax - 1f, contentWidth, 1f),
                    EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.06f) : new Color(0f, 0f, 0f, 0.08f));
            }
        }

        private void RebuildMatches()
        {
            if (string.Equals(appliedSearch, search, StringComparison.Ordinal))
                return;
            appliedSearch = search;
            var terms = (search ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            matches = terms.Length == 0
                ? allTypes
                : allTypes.Where(type => terms.All(term =>
                    type.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || type.FullDisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || type.Namespace.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || type.AssemblyName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
        }

        private void Select(ArchitectorIndexedType type)
        {
            var callback = onSelected;
            onSelected = null;
            callback?.Invoke(type);
            Close();
        }
    }
}
