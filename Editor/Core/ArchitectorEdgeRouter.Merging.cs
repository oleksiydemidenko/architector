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
            CancellationToken cancellation = default,
            bool separate = true)
        {
            this.cancellation = cancellation;
            UseCells(nodes);
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
            var trees = GroupTrees(work, members, minimum);
            var treeByRoute = new Dictionary<string, RouteTree>(StringComparer.Ordinal);
            var sharedSources = new HashSet<string>(StringComparer.Ordinal);
            var sharedTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in trees)
            {
                foreach (var route in tree.Routes)
                    treeByRoute[route.Id] = tree;
                foreach (var route in tree.Routes.Where(route => tree.SourcePort(route) != route))
                    sharedSources.Add(route.Id);
                foreach (var route in tree.Routes.Where(route => tree.TargetPort(route) != route))
                    sharedTargets.Add(route.Id);
            }
            AssignPorts(work, Array.Empty<WorkRoute>(), cell, sharedTargets, sharedSources);
            foreach (var tree in trees)
                foreach (var route in tree.Routes)
                {
                    route.Source = tree.SourcePort(route).Source;
                    route.Target = tree.TargetPort(route).Target;
                }
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
                    var paths = tree.Shape == TreeShape.Bus ? GrowBus(tree) : GrowTree(tree.Routes, tree.Shape == TreeShape.FanOut);
                    if (paths == null)
                        return IndividualNetwork(nodes, requests);
                    AddTreeStrokes(network, tree.Routes[0], paths, members);
                    foreach (var branch in tree.Routes)
                        completed.Add(branch.Id);
                }
                else
                {
                    if (!FindSeparatePath(route, new[] { route.Source }, new[] { route.Target }, cell > 0f ? CellCapacity : 1))
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
            if (separate)
                SeparateTracks(strokes, obstacles, cell);
            foreach (var stroke in strokes)
                Clean(stroke.Points);
            foreach (var route in work)
                foreach (var id in members[route.Id])
                {
                    var carrying = network.Strokes.Where(stroke => Array.IndexOf(stroke.EdgeIds, id) >= 0).ToArray();
                    var outgoing = carrying.ToDictionary(stroke => stroke.Points[0]);
                    var ends = new HashSet<Vector2>(carrying.Select(stroke => stroke.Points[stroke.Points.Count - 1]));
                    var points = new List<Vector2> { carrying.First(stroke => !ends.Contains(stroke.Points[0])).Points[0] };
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

        private enum TreeShape
        {
            FanIn,
            FanOut,
            Bus
        }

        private sealed class RouteTree
        {
            public RouteTree(TreeShape shape, List<WorkRoute> routes)
            {
                Shape = shape;
                Routes = routes;
                foreach (var route in routes)
                {
                    sourcePorts.TryAdd(route.Request.SourceNodeId, route);
                    targetPorts.TryAdd(route.Request.TargetNodeId, route);
                }
            }

            private readonly Dictionary<string, WorkRoute> sourcePorts = new(StringComparer.Ordinal);
            private readonly Dictionary<string, WorkRoute> targetPorts = new(StringComparer.Ordinal);

            public TreeShape Shape { get; }
            public List<WorkRoute> Routes { get; }

            public WorkRoute SourcePort(WorkRoute route) =>
                Shape == TreeShape.FanIn ? route : sourcePorts[route.Request.SourceNodeId];

            public WorkRoute TargetPort(WorkRoute route) =>
                Shape == TreeShape.FanOut ? route : targetPorts[route.Request.TargetNodeId];
        }

        private static List<RouteTree> GroupTrees(
            IReadOnlyList<WorkRoute> work,
            IReadOnlyDictionary<string, string[]> members,
            int minimum)
        {
            var remaining = work.Where(route => route.Request.SourceNodeId != route.Request.TargetNodeId).ToList();
            var trees = new List<RouteTree>();
            int Logical(IEnumerable<WorkRoute> routes) => routes.Sum(route => members[route.Id].Length);
            List<WorkRoute> Ordered(IEnumerable<WorkRoute> routes) =>
                routes.OrderBy(route => route.Span).ThenBy(route => route.Id, StringComparer.Ordinal).ToList();

            while (true)
            {
                var bus = FindBus(remaining);
                if (bus == null)
                    break;
                trees.Add(new RouteTree(TreeShape.Bus, Ordered(bus)));
                remaining.RemoveAll(bus.Contains);
            }
            while (true)
            {
                var fanIn = remaining
                    .GroupBy(route => (node: route.Request.TargetNodeId, side: route.Target.Side, kind: route.Request.Kind))
                    .Select(group => (shape: TreeShape.FanIn, key: $"{group.Key.node}\u001f{group.Key.side}\u001f{group.Key.kind}", routes: group.ToList()));
                var fanOut = remaining
                    .GroupBy(route => (node: route.Request.SourceNodeId, side: route.Source.Side, kind: route.Request.Kind))
                    .Select(group => (shape: TreeShape.FanOut, key: $"{group.Key.node}\u001f{group.Key.side}\u001f{group.Key.kind}", routes: group.ToList()));
                var best = fanIn.Concat(fanOut)
                    .Where(group => group.routes.Count > 1 && Logical(group.routes) >= minimum)
                    .OrderByDescending(group => Logical(group.routes))
                    .ThenBy(group => group.shape)
                    .ThenBy(group => group.key, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (best.routes == null)
                    break;
                trees.Add(new RouteTree(best.shape, Ordered(best.routes)));
                remaining.RemoveAll(best.routes.Contains);
            }
            return trees;
        }

        private static List<WorkRoute> FindBus(IReadOnlyList<WorkRoute> routes)
        {
            List<WorkRoute> best = null;
            foreach (var kind in routes.GroupBy(route => route.Request.Kind).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var edges = new Dictionary<(string source, string target), WorkRoute>();
                var sourceSides = new Dictionary<string, string>(StringComparer.Ordinal);
                var targetSides = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var route in kind.OrderBy(route => route.Id, StringComparer.Ordinal))
                {
                    var source = route.Request.SourceNodeId;
                    var target = route.Request.TargetNodeId;
                    if (sourceSides.TryGetValue(source, out var sourceSide) && sourceSide != route.Source.Side
                        || targetSides.TryGetValue(target, out var targetSide) && targetSide != route.Target.Side)
                        continue;
                    sourceSides[source] = route.Source.Side;
                    targetSides[target] = route.Target.Side;
                    edges.TryAdd((source, target), route);
                }
                var targets = edges.Keys.Select(edge => edge.target).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
                var sourcesByTarget = targets.ToDictionary(
                    target => target,
                    target => edges.Keys.Where(edge => edge.target == target).Select(edge => edge.source).ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);
                for (var first = 0; first < targets.Length; first++)
                    for (var second = first + 1; second < targets.Length; second++)
                    {
                        var sources = sourcesByTarget[targets[first]]
                            .Where(sourcesByTarget[targets[second]].Contains)
                            .ToArray();
                        if (sources.Length < 2)
                            continue;
                        var busTargets = targets
                            .Where(target => !sources.Contains(target) && sources.All(sourcesByTarget[target].Contains))
                            .ToArray();
                        var size = sources.Length * busTargets.Length;
                        if (best != null && size <= best.Count)
                            continue;
                        best = sources.SelectMany(source => busTargets.Select(target => edges[(source, target)])).ToList();
                    }
            }
            return best;
        }

        private List<(string id, List<Vector2> points)> GrowTree(IReadOnlyList<WorkRoute> routes, bool fanOut)
        {
            var root = fanOut ? routes[0].Source : routes[0].Target;
            var parent = new Dictionary<int, int> { [StubCell(root)] = -1 };
            if (!GrowCells(routes, route => fanOut ? route.Target : route.Source, parent))
                return null;
            CommitTreeLoad(parent, routes.Select(route => fanOut ? route.Target : route.Source).Append(root));
            return routes.Select(route =>
            {
                var cells = ChainToRoot(StubCell(fanOut ? route.Target : route.Source), parent);
                if (fanOut)
                    cells.Reverse();
                var points = new List<Vector2> { route.Source.Anchor };
                points.AddRange(cells.Select(GridPoint));
                points.Add(route.Target.Anchor);
                return (route.Id, points);
            }).ToList();
        }

        private List<(string id, List<Vector2> points)> GrowBus(RouteTree bus)
        {
            var first = bus.Routes[0];
            var root = first.Target;
            var leaves = bus.Routes
                .Where(route => route.Request.TargetNodeId == first.Request.TargetNodeId)
                .ToList();
            var parent = new Dictionary<int, int> { [StubCell(root)] = -1 };
            if (!GrowCells(leaves, route => route.Source, parent))
                return null;
            CommitTreeLoad(parent, leaves.Select(route => route.Source).Append(root));

            var chains = leaves.ToDictionary(
                route => route.Request.SourceNodeId,
                route => ChainToRoot(StubCell(route.Source), parent),
                StringComparer.Ordinal);
            var trunk = chains.Values.Skip(1).Aggregate(
                chains.Values.First().ToHashSet(),
                (shared, chain) => { shared.IntersectWith(chain); return shared; });
            var owners = new Dictionary<int, string>();
            var branches = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var target in bus.Routes
                         .Where(route => route.Request.TargetNodeId != first.Request.TargetNodeId)
                         .GroupBy(route => route.Request.TargetNodeId)
                         .Select(group => group.First()))
            {
                cancellation.ThrowIfCancellationRequested();
                var origins = new List<Terminal>((trunk.Count + owners.Count) * 4);
                foreach (var cell in trunk.Concat(owners.Keys).OrderBy(cell => cell))
                    foreach (var side in Sides)
                        origins.Add(new Terminal(side, GridPoint(cell), 0f, 0f));
                var search = new WorkRoute(target.Request, target.SourceRect, target.TargetRect);
                var closed = BlockCells(parent.Keys.Where(cell => !trunk.Contains(cell)));
                var found = FindSeparatePath(search, origins, new[] { target.Target });
                foreach (var step in closed)
                    blocked[step] = false;
                if (!found)
                    return null;
                var cells = search.States.Select(state => state >> 2).ToList();
                var start = cells[0];
                var branch = trunk.Contains(start)
                    ? new List<int>()
                    : branches[owners[start]].TakeWhile(cell => cell != start).ToList();
                branch.AddRange(cells);
                branches[target.Request.TargetNodeId] = branch;
                foreach (var cell in cells.Skip(1))
                    owners.TryAdd(cell, target.Request.TargetNodeId);
            }

            return bus.Routes.Select(route =>
            {
                var chain = chains[route.Request.SourceNodeId];
                var points = new List<Vector2> { route.Source.Anchor };
                if (route.Request.TargetNodeId == first.Request.TargetNodeId)
                    points.AddRange(chain.Select(GridPoint));
                else
                {
                    var branch = branches[route.Request.TargetNodeId];
                    points.AddRange(chain.TakeWhile(cell => cell != branch[0]).Select(GridPoint));
                    points.AddRange(branch.Select(GridPoint));
                }
                points.Add(route.Target.Anchor);
                return (route.Id, points);
            }).ToList();
        }

        private bool FindSeparatePath(WorkRoute route, IReadOnlyList<Terminal> sources, IReadOnlyList<Terminal> targets, int capacity = 1) =>
            FindPath(route, sources, targets, true, capacity) || FindPath(route, sources, targets);

        private int StubCell(Terminal terminal) => Cell(IndexOf(xs, terminal.Stub.x), IndexOf(ys, terminal.Stub.y));

        private static List<int> ChainToRoot(int cell, IReadOnlyDictionary<int, int> parent)
        {
            var cells = new List<int> { cell };
            while (parent[cell] >= 0)
            {
                cell = parent[cell];
                cells.Add(cell);
            }
            return cells;
        }

        private bool GrowCells(IReadOnlyList<WorkRoute> routes, Func<WorkRoute, Terminal> leaf, IDictionary<int, int> parent)
        {
            var pending = routes.ToList();
            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var origins = new List<Terminal>(parent.Count * 4);
                foreach (var cell in parent.Keys.OrderBy(cell => cell))
                    foreach (var side in Sides)
                        origins.Add(new Terminal(side, GridPoint(cell), 0f, 0f));
                var search = new WorkRoute(pending[0].Request, pending[0].TargetRect, pending[0].SourceRect);
                if (!FindSeparatePath(search, origins, pending.Select(leaf).ToArray()))
                    return false;
                ApplyLoad(search.States, -1);
                var connected = pending.First(route => leaf(route).Anchor == search.Target.Anchor && leaf(route).Side == search.Target.Side);
                for (var index = search.States.Count - 1; index > 0; index--)
                    parent[search.States[index] >> 2] = search.States[index - 1] >> 2;
                pending.Remove(connected);
            }
            return true;
        }

        private void CommitTreeLoad(IReadOnlyDictionary<int, int> parent, IEnumerable<Terminal> ends)
        {
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
            foreach (var end in ends)
                (IsHorizontalSide(end.Side) ? horizontal : vertical).Add(StubCell(end));
            foreach (var cell in horizontal)
                horizontalLoad[cell]++;
            foreach (var cell in vertical)
                verticalLoad[cell]++;
        }

        private List<int> BlockCells(IEnumerable<int> cells)
        {
            var closed = new List<int>();
            void Block(int step)
            {
                if (step < 0 || step >= blocked.Length || blocked[step])
                    return;
                blocked[step] = true;
                closed.Add(step);
            }
            foreach (var cell in cells)
            {
                var x = cell % width;
                var y = cell / width;
                if (x + 1 < width)
                    Block(cell * 2);
                if (x > 0)
                    Block((cell - 1) * 2);
                if (y + 1 < height)
                    Block(cell * 2 + 1);
                if (y > 0)
                    Block((cell - width) * 2 + 1);
            }
            return closed;
        }

        private static void AddTreeStrokes(
            ArchitectorRouteNetwork network,
            WorkRoute first,
            IReadOnlyList<(string id, List<Vector2> points)> paths,
            IReadOnlyDictionary<string, string[]> members)
        {
            var carried = new Dictionary<(Vector2 from, Vector2 to), SortedSet<string>>();
            var outgoing = new Dictionary<Vector2, List<Vector2>>();
            var previous = new Dictionary<Vector2, List<Vector2>>();
            foreach (var (id, points) in paths)
                for (var index = 1; index < points.Count; index++)
                {
                    var segment = (points[index - 1], points[index]);
                    if (segment.Item1 == segment.Item2)
                        continue;
                    if (!carried.TryGetValue(segment, out var ids))
                    {
                        ids = new SortedSet<string>(StringComparer.Ordinal);
                        carried.Add(segment, ids);
                        if (!outgoing.TryGetValue(segment.Item1, out var next))
                            outgoing.Add(segment.Item1, next = new List<Vector2>());
                        next.Add(segment.Item2);
                        if (!previous.TryGetValue(segment.Item2, out var back))
                            previous.Add(segment.Item2, back = new List<Vector2>());
                        back.Add(segment.Item1);
                    }
                    ids.UnionWith(members[id]);
                }

            int Degree(Vector2 point) =>
                (outgoing.TryGetValue(point, out var next) ? next.Count : 0)
                + (previous.TryGetValue(point, out var back) ? back.Count : 0);
            bool PassesThrough(Vector2 point) =>
                previous.TryGetValue(point, out var back) && back.Count == 1
                && outgoing.TryGetValue(point, out var next) && next.Count == 1
                && carried[(back[0], point)].SetEquals(carried[(point, next[0])]);

            foreach (var segment in carried.Keys
                         .Where(segment => !PassesThrough(segment.from))
                         .OrderBy(segment => segment.from.x).ThenBy(segment => segment.from.y)
                         .ThenBy(segment => segment.to.x).ThenBy(segment => segment.to.y)
                         .ToArray())
            {
                var points = new List<Vector2> { segment.from, segment.to };
                var end = segment.to;
                while (PassesThrough(end))
                {
                    end = outgoing[end][0];
                    points.Add(end);
                }
                Clean(points);
                network.Strokes.Add(new ArchitectorRouteStroke
                {
                    Id = UniqueId("@tree:" + first.Id + ":" + network.Strokes.Count, members.Keys.Concat(network.Strokes.Select(stroke => stroke.Id))),
                    Kind = first.Request.Kind,
                    EdgeIds = carried[segment].ToArray(),
                    Points = points,
                    Arrow = !outgoing.ContainsKey(end),
                    JunctionAtStart = Degree(segment.from) >= 3
                });
            }
        }

        private Vector2 GridPoint(int cell)
        {
            return new Vector2(xs[cell % width], ys[cell / width]);
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
                stroke.JunctionAtStart = network.IsJunction(stroke, _ => true);
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
