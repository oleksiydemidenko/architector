using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Architector.Editor
{
    public sealed partial class ArchitectorEdgeRouter
    {
        public ArchitectorRouteNetwork RouteMerged(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests,
            ArchitectorRelationshipMerging merging,
            CancellationToken cancellation = default)
        {
            this.cancellation = cancellation;
            if (merging == ArchitectorRelationshipMerging.Off)
                return IndividualNetwork(nodes, requests);

            var members = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var physicalRequests = new List<ArchitectorRouteRequest>();
            foreach (var group in requests.GroupBy(request =>
                         (request.SourceNodeId, request.TargetNodeId, request.Kind, request.SourceSide, request.TargetSide)))
            {
                var ordered = group.OrderBy(request => request.Id, StringComparer.Ordinal).ToArray();
                physicalRequests.Add(ordered[0]);
                members[ordered[0].Id] = ordered.Select(request => request.Id).ToArray();
            }
            var work = WorkRoutes(nodes, physicalRequests);
            var obstacles = nodes.Values.ToArray();
            BuildGrid(obstacles, work);
            ChooseSides(work);
            var minimum = merging == ArchitectorRelationshipMerging.ThreeOrMore ? 3 : 2;
            var trees = work.Where(route => route.Request.SourceNodeId != route.Request.TargetNodeId)
                .GroupBy(route => (route.Request.TargetNodeId, route.Target.Side, route.Request.Kind))
                .Where(group => group.Count() > 1 && group.Sum(route => members[route.Id].Length) >= minimum)
                .Select(group => group.OrderBy(route => route.Span).ThenBy(route => route.Id, StringComparer.Ordinal).ToList())
                .ToArray();
            var treeByRoute = new Dictionary<string, List<WorkRoute>>(StringComparer.Ordinal);
            var sharedTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in trees)
                foreach (var route in tree)
                {
                    treeByRoute[route.Id] = tree;
                    if (route != tree[0])
                        sharedTargets.Add(route.Id);
                }
            AssignPorts(work, Array.Empty<WorkRoute>(), sharedTargets);
            foreach (var tree in trees)
                foreach (var route in tree)
                    route.Target = tree[0].Target;
            BuildGrid(obstacles, work, true);

            var network = new ArchitectorRouteNetwork();
            var completed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in work)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!completed.Add(route.Id))
                    continue;
                if (treeByRoute.TryGetValue(route.Id, out var tree))
                {
                    if (!GrowRouteTree(tree, members, network))
                        return IndividualNetwork(nodes, requests);
                    foreach (var branch in tree)
                        completed.Add(branch.Id);
                }
                else
                {
                    if (!FindPath(route, new[] { route.Source }, new[] { route.Target }, true))
                        return IndividualNetwork(nodes, requests);
                    network.Strokes.Add(new ArchitectorRouteStroke
                    {
                        Id = route.Id,
                        Kind = route.Request.Kind,
                        EdgeIds = members[route.Id],
                        Points = route.Points,
                        Arrow = true
                    });
                }
            }
            var strokes = network.Strokes.Select(stroke => new WorkRoute(
                new ArchitectorRouteRequest(stroke.Id, stroke.Id + ":from", stroke.Id + ":to", "auto", "auto", stroke.Kind),
                new Rect(stroke.Points[0], Vector2.zero), new Rect(stroke.Points.Last(), Vector2.zero))
            {
                Points = stroke.Points,
                SourceJunction = stroke.JunctionAtStart,
                TargetJunction = !stroke.Arrow
            }).ToList();
            SeparateTracks(strokes, obstacles);
            foreach (var stroke in strokes)
                Clean(stroke.Points);
            foreach (var route in work)
                foreach (var id in members[route.Id])
                {
                    var outgoing = network.Strokes.Where(stroke => Array.IndexOf(stroke.EdgeIds, id) >= 0)
                        .ToDictionary(stroke => stroke.Points[0]);
                    var points = new List<Vector2> { route.Source.Anchor };
                    while (outgoing.TryGetValue(points.Last(), out var stroke))
                    {
                        points.AddRange(stroke.Points.Skip(1));
                        if (stroke.Arrow)
                            break;
                    }
                    Clean(points);
                    network.Routes.Add(id, points);
                }
            return network;
        }

        private bool GrowRouteTree(
            IReadOnlyList<WorkRoute> routes,
            IReadOnlyDictionary<string, string[]> members,
            ArchitectorRouteNetwork network)
        {
            var target = routes[0].Target;
            var root = Cell(IndexOf(xs, target.Stub.x), IndexOf(ys, target.Stub.y));
            var parent = new Dictionary<int, int> { [root] = -1 };
            var pending = routes.ToList();
            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var origins = new List<Terminal>(parent.Count * 4);
                foreach (var cell in parent.Keys.OrderBy(cell => cell))
                    foreach (var side in Sides)
                        origins.Add(new Terminal(side, GridPoint(cell), 0f, 0f));
                var search = new WorkRoute(pending[0].Request, pending[0].TargetRect, pending[0].SourceRect);
                if (!FindPath(search, origins, pending.Select(route => route.Source).ToArray(), true))
                    return false;
                ApplyLoad(search.States, -1);
                var connected = pending.First(route => route.Source.Anchor == search.Target.Anchor && route.Source.Side == search.Target.Side);
                for (var index = search.States.Count - 1; index > 0; index--)
                    parent[search.States[index] >> 2] = search.States[index - 1] >> 2;
                pending.Remove(connected);
            }

            var next = new Dictionary<Vector2, Vector2>();
            var carried = new Dictionary<Vector2, HashSet<string>>();
            foreach (var route in routes)
            {
                var source = route.Source.Anchor;
                var cell = Cell(IndexOf(xs, route.Source.Stub.x), IndexOf(ys, route.Source.Stub.y));
                var point = GridPoint(cell);
                AddTreeStep(next, carried, source, point, members[route.Id]);
                while (parent[cell] >= 0)
                {
                    var nextCell = parent[cell];
                    AddTreeStep(next, carried, GridPoint(cell), GridPoint(nextCell), members[route.Id]);
                    cell = nextCell;
                }
                AddTreeStep(next, carried, GridPoint(root), target.Anchor, members[route.Id]);
            }
            var incoming = new Dictionary<Vector2, int>();
            foreach (var point in next.Values)
                incoming[point] = incoming.GetValueOrDefault(point) + 1;
            foreach (var start in next.Keys.Where(point => incoming.GetValueOrDefault(point) != 1)
                         .OrderBy(point => point.x).ThenBy(point => point.y))
            {
                var points = new List<Vector2> { start };
                var end = next[start];
                points.Add(end);
                while (next.ContainsKey(end) && incoming[end] == 1)
                {
                    end = next[end];
                    points.Add(end);
                }
                Clean(points);
                network.Strokes.Add(new ArchitectorRouteStroke
                {
                    Id = UniqueId("@tree:" + routes[0].Id + ":" + network.Strokes.Count, members.Keys.Concat(network.Strokes.Select(stroke => stroke.Id))),
                    Kind = routes[0].Request.Kind,
                    EdgeIds = carried[start].OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    Points = points,
                    Arrow = end == target.Anchor,
                    JunctionAtStart = incoming.GetValueOrDefault(start) > 1
                });
            }
            var horizontal = new HashSet<int>();
            var vertical = new HashSet<int>();
            foreach (var pair in parent.Where(pair => pair.Value >= 0))
            {
                var from = pair.Key;
                var to = pair.Value;
                var direction = to == from + 1 ? Right : to == from - 1 ? Left : to > from ? Down : Up;
                usage[StepIndex(from % width, from / width, direction)]++;
                var load = direction < Down ? horizontal : vertical;
                load.Add(from);
                load.Add(to);
            }
            foreach (var route in routes)
                (IsHorizontalSide(route.Source.Side) ? horizontal : vertical)
                    .Add(Cell(IndexOf(xs, route.Source.Stub.x), IndexOf(ys, route.Source.Stub.y)));
            (IsHorizontalSide(target.Side) ? horizontal : vertical).Add(root);
            foreach (var cell in horizontal)
                horizontalLoad[cell]++;
            foreach (var cell in vertical)
                verticalLoad[cell]++;
            return true;
        }

        private Vector2 GridPoint(int cell)
        {
            return new Vector2(xs[cell % width], ys[cell / width]);
        }

        private static void AddTreeStep(
            IDictionary<Vector2, Vector2> next,
            IDictionary<Vector2, HashSet<string>> carried,
            Vector2 from,
            Vector2 to,
            IEnumerable<string> members)
        {
            if (from == to)
                return;
            next[from] = to;
            if (!carried.TryGetValue(from, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                carried.Add(from, ids);
            }
            ids.UnionWith(members);
        }

        private ArchitectorRouteNetwork IndividualNetwork(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests)
        {
            var routes = Route(nodes, requests, cancellation: cancellation);
            var network = new ArchitectorRouteNetwork();
            foreach (var request in requests)
                if (routes.TryGetValue(request.Id, out var points))
                {
                    network.Routes[request.Id] = points;
                    network.Strokes.Add(new ArchitectorRouteStroke
                    {
                        Id = request.Id, Kind = request.Kind, EdgeIds = new[] { request.Id }, Points = points, Arrow = true
                    });
                }
            return network;
        }

        public ArchitectorRouteNetwork RouteMovedMerged(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests,
            IReadOnlyCollection<string> movedNodeIds,
            ArchitectorRouteNetwork previous,
            Vector2 delta)
        {
            var moved = new HashSet<string>(movedNodeIds, StringComparer.Ordinal);
            var affected = new HashSet<string>(requests
                .Where(request => moved.Contains(request.SourceNodeId) || moved.Contains(request.TargetNodeId))
                .Select(request => request.Id), StringComparer.Ordinal);
            var travelling = new HashSet<string>(requests
                .Where(request => moved.Contains(request.SourceNodeId) && moved.Contains(request.TargetNodeId))
                .Select(request => request.Id), StringComparer.Ordinal);
            var frozen = previous.Routes.Where(pair => !affected.Contains(pair.Key) || travelling.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var routes = RouteMoved(nodes, requests, movedNodeIds, frozen, delta);
            var network = new ArchitectorRouteNetwork();
            foreach (var route in routes)
                network.Routes.Add(route.Key, route.Value);
            foreach (var stroke in previous.Strokes)
            {
                var remaining = stroke.EdgeIds.Where(id => !affected.Contains(id) || travelling.Contains(id)).ToArray();
                if (remaining.Length > 0)
                    network.Strokes.Add(new ArchitectorRouteStroke
                    {
                        Id = stroke.Id,
                        Kind = stroke.Kind,
                        Points = travelling.Contains(remaining[0]) ? stroke.Points.Select(point => point + delta).ToList() : stroke.Points,
                        EdgeIds = remaining,
                        Arrow = stroke.Arrow,
                        JunctionAtStart = stroke.JunctionAtStart
                    });
            }
            foreach (var request in requests.Where(request => affected.Contains(request.Id) && !travelling.Contains(request.Id)))
                if (routes.TryGetValue(request.Id, out var points))
                    network.Strokes.Add(new ArchitectorRouteStroke
                    {
                        Id = UniqueId("@drag:" + request.Id, network.Strokes.Select(stroke => stroke.Id)),
                        Kind = request.Kind,
                        Points = points,
                        EdgeIds = new[] { request.Id },
                        Arrow = true
                    });
            foreach (var stroke in network.Strokes.Where(stroke => stroke.JunctionAtStart))
            {
                var incoming = network.Strokes.Count(branch => branch.Points.Last() == stroke.Points[0]
                    && branch.EdgeIds.Any(id => Array.IndexOf(stroke.EdgeIds, id) >= 0));
                stroke.JunctionAtStart = incoming > 1;
            }
            return network;
        }

        private static string UniqueId(string prefix, IEnumerable<string> existing)
        {
            var ids = new HashSet<string>(existing, StringComparer.Ordinal);
            while (ids.Contains(prefix))
                prefix += ":";
            return prefix;
        }

    }
}
