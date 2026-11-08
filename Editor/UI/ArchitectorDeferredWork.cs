using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    // Runs heavy work on editor updates after its window was painted, so the window is drawn first and shows that it
    // is loading instead of hanging before its first frame. The work is a routine: each step does one piece and yields
    // its progress (0..1). Steps run in short batches; between batches the bar glides to the new progress, so it moves
    // smoothly even though each piece blocks the editor while it runs. Quiet work draws nothing and keeps the window
    // usable: the old content stays on screen until the work is done.
    public sealed class ArchitectorDeferredWork
    {
        private const double BatchBudgetMs = 30.0;
        private const double GlideSeconds = 0.15;
        private const float GlideRate = 18f;
        private const float MaxFrameStep = 1f / 30f;

        private readonly EditorWindow owner;
        private IEnumerator<float> routine;
        private Action whenDone;
        private bool painted;
        private bool barVisible;
        private bool finished;
        private bool batchedOnce;
        private float target;
        private float shown;
        private double lastTick;
        private double targetChangedAt;
        private double workSeconds;

        public ArchitectorDeferredWork(EditorWindow owner)
        {
            this.owner = owner;
        }

        public bool Pending => routine != null;
        // Pending work that shows a bar and locks the window.
        public bool Blocking => routine != null && !Quiet;
        public bool Quiet { get; private set; }
        public string Label { get; set; } = string.Empty;
        public float Progress => shown;
        // How long the last finished work took, without the pauses between batches; -1 before any finished.
        public double LastSeconds { get; private set; } = -1.0;

        public void Request(string label, Action step, bool quiet = false) => Run(label, Single(step), null, quiet);

        // onDone runs once the work ends: after the bar reached its end, or at once when a step throws, so the window
        // never stays in its loading state.
        public void Run(string label, IEnumerator<float> work, Action onDone = null, bool quiet = false)
        {
            if (routine == null)
            {
                painted = false;
                barVisible = false;
                shown = 0f;
                target = 0f;
                batchedOnce = false;
                workSeconds = 0.0;
            }
            Quiet = quiet;
            routine?.Dispose();
            Label = label;
            routine = work;
            whenDone = onDone;
            finished = false;
            lastTick = targetChangedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            owner.Repaint();
        }

        public void Cancel()
        {
            routine?.Dispose();
            routine = null;
            whenDone = null;
            EditorApplication.update -= Tick;
        }

        // Call in OnGUI when the window already shows usable content and the work may run quietly.
        public void MarkPainted()
        {
            if (Event.current.type == EventType.Repaint)
                painted = true;
        }

        // Call at the top of OnGUI. While work is pending it draws a loading line and swallows input.
        public void DrawStatus()
        {
            if (routine == null)
                return;
            if (Quiet)
            {
                MarkPainted();
                return;
            }
            var rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            rect.x += 4f;
            rect.width = Mathf.Max(0f, rect.width - 8f);
            rect.y += 2f;
            rect.height -= 4f;
            DrawBar(rect);
            ArchitectorAiWorkUi.ConsumeInputWhenLocked();
        }

        // Draws the bar in a rect of the caller's layout; counts as a paint of the window.
        public void DrawBar(Rect rect)
        {
            MarkPainted();
            if (Event.current.type != EventType.Repaint)
                return;
            barVisible = true;
            EditorGUI.ProgressBar(rect, shown, Label + "…");
        }

        private static IEnumerator<float> Single(Action step)
        {
            step();
            yield return 1f;
        }

        private void Tick()
        {
            if (routine == null)
            {
                EditorApplication.update -= Tick;
                return;
            }
            if (owner == null)
            {
                Cancel();
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            // A long step freezes the editor; clamping the frame time keeps the bar from jumping after it.
            var dt = Mathf.Min((float)(now - lastTick), MaxFrameStep);
            lastTick = now;
            if (shown < target)
            {
                shown = Mathf.Min(target, shown + (target - shown) * (1f - Mathf.Exp(-GlideRate * dt)) + 0.25f * dt);
                if (target - shown < 0.005f)
                    shown = target;
                owner.Repaint();
            }

            if (!painted && !Quiet)
                return;
            var gliding = barVisible && shown < target && now - targetChangedAt < GlideSeconds;
            if (gliding)
                return;

            if (finished)
            {
                Complete();
                return;
            }

            painted = false;
            var watch = Stopwatch.StartNew();
            var before = target;
            try
            {
                do
                {
                    if (!routine.MoveNext())
                    {
                        finished = true;
                        target = 1f;
                        break;
                    }
                    target = Mathf.Clamp(Mathf.Max(target, routine.Current), 0f, 1f);
                }
                while (watch.Elapsed.TotalMilliseconds < BatchBudgetMs);
                workSeconds += watch.Elapsed.TotalSeconds;
            }
            catch
            {
                var onDone = whenDone;
                Cancel();
                onDone?.Invoke();
                owner.Repaint();
                throw;
            }

            // Work that was done within the first batch, or quiet work, needs no animation at all.
            if (finished && (!batchedOnce || Quiet))
            {
                Complete();
                return;
            }
            batchedOnce = true;
            if (Quiet)
                return;
            if (target > before)
                targetChangedAt = EditorApplication.timeSinceStartup;
            lastTick = EditorApplication.timeSinceStartup;
            owner.Repaint();
        }

        private void Complete()
        {
            EditorApplication.update -= Tick;
            routine?.Dispose();
            routine = null;
            var onDone = whenDone;
            whenDone = null;
            shown = target = 1f;
            LastSeconds = workSeconds;
            onDone?.Invoke();
            if (owner != null)
                owner.Repaint();
        }
    }
}
