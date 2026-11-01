using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Architector.Editor
{
    public readonly struct ArchitectorRouteRequest
    {
        public ArchitectorRouteRequest(string id, string sourceNodeId, string targetNodeId, string sourceSide, string targetSide)
        {
            Id = id;
            SourceNodeId = sourceNodeId;
            TargetNodeId = targetNodeId;
            SourceSide = sourceSide;
            TargetSide = targetSide;
        }

        public string Id { get; }
        public string SourceNodeId { get; }
        public string TargetNodeId { get; }
        public string SourceSide { get; }
        public string TargetSide { get; }
    }

    public sealed class ArchitectorEdgeRouter
    {
        public const float Clearance = 20f;
        private const float OuterMargin = 48f;
        private const float BendCost = 64f;
        private const float CrossingCost = 64f;
        private const float OverlapCost = 0.1f;
        private const int OverlapLimit = 4;
        private const float HeuristicWeight = 1.001f;
        private const float SecondarySideCost = 24f;
        private const float SideLoadCost = 8f;
        private const float PortSpacing = 14f;
        private const float PortMargin = 10f;
        private const float TrackSpacing = 10f;
        private const float MinimumSegment = 6f;
        private const float NodeMargin = 5f;
        private const int CornerSteps = 4;
        private const int BridgeSteps = 3;
        private const float MinimumCorner = 2f;
        private const int UntangleAttempts = 6;
        private const float BridgeEndMargin = 2f;
        private const float BridgeMergeGap = 6f;
        private const float BridgeArrowMargin = 14f;
        private const int Right = 0;
        private const int Left = 1;
        private const int Down = 2;
        private const int Up = 3;
        private static readonly string[] Sides = { "right", "left", "bottom", "top" };

        private float[] xs = Array.Empty<float>();
        private float[] ys = Array.Empty<float>();
        private int width;
        private int height;
        private bool[] blocked = Array.Empty<bool>();
        private int[] usage = Array.Empty<int>();
        private int[] horizontalLoad = Array.Empty<int>();
        private int[] verticalLoad = Array.Empty<int>();
        private float[] costs = Array.Empty<float>();
        private int[] parents = Array.Empty<int>();
        private int[] seen = Array.Empty<int>();
        private int[] closed = Array.Empty<int>();
        private int stamp;
        private float[] heapKeys = new float[256];
        private int[] heapStates = new int[256];
        private int heapCount;
        private int arrivalIndex;
        private CancellationToken cancellation;

        public Dictionary<string, List<Vector2>> Route(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests,
            bool untangle = true,
            CancellationToken cancellation = default)
        {
            this.cancellation = cancellation;
            return Solve(nodes, WorkRoutes(nodes, requests), Array.Empty<WorkRoute>(), untangle);
        }

        public Dictionary<string, List<Vector2>> RouteMoved(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests,
            IReadOnlyCollection<string> movedNodeIds,
            IReadOnlyDictionary<string, List<Vector2>> previousRoutes,
            Vector2 delta)
        {
            cancellation = CancellationToken.None;
            var moved = new HashSet<string>(movedNodeIds, StringComparer.Ordinal);
            var work = new List<WorkRoute>();
            var frozen = new List<WorkRoute>();
            foreach (var route in WorkRoutes(nodes, requests))
            {
                var sourceMoved = moved.Contains(route.Request.SourceNodeId);
                var targetMoved = moved.Contains(route.Request.TargetNodeId);
                if (sourceMoved != targetMoved || !previousRoutes.TryGetValue(route.Id, out var previous))
                {
                    work.Add(route);
                    continue;
                }
                var offset = sourceMoved ? delta : Vector2.zero;
                route.Freeze(previous.Select(point => point + offset).ToList());
                frozen.Add(route);
            }
            return Solve(nodes, work, frozen, false);
        }

        public static Dictionary<string, List<Vector2>> Decorate(
            IReadOnlyDictionary<string, List<Vector2>> routes,
            float cornerRadius,
            float bridgeRadius)
        {
            var legs = routes.ToDictionary(route => route.Key, route => Legs(route.Key, route.Value), StringComparer.Ordinal);
            var verticals = SortedVerticals(legs.Values);
            foreach (var horizontal in legs.Values.SelectMany(route => route).Where(leg => leg.Horizontal))
            {
                foreach (var vertical in Crossed(verticals, horizontal))
                    ChooseHop(horizontal, vertical, cornerRadius, bridgeRadius);
            }

            var result = new Dictionary<string, List<Vector2>>(routes.Count, StringComparer.Ordinal);
            foreach (var route in routes)
                result[route.Key] = Drawn(route.Value, legs[route.Key], cornerRadius, bridgeRadius);
            return result;
        }

        public static Dictionary<string, int> CrossingCounts(IReadOnlyDictionary<string, List<Vector2>> routes)
        {
            var counts = routes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
            var legs = routes.Select(route => Legs(route.Key, route.Value)).ToList();
            var verticals = SortedVerticals(legs);
            foreach (var horizontal in legs.SelectMany(route => route).Where(leg => leg.Horizontal))
            {
                foreach (var vertical in Crossed(verticals, horizontal))
                {
                    counts[horizontal.Id]++;
                    counts[vertical.Id]++;
                }
            }
            return counts;
        }

        public static Vector2 Anchor(Rect rect, string side)
        {
            return side switch
            {
                "left" => new Vector2(rect.xMin, rect.center.y),
                "right" => new Vector2(rect.xMax, rect.center.y),
                "top" => new Vector2(rect.center.x, rect.yMin),
                _ => new Vector2(rect.center.x, rect.yMax)
            };
        }

        public static Vector2 SideDirection(string side)
        {
            return DirectionVector(DirectionOf(side));
        }

        private static List<WorkRoute> WorkRoutes(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests)
        {
            return requests
                .Where(request => nodes.ContainsKey(request.SourceNodeId) && nodes.ContainsKey(request.TargetNodeId))
                .Select(request => new WorkRoute(request, nodes[request.SourceNodeId], nodes[request.TargetNodeId]))
                .OrderBy(route => route.Span)
                .ThenBy(route => route.Id, StringComparer.Ordinal)
                .ToList();
        }

        private Dictionary<string, List<Vector2>> Solve(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<WorkRoute> routes,
            IReadOnlyList<WorkRoute> frozen,
            bool untangle)
        {
            var obstacles = nodes.Values.ToArray();
            BuildGrid(obstacles, routes);
            LoadFrozen(frozen);
            ChooseSides(routes);
            AssignPorts(routes, frozen);
            BuildGrid(obstacles, routes);
            LoadFrozen(frozen);
            foreach (var route in routes)
                RoutePorts(route);
            foreach (var route in routes)
            {
                if (!CrossesOthers(route))
                    continue;
                Release(route);
                RoutePorts(route);
            }
            var all = routes.Concat(frozen).ToList();
            if (untangle)
                Untangle(routes, obstacles);
            else
                SeparateTracks(all, obstacles);
            var result = new Dictionary<string, List<Vector2>>(StringComparer.Ordinal);
            foreach (var route in all)
            {
                Clean(route.Points);
                result[route.Id] = route.Points;
            }
            return result;
        }

        private void LoadFrozen(IReadOnlyList<WorkRoute> frozen)
        {
            foreach (var route in frozen)
            {
                for (var index = 1; index < route.Points.Count; index++)
                {
                    var start = route.Points[index - 1];
                    var end = route.Points[index];
                    if (IsHorizontalSegment(start, end))
                    {
                        var row = IndexOf(ys, start.y);
                        var last = IndexOf(xs, Mathf.Max(start.x, end.x));
                        for (var x = IndexOf(xs, Mathf.Min(start.x, end.x)); x <= last; x++)
                        {
                            horizontalLoad[Cell(x, row)]++;
                            if (x < last)
                                usage[Cell(x, row) * 2]++;
                        }
                    }
                    else
                    {
                        var column = IndexOf(xs, start.x);
                        var last = IndexOf(ys, Mathf.Max(start.y, end.y));
                        for (var y = IndexOf(ys, Mathf.Min(start.y, end.y)); y <= last; y++)
                        {
                            verticalLoad[Cell(column, y)]++;
                            if (y < last)
                                usage[Cell(column, y) * 2 + 1]++;
                        }
                    }
                }
            }
        }

        private static bool IsHorizontalSegment(Vector2 start, Vector2 end)
        {
            return Mathf.Abs(start.y - end.y) < 0.01f && Mathf.Abs(start.x - end.x) > 1f;
        }

        private static List<Leg> Legs(string id, IReadOnlyList<Vector2> route)
        {
            var legs = new List<Leg>(route.Count);
            for (var index = 1; index < route.Count; index++)
                legs.Add(new Leg(id, route[index - 1], route[index], index == 1, index + 1 == route.Count));
            return legs;
        }

        private static List<Leg> SortedVerticals(IEnumerable<List<Leg>> routes)
        {
            var verticals = routes.SelectMany(route => route).Where(leg => leg.Vertical).ToList();
            verticals.Sort((left, right) => left.Across.CompareTo(right.Across));
            return verticals;
        }

        private static IEnumerable<Leg> Crossed(List<Leg> verticals, Leg horizontal)
        {
            var low = 0;
            var high = verticals.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (verticals[middle].Across <= horizontal.Min)
                    low = middle + 1;
                else
                    high = middle;
            }
            for (var index = low; index < verticals.Count && verticals[index].Across < horizontal.Max; index++)
            {
                var vertical = verticals[index];
                if (vertical.Id != horizontal.Id && horizontal.Across > vertical.Min && horizontal.Across < vertical.Max)
                    yield return vertical;
            }
        }

        private static void ChooseHop(Leg horizontal, Leg vertical, float cornerRadius, float bridgeRadius)
        {
            var horizontalRoom = horizontal.Room(vertical.Across, bridgeRadius);
            var verticalRoom = vertical.Room(horizontal.Across, bridgeRadius);
            if (horizontalRoom >= cornerRadius - MinimumCorner || horizontalRoom >= 0f && horizontalRoom >= verticalRoom)
                horizontal.Hops.Add(vertical.Across);
            else if (verticalRoom >= 0f)
                vertical.Hops.Add(horizontal.Across);
        }

        private static List<Vector2> Drawn(IReadOnlyList<Vector2> points, IReadOnlyList<Leg> legs, float cornerRadius, float bridgeRadius)
        {
            var result = new List<Vector2>(points.Count * (CornerSteps + 1));
            if (points.Count == 0)
                return result;
            result.Add(points[0]);
            var spans = legs.Select(leg => leg.Spans(bridgeRadius)).ToArray();
            for (var index = 0; index < legs.Count; index++)
            {
                foreach (var span in spans[index])
                    AddBridge(result, legs[index], span.from, span.to, bridgeRadius);
                if (index + 1 == legs.Count)
                    continue;
                var before = legs[index].Length * 0.5f;
                var after = legs[index + 1].Length * 0.5f;
                if (spans[index].Count > 0)
                    before = Mathf.Min(before, Mathf.Abs(legs[index].End - spans[index][spans[index].Count - 1].to) - 1f);
                if (spans[index + 1].Count > 0)
                    after = Mathf.Min(after, Mathf.Abs(spans[index + 1][0].from - legs[index + 1].Start) - 1f);
                AddCorner(result, points[index], points[index + 1], points[index + 2], Mathf.Min(cornerRadius, before, after));
            }
            if (points.Count > 1)
                result.Add(points[points.Count - 1]);
            return result;
        }

        private static void AddCorner(List<Vector2> route, Vector2 previous, Vector2 corner, Vector2 next, float radius)
        {
            if (radius < 0.5f)
            {
                route.Add(corner);
                return;
            }
            var entry = corner - (corner - previous).normalized * radius;
            var exit = corner + (next - corner).normalized * radius;
            for (var step = 0; step <= CornerSteps; step++)
            {
                var t = step / (float)CornerSteps;
                var u = 1f - t;
                route.Add(u * u * entry + 2f * u * t * corner + t * t * exit);
            }
        }

        private static void AddBridge(List<Vector2> route, Leg leg, float from, float to, float radius)
        {
            var direction = to > from ? 1f : -1f;
            for (var step = 0; step <= BridgeSteps; step++)
            {
                var angle = step * Mathf.PI * 0.5f / BridgeSteps;
                route.Add(leg.Point(from + direction * radius * (1f - Mathf.Cos(angle)), radius * Mathf.Sin(angle)));
            }
            for (var step = BridgeSteps; step >= 0; step--)
            {
                var angle = step * Mathf.PI * 0.5f / BridgeSteps;
                route.Add(leg.Point(to - direction * radius * (1f - Mathf.Cos(angle)), radius * Mathf.Sin(angle)));
            }
        }

        private static List<Vector2> DirectRoute(Terminal source, Terminal target)
        {
            var sourceOut = source.Stub;
            var targetOut = target.Stub;
            var route = new List<Vector2> { source.Anchor, sourceOut };
            var sourceHorizontal = IsHorizontalSide(source.Side);
            var targetHorizontal = IsHorizontalSide(target.Side);
            if (sourceHorizontal && targetHorizontal)
            {
                var middleX = (sourceOut.x + targetOut.x) * 0.5f;
                route.Add(new Vector2(middleX, sourceOut.y));
                route.Add(new Vector2(middleX, targetOut.y));
            }
            else if (!sourceHorizontal && !targetHorizontal)
            {
                var middleY = (sourceOut.y + targetOut.y) * 0.5f;
                route.Add(new Vector2(sourceOut.x, middleY));
                route.Add(new Vector2(targetOut.x, middleY));
            }
            else
            {
                route.Add(sourceHorizontal ? new Vector2(targetOut.x, sourceOut.y) : new Vector2(sourceOut.x, targetOut.y));
            }
            route.Add(targetOut);
            route.Add(target.Anchor);
            Clean(route);
            return route;
        }

        private static string FacingSide(Vector2 origin, Vector2 target)
        {
            var delta = target - origin;
            if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                return delta.x >= 0f ? "right" : "left";
            return delta.y >= 0f ? "bottom" : "top";
        }

        private static float FacingGap(string side, Rect rect, Rect opposite)
        {
            return side switch
            {
                "right" => opposite.xMin - rect.xMax,
                "left" => rect.xMin - opposite.xMax,
                "bottom" => opposite.yMin - rect.yMax,
                _ => rect.yMin - opposite.yMax
            };
        }

        private static string OppositeSide(string side)
        {
            return side switch
            {
                "right" => "left",
                "left" => "right",
                "bottom" => "top",
                _ => "bottom"
            };
        }

        private static bool IsHorizontalSide(string side)
        {
            return side == "left" || side == "right";
        }

        private static int DirectionOf(string side)
        {
            return side switch
            {
                "left" => Left,
                "right" => Right,
                "top" => Up,
                _ => Down
            };
        }

        private static Vector2 DirectionVector(int direction)
        {
            return direction switch
            {
                Right => Vector2.right,
                Left => Vector2.left,
                Down => Vector2.up,
                _ => Vector2.down
            };
        }

        private void BuildGrid(IReadOnlyList<Rect> obstacles, IReadOnlyList<WorkRoute> routes)
        {
            var xValues = new List<float>(obstacles.Count * 3 + routes.Count * 2 + 2);
            var yValues = new List<float>(obstacles.Count * 3 + routes.Count * 2 + 2);
            var portsAssigned = routes.Count > 0 && routes[0].PortsAssigned;
            foreach (var rect in obstacles)
            {
                xValues.Add(rect.xMin - Clearance);
                xValues.Add(rect.xMax + Clearance);
                yValues.Add(rect.yMin - Clearance);
                yValues.Add(rect.yMax + Clearance);
                if (portsAssigned)
                    continue;
                xValues.Add(rect.center.x);
                yValues.Add(rect.center.y);
            }
            if (portsAssigned)
            {
                foreach (var route in routes)
                {
                    xValues.Add(route.Source.Stub.x);
                    xValues.Add(route.Target.Stub.x);
                    yValues.Add(route.Source.Stub.y);
                    yValues.Add(route.Target.Stub.y);
                }
            }
            if (xValues.Count > 0)
            {
                xValues.Add(xValues.Min() - OuterMargin);
                xValues.Add(xValues.Max() + OuterMargin);
                yValues.Add(yValues.Min() - OuterMargin);
                yValues.Add(yValues.Max() + OuterMargin);
            }
            xs = DistinctSorted(xValues);
            ys = DistinctSorted(yValues);
            width = xs.Length;
            height = ys.Length;

            var cellCount = width * height;
            if (blocked.Length < cellCount * 2)
            {
                blocked = new bool[cellCount * 2];
                usage = new int[cellCount * 2];
                horizontalLoad = new int[cellCount];
                verticalLoad = new int[cellCount];
                costs = new float[cellCount * 4 + 1];
                parents = new int[cellCount * 4 + 1];
                seen = new int[cellCount * 4 + 1];
                closed = new int[cellCount * 4 + 1];
                stamp = 0;
            }
            else
            {
                Array.Clear(blocked, 0, cellCount * 2);
                Array.Clear(usage, 0, cellCount * 2);
                Array.Clear(horizontalLoad, 0, cellCount);
                Array.Clear(verticalLoad, 0, cellCount);
            }

            foreach (var rect in obstacles)
            {
                var left = IndexOf(xs, rect.xMin - Clearance);
                var right = IndexOf(xs, rect.xMax + Clearance);
                var top = IndexOf(ys, rect.yMin - Clearance);
                var bottom = IndexOf(ys, rect.yMax + Clearance);
                for (var y = top + 1; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                        blocked[Cell(x, y) * 2] = true;
                }
                for (var x = left + 1; x < right; x++)
                {
                    for (var y = top; y < bottom; y++)
                        blocked[Cell(x, y) * 2 + 1] = true;
                }
            }
        }

        private void ChooseSides(IReadOnlyList<WorkRoute> routes)
        {
            var loads = new Dictionary<(string node, string side), int>();
            foreach (var route in routes)
            {
                cancellation.ThrowIfCancellationRequested();
                var sources = route.SideOptions(true, loads);
                var targets = route.SideOptions(false, loads);
                if (!FindPath(route, sources, targets))
                {
                    route.Source = sources[0];
                    route.Target = targets[0];
                    route.Points = DirectRoute(route.Source, route.Target);
                }
                var sourceKey = (route.Request.SourceNodeId, route.Source.Side);
                var targetKey = (route.Request.TargetNodeId, route.Target.Side);
                loads[sourceKey] = loads.GetValueOrDefault(sourceKey) + 1;
                loads[targetKey] = loads.GetValueOrDefault(targetKey) + 1;
            }
        }

        private void RoutePorts(WorkRoute route)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!FindPath(route, new[] { route.Source }, new[] { route.Target }))
                route.Points = DirectRoute(route.Source, route.Target);
        }

        private bool FindPath(WorkRoute route, IReadOnlyList<Terminal> sources, IReadOnlyList<Terminal> targets)
        {
            var goalState = width * height * 4;
            var goalMinX = int.MaxValue;
            var goalMaxX = int.MinValue;
            var goalMinY = int.MaxValue;
            var goalMaxY = int.MinValue;
            var targetCells = new int[targets.Count];
            for (var index = 0; index < targets.Count; index++)
            {
                var targetX = IndexOf(xs, targets[index].Stub.x);
                var targetY = IndexOf(ys, targets[index].Stub.y);
                targetCells[index] = Cell(targetX, targetY);
                goalMinX = Mathf.Min(goalMinX, targetX);
                goalMaxX = Mathf.Max(goalMaxX, targetX);
                goalMinY = Mathf.Min(goalMinY, targetY);
                goalMaxY = Mathf.Max(goalMaxY, targetY);
            }

            stamp++;
            heapCount = 0;
            arrivalIndex = -1;
            foreach (var source in sources)
            {
                var startX = IndexOf(xs, source.Stub.x);
                var startY = IndexOf(ys, source.Stub.y);
                var startState = Cell(startX, startY) * 4 + source.Direction;
                costs[startState] = source.Penalty;
                parents[startState] = -1;
                seen[startState] = stamp;
                Push(source.Penalty + Heuristic(startX, startY, source.Direction, goalMinX, goalMaxX, goalMinY, goalMaxY), startState);
            }

            while (heapCount > 0)
            {
                var state = Pop();
                if (closed[state] == stamp)
                    continue;
                closed[state] = stamp;
                if (state == goalState)
                {
                    Reconstruct(route, parents[goalState], sources, targets[arrivalIndex]);
                    return true;
                }

                var cell = state >> 2;
                var direction = state & 3;
                for (var index = 0; index < targets.Count; index++)
                {
                    if (cell != targetCells[index] || direction == targets[index].Direction)
                        continue;
                    var arrival = costs[state] + targets[index].Penalty;
                    if (direction != (targets[index].Direction ^ 1))
                        arrival += BendCost;
                    if (seen[goalState] == stamp && costs[goalState] <= arrival)
                        continue;
                    seen[goalState] = stamp;
                    costs[goalState] = arrival;
                    parents[goalState] = state;
                    arrivalIndex = index;
                    Push(arrival, goalState);
                }

                var x = cell % width;
                var y = cell / width;
                var stateCost = costs[state];
                for (var next = 0; next < 4; next++)
                {
                    if (next == (direction ^ 1))
                        continue;
                    int nextX;
                    int nextY;
                    int step;
                    float length;
                    switch (next)
                    {
                        case Right:
                            if (x + 1 >= width)
                                continue;
                            nextX = x + 1;
                            nextY = y;
                            step = cell * 2;
                            length = xs[nextX] - xs[x];
                            break;
                        case Left:
                            if (x == 0)
                                continue;
                            nextX = x - 1;
                            nextY = y;
                            step = (cell - 1) * 2;
                            length = xs[x] - xs[nextX];
                            break;
                        case Down:
                            if (y + 1 >= height)
                                continue;
                            nextX = x;
                            nextY = y + 1;
                            step = cell * 2 + 1;
                            length = ys[nextY] - ys[y];
                            break;
                        default:
                            if (y == 0)
                                continue;
                            nextX = x;
                            nextY = y - 1;
                            step = (cell - width) * 2 + 1;
                            length = ys[y] - ys[nextY];
                            break;
                    }
                    if (blocked[step])
                        continue;

                    var nextCell = nextY * width + nextX;
                    var cost = stateCost + length;
                    var shared = usage[step];
                    if (shared > 0)
                        cost += length * OverlapCost * (shared < OverlapLimit ? shared : OverlapLimit);
                    if (next != direction)
                        cost += BendCost;
                    var crossed = next < Down ? verticalLoad[nextCell] : horizontalLoad[nextCell];
                    if (crossed > 0)
                        cost += CrossingCost * crossed;

                    var nextState = nextCell * 4 + next;
                    if (seen[nextState] == stamp && costs[nextState] <= cost)
                        continue;
                    seen[nextState] = stamp;
                    costs[nextState] = cost;
                    parents[nextState] = state;
                    Push(cost + Heuristic(nextX, nextY, next, goalMinX, goalMaxX, goalMinY, goalMaxY), nextState);
                }
            }
            return false;
        }

        private float Heuristic(int x, int y, int direction, int goalMinX, int goalMaxX, int goalMinY, int goalMaxY)
        {
            var distanceX = x < goalMinX ? xs[goalMinX] - xs[x] : x > goalMaxX ? xs[x] - xs[goalMaxX] : 0f;
            var distanceY = y < goalMinY ? ys[goalMinY] - ys[y] : y > goalMaxY ? ys[y] - ys[goalMaxY] : 0f;
            var straight = distanceY <= 0f && direction == (x < goalMinX ? Right : Left)
                           || distanceX <= 0f && direction == (y < goalMinY ? Down : Up);
            var bend = distanceX + distanceY > 0f && !straight ? BendCost : 0f;
            return (distanceX + distanceY) * HeuristicWeight + bend;
        }

        private void Reconstruct(WorkRoute route, int lastState, IReadOnlyList<Terminal> sources, Terminal target)
        {
            var states = new List<int>();
            for (var state = lastState; state >= 0; state = parents[state])
                states.Add(state);
            states.Reverse();

            var startDirection = states[0] & 3;
            var startCell = states[0] >> 2;
            route.Source = sources.First(source =>
                source.Direction == startDirection
                && Cell(IndexOf(xs, source.Stub.x), IndexOf(ys, source.Stub.y)) == startCell);
            route.Target = target;
            route.States = states;

            var points = new List<Vector2>(states.Count + 2) { route.Source.Anchor };
            foreach (var state in states)
            {
                var cell = state >> 2;
                points.Add(new Vector2(xs[cell % width], ys[cell / width]));
            }
            points.Add(target.Anchor);
            Clean(points);
            route.Points = points;
            ApplyLoad(states, 1);
        }

        private bool CrossesOthers(WorkRoute route)
        {
            foreach (var state in route.States)
            {
                var cell = state >> 2;
                if (((state & 3) < Down ? verticalLoad[cell] : horizontalLoad[cell]) > 0)
                    return true;
            }
            return false;
        }

        private void Release(WorkRoute route)
        {
            ApplyLoad(route.States, -1);
            route.States = Array.Empty<int>();
        }

        private void ApplyLoad(IReadOnlyList<int> states, int amount)
        {
            for (var index = 0; index < states.Count; index++)
            {
                var cell = states[index] >> 2;
                if ((states[index] & 3) < Down)
                    horizontalLoad[cell] += amount;
                else
                    verticalLoad[cell] += amount;
                if (index + 1 < states.Count)
                    usage[StepIndex(cell % width, cell / width, states[index + 1] & 3)] += amount;
            }
        }

        private static void AssignPorts(IReadOnlyList<WorkRoute> routes, IReadOnlyList<WorkRoute> frozen)
        {
            var groups = new Dictionary<(string node, string side), List<Endpoint>>();
            foreach (var route in routes.Concat(frozen))
            {
                AddEndpoint(groups, route, true);
                AddEndpoint(groups, route, false);
            }
            foreach (var group in groups
                         .Where(group => group.Value.Any(endpoint => !endpoint.route.Frozen))
                         .OrderByDescending(group => group.Value.Count)
                         .ThenBy(group => group.Key.node, StringComparer.Ordinal)
                         .ThenBy(group => group.Key.side, StringComparer.Ordinal))
            {
                PlacePorts(group.Key.side, group.Value
                    .OrderBy(endpoint => endpoint.leaveKey)
                    .ThenBy(endpoint => endpoint.farKey)
                    .ThenBy(endpoint => endpoint.route.Id, StringComparer.Ordinal)
                    .ThenBy(endpoint => endpoint.source)
                    .ToArray());
            }
            foreach (var route in routes)
                route.PortsAssigned = true;
        }

        private static void PlacePorts(string side, Endpoint[] endpoints)
        {
            var horizontal = IsHorizontalSide(side);
            var rect = endpoints[0].rect;
            var min = (horizontal ? rect.yMin : rect.xMin) + PortMargin;
            var max = (horizontal ? rect.yMax : rect.xMax) - PortMargin;
            if (min > max)
                min = max = horizontal ? rect.center.y : rect.center.x;
            var center = (min + max) * 0.5f;
            var spacing = endpoints.Length < 2 ? 0f : Mathf.Min(PortSpacing, (max - min) / (endpoints.Length - 1));
            var ordered = endpoints
                .Select((endpoint, index) => (endpoint, position: endpoint.route.Frozen
                    ? Along(endpoint, horizontal)
                    : StraightPort(
                        endpoint,
                        horizontal,
                        min,
                        max,
                        center + (index - (endpoints.Length - 1) * 0.5f) * spacing)))
                .OrderBy(item => item.position)
                .ToArray();
            var positions = ordered.Select(item => item.position).ToArray();
            var lows = ordered.Select(item => item.endpoint.route.Frozen ? item.position : min).ToArray();
            var highs = ordered.Select(item => item.endpoint.route.Frozen ? item.position : max).ToArray();
            Spread(positions, lows, highs, spacing);
            for (var index = 0; index < ordered.Length; index++)
            {
                var endpoint = ordered[index].endpoint;
                if (endpoint.route.Frozen)
                    continue;
                var anchor = Anchor(rect, side);
                anchor = horizontal ? new Vector2(anchor.x, positions[index]) : new Vector2(positions[index], anchor.y);
                var terminal = new Terminal(side, anchor, 0f);
                if (endpoint.source)
                {
                    endpoint.route.Source = terminal;
                    endpoint.route.SourcePortPlaced = true;
                }
                else
                {
                    endpoint.route.Target = terminal;
                    endpoint.route.TargetPortPlaced = true;
                }
            }
        }

        private static float Along(Endpoint endpoint, bool horizontal)
        {
            var anchor = endpoint.source ? endpoint.route.Source.Anchor : endpoint.route.Target.Anchor;
            return horizontal ? anchor.y : anchor.x;
        }

        private static float StraightPort(Endpoint endpoint, bool horizontal, float min, float max, float slot)
        {
            var route = endpoint.route;
            var other = endpoint.source ? route.Target : route.Source;
            if (other.Side != OppositeSide(endpoint.side))
                return slot;
            var otherRect = endpoint.source ? route.TargetRect : route.SourceRect;
            if (FacingGap(endpoint.side, endpoint.rect, otherRect) < 0f)
                return slot;
            var otherMin = (horizontal ? otherRect.yMin : otherRect.xMin) + PortMargin;
            var otherMax = (horizontal ? otherRect.yMax : otherRect.xMax) - PortMargin;
            var low = Mathf.Max(min, otherMin);
            var high = Mathf.Min(max, otherMax);
            if (low > high)
                return slot;
            var placed = endpoint.source ? route.TargetPortPlaced : route.SourcePortPlaced;
            if (!placed)
                return Mathf.Clamp(slot, low, high);
            return Mathf.Clamp(horizontal ? other.Anchor.y : other.Anchor.x, low, high);
        }

        private static void Spread(float[] positions, float[] lows, float[] highs, float spacing)
        {
            for (var index = 0; index < positions.Length; index++)
            {
                var low = index == 0 ? lows[index] : Mathf.Max(lows[index], positions[index - 1] + spacing);
                positions[index] = Mathf.Max(positions[index], low);
            }
            for (var index = positions.Length - 1; index >= 0; index--)
            {
                var high = index == positions.Length - 1 ? highs[index] : Mathf.Min(highs[index], positions[index + 1] - spacing);
                positions[index] = Mathf.Min(positions[index], high);
            }
            for (var index = 0; index < positions.Length; index++)
                positions[index] = Mathf.Clamp(positions[index], lows[index], highs[index]);
        }

        private static void AddEndpoint(
            IDictionary<(string node, string side), List<Endpoint>> groups,
            WorkRoute route,
            bool source)
        {
            var side = source ? route.Source.Side : route.Target.Side;
            var key = (source ? route.Request.SourceNodeId : route.Request.TargetNodeId, side);
            if (!groups.TryGetValue(key, out var endpoints))
            {
                endpoints = new List<Endpoint>();
                groups.Add(key, endpoints);
            }
            var points = route.Points;
            var leavePoint = source ? points[Mathf.Min(2, points.Count - 1)] : points[Mathf.Max(points.Count - 3, 0)];
            var farPoint = source ? route.TargetRect.center : route.SourceRect.center;
            var horizontal = IsHorizontalSide(side);
            endpoints.Add(new Endpoint(
                route,
                source,
                source ? route.SourceRect : route.TargetRect,
                side,
                horizontal ? leavePoint.y : leavePoint.x,
                horizontal ? farPoint.y : farPoint.x));
        }

        private void Untangle(IReadOnlyList<WorkRoute> routes, IReadOnlyList<Rect> obstacles)
        {
            foreach (var route in routes)
                route.GridPoints = new List<Vector2>(route.Points);
            SeparateTracks(routes, obstacles);
            var crossings = TotalCrossings(routes);
            var attempts = 0;
            var rejected = new HashSet<(string first, string second)>();
            while (crossings > 0 && attempts < UntangleAttempts)
            {
                var candidates = TangledPorts(routes, rejected);
                if (candidates.Count == 0)
                    return;
                var (first, second) = candidates[0];
                attempts++;
                var separated = routes.ToDictionary(route => route, route => route.Points);
                var saved = new[] { first.route, second.route }
                    .Distinct()
                    .Select(route => (route, route.Source, route.Target, route.GridPoints, route.States))
                    .ToArray();
                SwapPorts(first, second);
                foreach (var (route, _, _, _, _) in saved)
                {
                    Release(route);
                    RoutePorts(route);
                    route.GridPoints = new List<Vector2>(route.Points);
                }
                foreach (var route in routes)
                    route.Points = new List<Vector2>(route.GridPoints);
                SeparateTracks(routes, obstacles);

                var next = TotalCrossings(routes);
                if (next < crossings)
                {
                    crossings = next;
                    rejected.Clear();
                    continue;
                }

                foreach (var (route, source, target, gridPoints, states) in saved)
                {
                    Release(route);
                    route.Source = source;
                    route.Target = target;
                    route.GridPoints = gridPoints;
                    route.States = states;
                    ApplyLoad(states, 1);
                }
                foreach (var route in routes)
                    route.Points = separated[route];
                rejected.Add((first.route.Id, second.route.Id));
            }
        }

        private static int TotalCrossings(IEnumerable<WorkRoute> routes)
        {
            var points = routes.ToDictionary(route => route.Id, route => route.Points, StringComparer.Ordinal);
            return CrossingCounts(points).Values.Sum() / 2 + SharedRuns(points);
        }

        private static int SharedRuns(IReadOnlyDictionary<string, List<Vector2>> routes)
        {
            var legs = routes.SelectMany(route => Legs(route.Key, route.Value))
                .OrderBy(leg => leg.Horizontal)
                .ThenBy(leg => leg.Across)
                .ToArray();
            var shared = 0;
            for (var first = 0; first < legs.Length; first++)
            {
                for (var second = first + 1; second < legs.Length; second++)
                {
                    if (legs[first].Horizontal != legs[second].Horizontal || legs[second].Across - legs[first].Across >= 0.5f)
                        break;
                    if (legs[first].Id != legs[second].Id
                        && Mathf.Min(legs[first].Max, legs[second].Max) - Mathf.Max(legs[first].Min, legs[second].Min) > 1f)
                        shared++;
                }
            }
            return shared;
        }

        private static List<(Endpoint first, Endpoint second)> TangledPorts(
            IReadOnlyList<WorkRoute> routes,
            ISet<(string first, string second)> rejected)
        {
            var groups = new Dictionary<(string node, string side), List<Endpoint>>();
            foreach (var route in routes)
            {
                AddEndpoint(groups, route, true);
                AddEndpoint(groups, route, false);
            }

            var swaps = new List<(Endpoint first, Endpoint second)>();
            var shared = 0;
            foreach (var group in groups
                         .Where(group => group.Value.Count > 1)
                         .OrderBy(group => group.Key.node, StringComparer.Ordinal)
                         .ThenBy(group => group.Key.side, StringComparer.Ordinal))
            {
                var horizontal = IsHorizontalSide(group.Key.side);
                var ordered = group.Value.OrderBy(endpoint => Along(endpoint, horizontal)).ToArray();
                for (var index = 0; index + 1 < ordered.Length; index++)
                {
                    var first = ordered[index];
                    var second = ordered[index + 1];
                    if (first.route == second.route || rejected.Contains((first.route.Id, second.route.Id)))
                        continue;
                    if (ShareTrack(first.route, second.route))
                        swaps.Insert(shared++, (first, second));
                    else if (Cross(first.route, second.route))
                        swaps.Add((first, second));
                }
            }
            return swaps;
        }

        private static bool Cross(WorkRoute first, WorkRoute second)
        {
            return CrossingCounts(Pair(first, second))[first.Id] % 2 == 1;
        }

        private static bool ShareTrack(WorkRoute first, WorkRoute second)
        {
            return SharedRuns(Pair(first, second)) > 0;
        }

        private static Dictionary<string, List<Vector2>> Pair(WorkRoute first, WorkRoute second)
        {
            return new Dictionary<string, List<Vector2>>(2, StringComparer.Ordinal)
            {
                [first.Id] = first.Points,
                [second.Id] = second.Points
            };
        }

        private static void SwapPorts(Endpoint first, Endpoint second)
        {
            var firstTerminal = first.source ? first.route.Source : first.route.Target;
            var secondTerminal = second.source ? second.route.Source : second.route.Target;
            SetTerminal(first, secondTerminal);
            SetTerminal(second, firstTerminal);
        }

        private static void SetTerminal(Endpoint endpoint, Terminal terminal)
        {
            if (endpoint.source)
                endpoint.route.Source = terminal;
            else
                endpoint.route.Target = terminal;
        }

        private static void SeparateTracks(IReadOnlyList<WorkRoute> routes, IReadOnlyList<Rect> obstacles)
        {
            SeparateTracks(routes, obstacles, true);
            SeparateTracks(routes, obstacles, false);
        }

        private static void SeparateTracks(IEnumerable<WorkRoute> routes, IReadOnlyList<Rect> obstacles, bool horizontal)
        {
            var segments = new List<TrackSegment>();
            foreach (var route in routes)
            {
                Clean(route.Points);
                var points = route.Points;
                for (var index = 0; index + 1 < points.Count; index++)
                {
                    var start = points[index];
                    var end = points[index + 1];
                    if (horizontal != Mathf.Abs(start.y - end.y) < 0.01f)
                        continue;
                    var pinned = route.Frozen || index == 0 || index + 2 == points.Count;
                    segments.Add(horizontal
                        ? new TrackSegment(route, index, start.y, Mathf.Min(start.x, end.x), Mathf.Max(start.x, end.x), pinned)
                        : new TrackSegment(route, index, start.x, Mathf.Min(start.y, end.y), Mathf.Max(start.y, end.y), pinned));
                }
            }

            segments.Sort((left, right) =>
            {
                var position = left.position.CompareTo(right.position);
                return position != 0 ? position : left.min.CompareTo(right.min);
            });

            var groupStart = 0;
            while (groupStart < segments.Count)
            {
                var groupEnd = groupStart + 1;
                while (groupEnd < segments.Count && segments[groupEnd].position - segments[groupStart].position < 0.5f)
                    groupEnd++;
                SeparateCollinearGroup(segments.GetRange(groupStart, groupEnd - groupStart), obstacles, horizontal);
                groupStart = groupEnd;
            }
        }

        private static void SeparateCollinearGroup(List<TrackSegment> group, IReadOnlyList<Rect> obstacles, bool horizontal)
        {
            group.Sort((left, right) => left.min.CompareTo(right.min));
            var bundle = new List<TrackSegment>();
            var bundleMax = float.NegativeInfinity;
            foreach (var segment in group)
            {
                if (bundle.Count > 0 && segment.min >= bundleMax - 0.5f)
                {
                    PlaceTracks(bundle, obstacles, horizontal);
                    bundle.Clear();
                    bundleMax = float.NegativeInfinity;
                }
                bundle.Add(segment);
                bundleMax = Mathf.Max(bundleMax, segment.max);
            }
            PlaceTracks(bundle, obstacles, horizontal);
        }

        private static void PlaceTracks(List<TrackSegment> bundle, IReadOnlyList<Rect> obstacles, bool horizontal)
        {
            if (bundle.Count < 2)
                return;

            for (var index = 1; index < bundle.Count; index++)
            {
                var item = bundle[index];
                var cursor = index - 1;
                while (cursor >= 0 && CompareTracks(bundle[cursor], item, horizontal) > 0)
                {
                    bundle[cursor + 1] = bundle[cursor];
                    cursor--;
                }
                bundle[cursor + 1] = item;
            }

            var position = bundle[0].position;
            var low = float.NegativeInfinity;
            var high = float.PositiveInfinity;
            var min = bundle.Min(segment => segment.min);
            var max = bundle.Max(segment => segment.max);
            foreach (var rect in obstacles)
            {
                var alongMin = (horizontal ? rect.xMin : rect.yMin) - NodeMargin;
                var alongMax = (horizontal ? rect.xMax : rect.yMax) + NodeMargin;
                if (max <= alongMin || min >= alongMax)
                    continue;
                var acrossMin = (horizontal ? rect.yMin : rect.xMin) - NodeMargin;
                var acrossMax = (horizontal ? rect.yMax : rect.xMax) + NodeMargin;
                if (acrossMax <= position)
                    low = Mathf.Max(low, acrossMax);
                else if (acrossMin >= position)
                    high = Mathf.Min(high, acrossMin);
            }

            var spacing = TrackSpacing;
            var total = spacing * (bundle.Count - 1);
            if (high - low < total)
            {
                spacing = (high - low) / (bundle.Count - 1);
                total = high - low;
            }
            var first = Mathf.Clamp(position - total * 0.5f, low, high - total);
            var tracks = new float[bundle.Count];
            var lows = new float[bundle.Count];
            var highs = new float[bundle.Count];
            for (var index = 0; index < bundle.Count; index++)
            {
                tracks[index] = first + spacing * index;
                TrackBounds(bundle[index], horizontal, low, high, out lows[index], out highs[index]);
            }
            Spread(tracks, lows, highs, spacing);
            for (var index = 0; index < bundle.Count; index++)
            {
                var segment = bundle[index];
                if (segment.pinned)
                    continue;
                var points = segment.route.Points;
                points[segment.index] = WithAcross(points[segment.index], tracks[index], horizontal);
                points[segment.index + 1] = WithAcross(points[segment.index + 1], tracks[index], horizontal);
            }
        }

        private static void TrackBounds(TrackSegment segment, bool horizontal, float corridorLow, float corridorHigh, out float low, out float high)
        {
            if (segment.pinned)
            {
                low = segment.position;
                high = segment.position;
                return;
            }
            var points = segment.route.Points;
            low = corridorLow;
            high = corridorHigh;
            var beforeLength = segment.index == 1 ? Clearance : MinimumSegment;
            var afterLength = segment.index + 3 == points.Count ? Clearance : MinimumSegment;
            Limit(Across(points[segment.index - 1], horizontal), segment.position, beforeLength, ref low, ref high);
            Limit(Across(points[segment.index + 2], horizontal), segment.position, afterLength, ref low, ref high);
            if (low > high)
                low = high = segment.position;
        }

        private static void Limit(float neighbor, float position, float length, ref float low, ref float high)
        {
            if (position >= neighbor)
                low = Mathf.Max(low, neighbor + length);
            else
                high = Mathf.Min(high, neighbor - length);
        }

        private static float Across(Vector2 point, bool horizontal)
        {
            return horizontal ? point.y : point.x;
        }

        private static Vector2 WithAcross(Vector2 point, float value, bool horizontal)
        {
            return horizontal ? new Vector2(point.x, value) : new Vector2(value, point.y);
        }

        private static int CompareTracks(TrackSegment left, TrackSegment right, bool horizontal)
        {
            var vote = EndVote(left, right, horizontal, false) + EndVote(left, right, horizontal, true);
            if (vote != 0)
                return vote > 0 ? 1 : -1;
            var key = TrackKey(left, horizontal).CompareTo(TrackKey(right, horizontal));
            return key != 0 ? key : string.CompareOrdinal(left.route.Id, right.route.Id);
        }

        private static int EndVote(TrackSegment left, TrackSegment right, bool horizontal, bool atMax)
        {
            var leftEnd = atMax ? left.max : left.min;
            var rightEnd = atMax ? right.max : right.min;
            var leftTurn = Turn(left, horizontal, atMax);
            var rightTurn = Turn(right, horizontal, atMax);
            if (Mathf.Abs(leftEnd - rightEnd) < 0.5f)
                return Math.Sign(leftTurn - rightTurn);
            var leftInside = atMax ? leftEnd < rightEnd : leftEnd > rightEnd;
            return leftInside ? leftTurn : -rightTurn;
        }

        private static int Turn(TrackSegment segment, bool horizontal, bool atMax)
        {
            var points = segment.route.Points;
            var first = points[segment.index];
            var second = points[segment.index + 1];
            var firstIsMax = horizontal ? first.x > second.x : first.y > second.y;
            var neighbor = atMax == firstIsMax ? segment.index - 1 : segment.index + 2;
            if (neighbor < 0 || neighbor >= points.Count)
                return 0;
            return Across(points[neighbor], horizontal) >= segment.position ? 1 : -1;
        }

        private static float TrackKey(TrackSegment segment, bool horizontal)
        {
            var points = segment.route.Points;
            var before = points[Mathf.Max(segment.index - 1, 0)];
            var after = points[Mathf.Min(segment.index + 2, points.Count - 1)];
            return horizontal ? before.y + after.y : before.x + after.x;
        }

        private static void Clean(List<Vector2> route)
        {
            for (var index = route.Count - 1; index > 0; index--)
            {
                if ((route[index] - route[index - 1]).sqrMagnitude < 0.0001f)
                    route.RemoveAt(index);
            }
            for (var index = route.Count - 2; index > 0; index--)
            {
                var before = route[index] - route[index - 1];
                var after = route[index + 1] - route[index];
                var sameAxis = Mathf.Abs(before.x) < 0.01f && Mathf.Abs(after.x) < 0.01f
                               || Mathf.Abs(before.y) < 0.01f && Mathf.Abs(after.y) < 0.01f;
                if (sameAxis && Vector2.Dot(before, after) > 0f)
                    route.RemoveAt(index);
            }
        }

        private static float[] DistinctSorted(List<float> values)
        {
            values.Sort();
            var result = new List<float>(values.Count);
            foreach (var value in values)
            {
                if (result.Count == 0 || value - result[result.Count - 1] > 0.01f)
                    result.Add(value);
            }
            return result.ToArray();
        }

        private static int IndexOf(float[] values, float value)
        {
            var low = 0;
            var high = values.Length - 1;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (values[middle] < value - 0.01f)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private int Cell(int x, int y)
        {
            return y * width + x;
        }

        private int StepIndex(int x, int y, int direction)
        {
            return direction switch
            {
                Right => Cell(x, y) * 2,
                Left => Cell(x - 1, y) * 2,
                Down => Cell(x, y) * 2 + 1,
                _ => Cell(x, y - 1) * 2 + 1
            };
        }

        private void Push(float key, int state)
        {
            if (heapCount == heapKeys.Length)
            {
                Array.Resize(ref heapKeys, heapCount * 2);
                Array.Resize(ref heapStates, heapCount * 2);
            }
            var index = heapCount++;
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (heapKeys[parent] <= key)
                    break;
                heapKeys[index] = heapKeys[parent];
                heapStates[index] = heapStates[parent];
                index = parent;
            }
            heapKeys[index] = key;
            heapStates[index] = state;
        }

        private int Pop()
        {
            var result = heapStates[0];
            heapCount--;
            var key = heapKeys[heapCount];
            var state = heapStates[heapCount];
            var index = 0;
            while (true)
            {
                var child = index * 2 + 1;
                if (child >= heapCount)
                    break;
                if (child + 1 < heapCount && heapKeys[child + 1] < heapKeys[child])
                    child++;
                if (key <= heapKeys[child])
                    break;
                heapKeys[index] = heapKeys[child];
                heapStates[index] = heapStates[child];
                index = child;
            }
            heapKeys[index] = key;
            heapStates[index] = state;
            return result;
        }

        private sealed class WorkRoute
        {
            public WorkRoute(ArchitectorRouteRequest request, Rect sourceRect, Rect targetRect)
            {
                Request = request;
                SourceRect = sourceRect;
                TargetRect = targetRect;
                Span = Mathf.Abs(sourceRect.center.x - targetRect.center.x) + Mathf.Abs(sourceRect.center.y - targetRect.center.y);
            }

            public ArchitectorRouteRequest Request { get; }
            public string Id => Request.Id;
            public Rect SourceRect { get; }
            public Rect TargetRect { get; }
            public Terminal Source { get; set; }
            public Terminal Target { get; set; }
            public bool SourcePortPlaced { get; set; }
            public bool TargetPortPlaced { get; set; }
            public bool PortsAssigned { get; set; }
            public bool Frozen { get; private set; }
            public float Span { get; }
            public List<Vector2> Points { get; set; }
            public List<Vector2> GridPoints { get; set; }
            public IReadOnlyList<int> States { get; set; } = Array.Empty<int>();

            public void Freeze(List<Vector2> points)
            {
                Points = points;
                Frozen = true;
                PortsAssigned = true;
                Source = new Terminal(SideAt(SourceRect, points[0]), points[0], 0f);
                Target = new Terminal(SideAt(TargetRect, points[points.Count - 1]), points[points.Count - 1], 0f);
            }

            private static string SideAt(Rect rect, Vector2 point)
            {
                var left = Mathf.Abs(point.x - rect.xMin);
                var right = Mathf.Abs(point.x - rect.xMax);
                var top = Mathf.Abs(point.y - rect.yMin);
                var bottom = Mathf.Abs(point.y - rect.yMax);
                var nearest = Mathf.Min(Mathf.Min(left, right), Mathf.Min(top, bottom));
                return nearest == left ? "left" : nearest == right ? "right" : nearest == top ? "top" : "bottom";
            }

            public List<Terminal> SideOptions(bool source, IReadOnlyDictionary<(string node, string side), int> loads)
            {
                var node = source ? Request.SourceNodeId : Request.TargetNodeId;
                var rect = source ? SourceRect : TargetRect;
                var opposite = source ? TargetRect : SourceRect;
                var preferred = source ? Request.SourceSide : Request.TargetSide;
                if (Request.SourceNodeId == Request.TargetNodeId)
                    preferred = source ? "right" : "top";
                var options = new List<Terminal>(4);
                if (!string.IsNullOrEmpty(preferred) && preferred != "auto")
                {
                    options.Add(new Terminal(preferred, Anchor(rect, preferred), 0f));
                    return options;
                }
                var facing = FacingSide(rect.center, opposite.center);
                foreach (var side in Sides)
                {
                    if (side != facing && FacingGap(side, rect, opposite) < 0f)
                        continue;
                    var penalty = (side == facing ? 0f : SecondarySideCost) + SideLoadCost * loads.GetValueOrDefault((node, side));
                    var terminal = new Terminal(side, Anchor(rect, side), penalty);
                    if (side == facing)
                        options.Insert(0, terminal);
                    else
                        options.Add(terminal);
                }
                return options;
            }
        }

        private readonly struct Terminal
        {
            public Terminal(string side, Vector2 anchor, float penalty)
            {
                Side = side;
                Anchor = anchor;
                Penalty = penalty;
                Direction = DirectionOf(side);
                Stub = anchor + DirectionVector(Direction) * Clearance;
            }

            public string Side { get; }
            public Vector2 Anchor { get; }
            public Vector2 Stub { get; }
            public int Direction { get; }
            public float Penalty { get; }
        }

        private sealed class Leg
        {
            public Leg(string id, Vector2 start, Vector2 end, bool first, bool last)
            {
                Id = id;
                First = first;
                Last = last;
                Horizontal = Mathf.Abs(start.y - end.y) < 0.01f && Mathf.Abs(start.x - end.x) > 1f;
                Vertical = Mathf.Abs(start.x - end.x) < 0.01f && Mathf.Abs(start.y - end.y) > 1f;
                Across = Horizontal ? start.y : start.x;
                Start = Horizontal ? start.x : start.y;
                End = Horizontal ? end.x : end.y;
                Min = Mathf.Min(Start, End);
                Max = Mathf.Max(Start, End);
            }

            public string Id { get; }
            public bool First { get; }
            public bool Last { get; }
            public bool Horizontal { get; }
            public bool Vertical { get; }
            public float Across { get; }
            public float Start { get; }
            public float End { get; }
            public float Min { get; }
            public float Max { get; }
            public float Length => Max - Min;
            public List<float> Hops { get; } = new();

            public float Room(float position, float bridgeRadius)
            {
                var startMargin = bridgeRadius + BridgeEndMargin + (First ? 0f : MinimumCorner);
                var endMargin = bridgeRadius + BridgeEndMargin + (Last ? BridgeArrowMargin : MinimumCorner);
                return Mathf.Min(Mathf.Abs(position - Start) - startMargin, Mathf.Abs(End - position) - endMargin);
            }

            public List<(float from, float to)> Spans(float bridgeRadius)
            {
                var spans = new List<(float from, float to)>();
                if (Hops.Count == 0)
                    return spans;
                var direction = End > Start ? 1f : -1f;
                var ordered = Hops.Distinct().OrderBy(position => position * direction).ToArray();
                var first = 0;
                for (var index = 0; index < ordered.Length; index++)
                {
                    if (index + 1 < ordered.Length && Mathf.Abs(ordered[index + 1] - ordered[index]) <= bridgeRadius * 2f + BridgeMergeGap)
                        continue;
                    spans.Add((ordered[first] - direction * bridgeRadius, ordered[index] + direction * bridgeRadius));
                    first = index + 1;
                }
                return spans;
            }

            public Vector2 Point(float along, float lift)
            {
                return Horizontal ? new Vector2(along, Across - lift) : new Vector2(Across + lift, along);
            }
        }

        private readonly struct Endpoint
        {
            public Endpoint(WorkRoute route, bool source, Rect rect, string side, float leaveKey, float farKey)
            {
                this.route = route;
                this.source = source;
                this.rect = rect;
                this.side = side;
                this.leaveKey = leaveKey;
                this.farKey = farKey;
            }

            public readonly WorkRoute route;
            public readonly bool source;
            public readonly Rect rect;
            public readonly string side;
            public readonly float leaveKey;
            public readonly float farKey;
        }

        private readonly struct TrackSegment
        {
            public TrackSegment(WorkRoute route, int index, float position, float min, float max, bool pinned)
            {
                this.route = route;
                this.index = index;
                this.position = position;
                this.min = min;
                this.max = max;
                this.pinned = pinned;
            }

            public readonly WorkRoute route;
            public readonly int index;
            public readonly float position;
            public readonly float min;
            public readonly float max;
            public readonly bool pinned;
        }
    }
}
