using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Architector.Editor
{
    // One rebuild of the type index running on worker threads. The main thread polls Progress and IsCompleted between
    // editor updates, or waits when a caller needs the index at once.
    internal sealed class ArchitectorTypeIndexBuild
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task<ArchitectorTypeIndexBuildResult> task;
        private readonly int total;
        private int done;

        public ArchitectorTypeIndexBuild(int total, Func<ArchitectorTypeIndexBuild, ArchitectorTypeIndexBuildResult> work)
        {
            this.total = total;
            Token = cancellation.Token;
            task = Task.Run(() => work(this), Token);
        }

        public CancellationToken Token { get; }
        public bool IsCompleted => task.IsCompleted;
        public float Progress => total <= 0 ? 1f : Math.Min(1f, Volatile.Read(ref done) / (float)total);

        public ArchitectorTypeIndexBuildResult Result
        {
            get
            {
                Wait();
                return task.Result;
            }
        }

        public void Advance() => Interlocked.Increment(ref done);

        public void Wait()
        {
            try
            {
                task.Wait();
            }
            catch (AggregateException exception) when (exception.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            }
        }

        // Stops the workers and waits briefly for them, so no worker outlives an assembly reload.
        public void Cancel()
        {
            if (task.IsCompleted)
                return;
            cancellation.Cancel();
            try
            {
                task.Wait(2000);
            }
            catch (AggregateException)
            {
            }
        }
    }
}
