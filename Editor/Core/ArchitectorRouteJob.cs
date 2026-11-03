using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorRouteJob
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task task;

        public ArchitectorRouteJob(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests,
            float cornerRadius,
            float bridgeRadius,
            ArchitectorRelationshipMerging merging = ArchitectorRelationshipMerging.Off,
            float cellSize = 0f)
        {
            var token = cancellation.Token;
            task = Task.Run(() =>
            {
                var router = new ArchitectorEdgeRouter { CellSize = cellSize };
                var network = merging == ArchitectorRelationshipMerging.Off ? null : router.RouteMerged(nodes, requests, merging, token);
                var routes = network?.Routes ?? router.Route(nodes, requests, true, token);
                var drawn = network == null ? ArchitectorEdgeRouter.Decorate(routes, cornerRadius, bridgeRadius) : network.Decorate(cornerRadius, bridgeRadius);
                Network = network;
                token.ThrowIfCancellationRequested();
                Routes = routes;
                DrawnRoutes = drawn;
            }, token);
        }

        public bool IsCompleted => task.IsCompleted;
        public bool Succeeded => task.Status == TaskStatus.RanToCompletion;
        public Exception Failure => task.IsFaulted ? task.Exception?.GetBaseException() : null;
        public ArchitectorRouteNetwork Network { get; private set; }
        public Dictionary<string, List<Vector2>> Routes { get; private set; }
        public Dictionary<string, List<Vector2>> DrawnRoutes { get; private set; }

        public void Cancel()
        {
            cancellation.Cancel();
        }

        public bool Wait(int milliseconds)
        {
            return ((IAsyncResult)task).AsyncWaitHandle.WaitOne(milliseconds);
        }
    }
}
