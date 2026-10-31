using System;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    internal sealed class ArchitectorPickerPages
    {
        private const float PagerHeight = 22f;
        private int page;

        public int FirstVisibleIndex { get; private set; }

        public void Reset()
        {
            page = 0;
            FirstVisibleIndex = 0;
        }

        public void Draw(Rect area, int count, float rowHeight, Action<int, Rect> drawRow, Action repaint)
        {
            var rowsPerPage = Mathf.Max(1, Mathf.FloorToInt(area.height / rowHeight));
            var paged = count > rowsPerPage;
            if (paged)
                rowsPerPage = Mathf.Max(1, Mathf.FloorToInt((area.height - PagerHeight) / rowHeight));
            var pageCount = Mathf.Max(1, Mathf.CeilToInt(count / (float)rowsPerPage));
            page = Mathf.Clamp(page, 0, pageCount - 1);

            var current = Event.current;
            if (paged && current.type == EventType.ScrollWheel && area.Contains(current.mousePosition))
            {
                page = Mathf.Clamp(page + (current.delta.y > 0f ? 1 : -1), 0, pageCount - 1);
                current.Use();
                repaint();
            }
            if (paged && current.type == EventType.KeyDown
                      && (current.keyCode == KeyCode.PageDown || current.keyCode == KeyCode.PageUp))
            {
                page = Mathf.Clamp(page + (current.keyCode == KeyCode.PageDown ? 1 : -1), 0, pageCount - 1);
                current.Use();
                repaint();
            }

            FirstVisibleIndex = page * rowsPerPage;
            var last = Mathf.Min(count, FirstVisibleIndex + rowsPerPage);
            for (var index = FirstVisibleIndex; index < last; index++)
                drawRow(index, new Rect(area.x, area.y + (index - FirstVisibleIndex) * rowHeight, area.width, rowHeight));

            if (!paged)
                return;
            var pager = new Rect(area.x, area.yMax - PagerHeight, area.width, PagerHeight);
            EditorGUI.DrawRect(pager, EditorGUIUtility.isProSkin ? new Color(0f, 0f, 0f, 0.18f) : new Color(0f, 0f, 0f, 0.06f));
            var center = pager.center.x;
            using (new EditorGUI.DisabledScope(page == 0))
            {
                if (GUI.Button(new Rect(center - 70f, pager.y + 2f, 28f, 18f), "◀", EditorStyles.miniButtonLeft))
                    page--;
            }
            GUI.Label(new Rect(center - 42f, pager.y + 2f, 84f, 18f), $"{page + 1} / {pageCount}", EditorStyles.centeredGreyMiniLabel);
            using (new EditorGUI.DisabledScope(page >= pageCount - 1))
            {
                if (GUI.Button(new Rect(center + 42f, pager.y + 2f, 28f, 18f), "▶", EditorStyles.miniButtonRight))
                    page++;
            }
        }
    }
}
