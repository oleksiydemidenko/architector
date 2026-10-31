using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorMemberPickerWindow : EditorWindow
    {
        private const float ToolbarHeight = 22f;
        private const float FilterHeight = 22f;
        private const float CountHeight = 20f;
        private const float RowHeight = 42f;
        private IReadOnlyList<ArchitectorIndexedMember> allMembers = Array.Empty<ArchitectorIndexedMember>();
        private IReadOnlyList<ArchitectorIndexedMember> matches = Array.Empty<ArchitectorIndexedMember>();
        private Action<ArchitectorIndexedMember> onSelected;
        private Action<IReadOnlyList<ArchitectorIndexedMember>> onAddAll;
        private string search = string.Empty;
        private string appliedFilterKey;
        private readonly ArchitectorPickerPages pages = new();
        private bool focusSearch = true;
        private int parentDepthIndex;
        private bool includeInterfaces;
        private bool includeFramework;
        private int maximumConfiguredParentDepth;
        private bool interfacesAvailable;
        private bool frameworkAvailable;
        private string[] parentDepthNames = { "No parents" };
        private int[] parentDepthValues = { 0 };

        public static void Show(
            Vector2 screenPosition,
            IReadOnlyList<ArchitectorIndexedMember> members,
            ArchitectorOptimizationSettings optimization,
            Action<ArchitectorIndexedMember> onSelected,
            Action<IReadOnlyList<ArchitectorIndexedMember>> onAddAll)
        {
            var window = CreateInstance<ArchitectorMemberPickerWindow>();
            window.titleContent = new GUIContent("Select Contract Member");
            window.allMembers = members ?? Array.Empty<ArchitectorIndexedMember>();
            window.onSelected = onSelected;
            window.onAddAll = onAddAll;
            window.ConfigureScope(optimization);
            window.focusSearch = true;
            window.RebuildMatches();
            window.ShowAsDropDown(new Rect(screenPosition, Vector2.zero), new Vector2(640f, 420f));
            window.Focus();
        }

        private void OnGUI()
        {
            GUI.SetNextControlName("ArchitectorMemberSearch");
            var nextSearch = EditorGUI.TextField(
                new Rect(0f, 0f, position.width, ToolbarHeight),
                search,
                EditorStyles.toolbarSearchField);
            if (!string.Equals(nextSearch, search, StringComparison.Ordinal))
            {
                search = nextSearch;
                pages.Reset();
                RebuildMatches();
            }
            if (focusSearch)
            {
                EditorGUI.FocusTextInControl("ArchitectorMemberSearch");
                EditorGUIUtility.editingTextField = true;
                Repaint();
                if (Event.current.type == EventType.Repaint)
                    focusSearch = false;
            }

            var filterY = ToolbarHeight;
            GUI.Label(new Rect(6f, filterY + 2f, 48f, 18f), "Parents", EditorStyles.miniLabel);
            var nextParentDepth = EditorGUI.Popup(new Rect(54f, filterY + 1f, 92f, 19f), parentDepthIndex, parentDepthNames);
            bool nextInterfaces;
            bool nextFramework;
            using (new EditorGUI.DisabledScope(!interfacesAvailable))
                nextInterfaces = EditorGUI.ToggleLeft(new Rect(154f, filterY + 1f, 82f, 19f), "Interfaces", includeInterfaces);
            using (new EditorGUI.DisabledScope(!frameworkAvailable))
                nextFramework = EditorGUI.ToggleLeft(new Rect(242f, filterY + 1f, 112f, 19f), "Framework API", includeFramework);
            if (nextParentDepth != parentDepthIndex || nextInterfaces != includeInterfaces || nextFramework != includeFramework)
            {
                parentDepthIndex = nextParentDepth;
                includeInterfaces = nextInterfaces;
                includeFramework = nextFramework;
                pages.Reset();
                RebuildMatches();
            }

            var countY = ToolbarHeight + FilterHeight;
            GUI.Label(new Rect(6f, countY, position.width - 116f, CountHeight),
                $"{matches.Count} member{(matches.Count == 1 ? string.Empty : "s")}", EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(matches.Count == 0))
            {
                if (GUI.Button(new Rect(position.width - 106f, countY + 1f, 100f, 18f), $"Add All ({matches.Count})", EditorStyles.miniButton))
                {
                    SelectAll();
                    GUIUtility.ExitGUI();
                }
            }
            var viewportTop = ToolbarHeight + FilterHeight + CountHeight;
            var viewport = new Rect(0f, viewportTop, position.width, Mathf.Max(0f, position.height - viewportTop));
            if (matches.Count == 0)
            {
                EditorGUI.HelpBox(viewport, "No available visible member matches the search.", MessageType.Info);
                return;
            }
            var submit = Event.current;
            if (submit.type == EventType.KeyDown && (submit.keyCode == KeyCode.Return || submit.keyCode == KeyCode.KeypadEnter))
            {
                submit.Use();
                Select(matches[Mathf.Min(pages.FirstVisibleIndex, matches.Count - 1)]);
                GUIUtility.ExitGUI();
            }
            pages.Draw(viewport, matches.Count, RowHeight, (index, row) => DrawRow(matches[index], row), Repaint);
            if (Event.current.type == EventType.MouseMove && viewport.Contains(Event.current.mousePosition))
                Repaint();
        }

        private void DrawRow(ArchitectorIndexedMember member, Rect row)
        {
            var contentWidth = row.width;
            var current = Event.current;
            {
                var hovered = row.Contains(current.mousePosition);
                if (hovered && current.type == EventType.Repaint)
                    EditorGUI.DrawRect(row, EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.06f));
                if (hovered && current.type == EventType.MouseDown && current.button == 0)
                {
                    current.Use();
                    Select(member);
                    GUIUtility.ExitGUI();
                }
                ArchitectorSemanticPalette.DrawBadge(
                    new Rect(6f, row.y + 4f, 78f, 16f),
                    member.Kind,
                    ArchitectorSemanticPalette.ForMemberKind(member.Kind));
                GUI.Label(new Rect(92f, row.y + 3f, contentWidth - 98f, 18f),
                    new GUIContent(member.Declaration, member.Declaration), EditorStyles.label);
                var origin = member.IsInterfaceMember ? "interface" : member.ParentDepth > 0 ? $"parent {member.ParentDepth}" : "declared";
                GUI.Label(new Rect(92f, row.y + 21f, contentWidth - 98f, 18f),
                    $"{origin} · {member.DeclaringTypeName}", EditorStyles.miniLabel);
                EditorGUI.DrawRect(new Rect(0f, row.yMax - 1f, contentWidth, 1f),
                    EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.06f) : new Color(0f, 0f, 0f, 0.08f));
            }
        }

        private void RebuildMatches()
        {
            var filterKey = $"{search}\n{parentDepthIndex}\n{includeInterfaces}\n{includeFramework}";
            if (string.Equals(appliedFilterKey, filterKey, StringComparison.Ordinal))
                return;
            appliedFilterKey = filterKey;
            var terms = (search ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var maximumParentDepth = parentDepthValues[Mathf.Clamp(parentDepthIndex, 0, parentDepthValues.Length - 1)];
            matches = allMembers.Where(member =>
                    (includeFramework || !member.IsFrameworkMember)
                    && (member.IsInterfaceMember ? includeInterfaces : member.ParentDepth <= maximumParentDepth)
                    && terms.All(term =>
                    member.Declaration.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || member.Kind.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                    || member.DeclaringTypeName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToArray();
        }

        private void ConfigureScope(ArchitectorOptimizationSettings optimization)
        {
            optimization ??= new ArchitectorOptimizationSettings();
            maximumConfiguredParentDepth = optimization.memberParentDepth < 0
                ? int.MaxValue
                : optimization.memberParentDepth;
            interfacesAvailable = optimization.includeInterfaceMembers;
            frameworkAvailable = optimization.includeFrameworkMembers;
            var names = new List<string> { "No parents" };
            var values = new List<int> { 0 };
            if (maximumConfiguredParentDepth >= 1)
            {
                names.Add("1 level");
                values.Add(1);
            }
            if (maximumConfiguredParentDepth >= 2)
            {
                names.Add("2 levels");
                values.Add(2);
            }
            if (maximumConfiguredParentDepth == int.MaxValue)
            {
                names.Add("All parents");
                values.Add(int.MaxValue);
            }
            parentDepthNames = names.ToArray();
            parentDepthValues = values.ToArray();
        }

        private void Select(ArchitectorIndexedMember member)
        {
            var callback = onSelected;
            onSelected = null;
            onAddAll = null;
            callback?.Invoke(member);
            Close();
        }

        private void SelectAll()
        {
            var callback = onAddAll;
            onSelected = null;
            onAddAll = null;
            callback?.Invoke(matches.ToArray());
            Close();
        }
    }
}
