using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    internal static class ArchitectorAiWorkUi
    {
        public const float StatusHeight = 30f;

        public static float WorkspaceButtonWidth(ArchitectorAiWorkCoordinator aiWork)
        {
            var interrupted = aiWork?.IsAwaitingAgentResume == true;
            var content = StatusContent(aiWork?.WorkspacesLocked == true, interrupted ? "AI Interrupted" : "AI Working", string.Empty);
            return Mathf.Ceil(CreateStatusStyle(false).CalcSize(content).x) + 2f;
        }

        public static void DrawWorkspaceButton(Rect rect, ArchitectorAiWorkCoordinator aiWork)
        {
            if (aiWork?.IsActive != true)
                return;
            var interrupted = aiWork.IsAwaitingAgentResume;
            DrawStatusControl(rect, aiWork.WorkspacesLocked, interrupted, interrupted ? "AI Interrupted" : "AI Working", interrupted
                ? "Compilation interrupted the confirmed AI session; click to clear the recovered lock"
                : "Toggle workspace editing while the AI session remains active", () =>
            {
                if (interrupted)
                {
                    if (ConfirmInterruptedUnlock())
                        aiWork.AbandonInterruptedSessionByUser();
                    return;
                }
                if (aiWork.WorkspacesLocked)
                {
                    if (ConfirmManualUnlock("Architector workspaces"))
                        aiWork.UnlockWorkspacesByUser();
                }
                else
                {
                    aiWork.RelockWorkspaces();
                }
            });
        }

        public static void DrawSettingsBanner(ArchitectorAiWorkCoordinator aiWork)
        {
            if (aiWork?.IsActive != true)
                return;
            var rect = GUILayoutUtility.GetRect(0f, StatusHeight, GUILayout.ExpandWidth(true));
            rect.x += 4f;
            rect.width = Mathf.Max(0f, rect.width - 8f);
            rect.y += 2f;
            rect.height -= 4f;
            var interrupted = aiWork.IsAwaitingAgentResume;
            DrawStatusControl(rect, aiWork.SettingsLocked, interrupted, interrupted ? "AI Interrupted — Settings" : "AI Working — Settings", interrupted
                ? "Compilation interrupted the confirmed AI session; click to clear the recovered lock"
                : "Toggle Settings editing independently from Architector workspaces", () =>
            {
                if (interrupted)
                {
                    if (ConfirmInterruptedUnlock())
                        aiWork.AbandonInterruptedSessionByUser();
                    return;
                }
                if (aiWork.SettingsLocked)
                {
                    if (ConfirmManualUnlock("Architector Settings"))
                        aiWork.UnlockSettingsByUser();
                }
                else
                {
                    aiWork.RelockSettings();
                }
            }, true);
        }

        public static void ConsumeInputWhenLocked()
        {
            var current = Event.current;
            if (current == null || current.type == EventType.Layout || current.type == EventType.Repaint || current.type == EventType.Used)
                return;
            if (current.isMouse
                || current.isKey
                || current.type == EventType.DragUpdated
                || current.type == EventType.DragPerform
                || current.type == EventType.DragExited
                || current.type == EventType.ValidateCommand
                || current.type == EventType.ExecuteCommand)
                current.Use();
        }

        private static void DrawStatusControl(
            Rect rect,
            bool locked,
            bool interrupted,
            string text,
            string tooltip,
            System.Action toggle,
            bool banner = false)
        {
            var color = interrupted
                ? (EditorGUIUtility.isProSkin ? new Color(0.86f, 0.38f, 0.10f, 0.92f) : new Color(1f, 0.48f, 0.16f, 0.82f))
                : locked
                ? (EditorGUIUtility.isProSkin ? new Color(0.78f, 0.55f, 0.10f, 0.88f) : new Color(1f, 0.76f, 0.20f, 0.78f))
                : (EditorGUIUtility.isProSkin ? new Color(0.22f, 0.62f, 0.30f, 0.88f) : new Color(0.30f, 0.76f, 0.38f, 0.72f));
            if (rect.Contains(Event.current.mousePosition))
                color *= Event.current.type == EventType.MouseDown ? 0.82f : 1.08f;
            color.a = 1f;
            var outline = EditorGUIUtility.isProSkin
                ? new Color(0.12f, 0.12f, 0.12f, 1f)
                : new Color(0.42f, 0.42f, 0.42f, 1f);
            EditorGUI.DrawRect(rect, outline);
            EditorGUI.DrawRect(new Rect(rect.x + 1f, rect.y + 1f, Mathf.Max(0f, rect.width - 2f), Mathf.Max(0f, rect.height - 2f)), color);
            var content = StatusContent(locked, text, tooltip);
            if (GUI.Button(rect, content, CreateStatusStyle(banner)))
                toggle();
        }

        private static GUIContent StatusContent(bool locked, string text, string tooltip)
        {
            var icon = EditorGUIUtility.IconContent(locked ? "IN LockButton on" : "IN LockButton").image;
            return new GUIContent(text, icon, tooltip);
        }

        private static GUIStyle CreateStatusStyle(bool banner)
        {
            var style = new GUIStyle(GUIStyle.none)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                imagePosition = ImagePosition.ImageLeft,
                padding = banner ? new RectOffset(8, 8, 2, 2) : new RectOffset(7, 7, 2, 2),
                normal = { textColor = EditorGUIUtility.isProSkin ? Color.white : new Color(0.12f, 0.12f, 0.12f) },
                hover = { textColor = EditorGUIUtility.isProSkin ? Color.white : new Color(0.12f, 0.12f, 0.12f) },
                active = { textColor = EditorGUIUtility.isProSkin ? Color.white : new Color(0.12f, 0.12f, 0.12f) }
            };
            return style;
        }

        private static bool ConfirmManualUnlock(string target)
        {
            return EditorUtility.DisplayDialog(
                "AI Work Is In Progress",
                $"An AI agent is currently working with Architector. Unlocking {target} allows concurrent manual changes and can cause revision conflicts, invalidate the agent's plan, or produce consequences that cannot be fully recovered through the current session Undo/Redo history.\n\nThe AI session will continue running.",
                "Unlock Anyway",
                "Keep Locked");
        }

        private static bool ConfirmInterruptedUnlock()
        {
            return EditorUtility.DisplayDialog(
                "AI Work Was Interrupted",
                "Unity recompiled scripts while an AI work session was active. Architector restored the safety lock, but the agent has not yet resumed with its work token.\n\nForce unlocking ends this recovered AI session completely. If the agent is still running, its following mutation will be rejected and it must start a new session.",
                "End Session And Unlock",
                "Keep Locked");
        }
    }
}
