using System;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorGroupLockWindow : EditorWindow
    {
        private string groupId = string.Empty;
        private bool unlocking;
        private string password = string.Empty;
        private string confirmation = string.Empty;
        private string notice = ArchitectorGroupLocks.DefaultNotice;
        private string error = string.Empty;
        private Vector2 noticeScroll;
        private bool focusPassword = true;
        private bool keepUnlocked;

        public static void ShowLock(string groupId)
        {
            Open(groupId, false);
        }

        public static void ShowUnlock(string groupId)
        {
            Open(groupId, true);
        }

        private static void Open(string groupId, bool unlocking)
        {
            EditorApplication.delayCall += () =>
            {
                var window = CreateInstance<ArchitectorGroupLockWindow>();
                window.groupId = groupId;
                window.unlocking = unlocking;
                window.titleContent = new GUIContent(unlocking ? "Unlock Group" : "Lock Group");
                var size = new Vector2(420f, unlocking ? 170f : 250f);
                var anchor = EditorGUIUtility.GetMainWindowPosition().center - size * 0.5f;
                window.ShowAsDropDown(new Rect(anchor, Vector2.zero), size);
                window.Focus();
            };
        }

        private void OnGUI()
        {
            if (!ArchitectorSession.TryGet(out var session, out var loadError))
            {
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
                return;
            }
            var repository = session.Repository;
            var group = repository.FindGroup(groupId);
            if (group == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField(unlocking ? $"Unlock {repository.GetEntryPath(groupId)}" : $"Lock {repository.GetEntryPath(groupId)}", EditorStyles.boldLabel);
            if (unlocking)
                EditorGUILayout.HelpBox(group.lockState?.notice ?? ArchitectorGroupLocks.DefaultNotice, MessageType.Warning);
            GUI.SetNextControlName("ArchitectorLockPassword");
            password = EditorGUILayout.PasswordField("Password", password);
            if (focusPassword)
            {
                EditorGUI.FocusTextInControl("ArchitectorLockPassword");
                Repaint();
                if (Event.current.type == EventType.Repaint)
                    focusPassword = false;
            }
            if (unlocking)
                keepUnlocked = EditorGUILayout.ToggleLeft("Keep unlocked until Unity restarts", keepUnlocked);
            if (!unlocking)
            {
                confirmation = EditorGUILayout.PasswordField("Confirm Password", confirmation);
                EditorGUILayout.LabelField("Notice shown to people and agents");
                var noticeStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
                noticeScroll = EditorGUILayout.BeginScrollView(noticeScroll, GUILayout.Height(88f));
                GUI.SetNextControlName("ArchitectorLockNotice");
                notice = EditorGUILayout.TextArea(notice, noticeStyle, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
            if (Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && (unlocking || GUI.GetNameOfFocusedControl() != "ArchitectorLockNotice"))
            {
                Event.current.Use();
                Submit(repository);
                return;
            }
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Event.current.Use();
                Close();
                return;
            }
            if (!string.IsNullOrEmpty(error))
                EditorGUILayout.HelpBox(error, MessageType.Error);

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(90f)))
                    Close();
                if (GUILayout.Button(unlocking ? "Unlock" : "Lock", GUILayout.Width(90f)))
                    Submit(repository);
            }
        }

        private void Submit(ArchitectorRepository repository)
        {
            try
            {
                if (unlocking)
                {
                    if (!repository.UnlockGroupForSession(groupId, password, keepUnlocked))
                    {
                        error = "The password is incorrect.";
                        return;
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        error = "Enter a password.";
                        return;
                    }
                    if (!string.Equals(password, confirmation, StringComparison.Ordinal))
                    {
                        error = "The passwords do not match.";
                        return;
                    }
                    repository.LockGroup(groupId, password, notice);
                }
                Close();
            }
            catch (ArchitectorDataException exception)
            {
                error = exception.Message;
            }
        }
    }
}
