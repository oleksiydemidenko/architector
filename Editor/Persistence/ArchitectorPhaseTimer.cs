using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Architector.Editor
{
    internal sealed class ArchitectorPhaseTimer
    {
        private readonly Stopwatch stopwatch = new();
        private Dictionary<string, double> current = new(StringComparer.Ordinal);
        private double previous;

        public IReadOnlyDictionary<string, double> Last { get; private set; } = new Dictionary<string, double>(StringComparer.Ordinal);

        public void Start()
        {
            current = new Dictionary<string, double>(StringComparer.Ordinal);
            Last = current;
            previous = 0d;
            stopwatch.Restart();
        }

        public void EnsureStarted()
        {
            if (!stopwatch.IsRunning)
                Start();
        }

        public void Stop()
        {
            stopwatch.Stop();
        }

        public void Lap(string phase)
        {
            if (!stopwatch.IsRunning)
                return;
            var now = stopwatch.Elapsed.TotalMilliseconds;
            current[phase] = (current.TryGetValue(phase, out var earlier) ? earlier : 0d) + now - previous;
            previous = now;
        }
    }
}
