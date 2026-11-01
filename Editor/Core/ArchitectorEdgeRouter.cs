using System;
using System.Collections.Generic;
using System.Linq;
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
        public const float Clearance = 16f;
        private const float OuterMargin = 48f;
        private const float BendCost = 48f;
        private const float CrossingCost = 24f;
        private const float OverlapCost = 0.75f;
        private const int OverlapLimit = 4;
        private const float PortSpacing = 14f;
        private const float PortMargin = 10f;
        private const float TrackSpacing = 10f;
        private const float NodeMargin = 5f;
        private const int CornerSteps = 4;
        private const int Right = 0;
        private const int Left = 1;
        private const int Down = 2;
        private const int Up = 3;

        private float[] xs = Array.Empty<float>();
        private float[] ys = Array.Empty<float>();
        private int width;
        private int height;
        private bool[] blocked = Array.Empty<bool>();
        private int[] usage = Array.Empty<int>();
        private bool[] horizontalThrough = Array.Empty<bool>();
        private bool[] verticalThrough = Array.Empty<bool>();
        private float[] costs = Array.Empty<float>();
        private int[] parents = Array.Empty<int>();
        private int[] seen = Array.Empty<int>();
        private int[] closed = Array.Empty<int>();
        private int stamp;
        private float[] heapKeys = new float[256];
        private int[] heapStates = new int[256];
        private int heapCount;

        public Dictionary<string, List<Vector2>> Route(
            IReadOnlyDictionary<string, Rect> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests)
        {
            var routes = requests
                .Where(request => nodes.ContainsKey(request.SourceNodeId) && nodes.ContainsKey(request.TargetNodeId))
                .Select(request => new WorkRoute(request, nodes[request.SourceNodeId], nodes[request.TargetNodeId]))
                .OrderBy(route => route.Span)
                .ThenBy(route => route.Id, StringComparer.Ordinal)
                .ToList();
            var obstacles = nodes.Values.ToArray();
            BuildGrid(obstacles);
            foreach (var route in routes)
                route.Points = FindPath(route)
                               ?? PreviewRoute(route.SourceRect, route.SourceSide, route.TargetRect, route.TargetSide);
            AssignPorts(routes);
            SeparateTracks(routes, obstacles, true);
            SeparateTracks(routes, obstacles, false);
            var result = new Dictionary<string, List<Vector2>>(StringComparer.Ordinal);
            foreach (var route in routes)
            {
                Clean(route.Points);
                result[route.Id] = route.Points;
            }
            return result;
        }

        public static string ResolveSide(string preferredSide, Rect rect, Rect oppositeRect)
        {
            return string.IsNullOrEmpty(preferredSide) || preferredSide == "auto"
                ? FacingSide(rect.center, oppositeRect.center)
                : preferredSide;
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

        public static List<Vector2> PreviewRoute(Rect sourceRect, string sourceSide, Rect targetRect, string targetSide)
        {
            var sourceAnchor = Anchor(sourceRect, sourceSide);
            var targetAnchor = Anchor(targetRect, targetSide);
            var sourceOut = sourceAnchor + DirectionVector(DirectionOf(sourceSide)) * Clearance;
            var targetOut = targetAnchor + DirectionVector(DirectionOf(targetSide)) * Clearance;
            var route = new List<Vector2> { sourceAnchor, sourceOut };
            var sourceHorizontal = IsHorizontalSide(sourceSide);
            var targetHorizontal = IsHorizontalSide(targetSide);
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
            route.Add(targetAnchor);
            Clean(route);
            return route;
        }

        public static List<Vector2> RoundCorners(IReadOnlyList<Vector2> points, float radius)
        {
            var result = new List<Vector2>(points.Count * (CornerSteps + 1));
            if (points.Count == 0)
                return result;
            result.Add(points[0]);
            for (var index = 1; index < points.Count - 1; index++)
            {
                var corner = points[index];
                var before = corner - points[index - 1];
                var after = points[index + 1] - corner;
                var cornerRadius = Mathf.Min(radius, before.magnitude * 0.5f, after.magnitude * 0.5f);
                if (cornerRadius < 0.5f)
                {
                    result.Add(corner);
                    continue;
                }
                var entry = corner - before.normalized * cornerRadius;
                var exit = corner + after.normalized * cornerRadius;
                for (var step = 0; step <= CornerSteps; step++)
                {
                    var t = step / (float)CornerSteps;
                    var u = 1f - t;
                    result.Add(u * u * entry + 2f * u * t * corner + t * t * exit);
                }
            }
            if (points.Count > 1)
                result.Add(points[points.Count - 1]);
            return result;
        }

        private static string FacingSide(Vector2 origin, Vector2 target)
        {
            var delta = target - origin;
            if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                return delta.x >= 0f ? "right" : "left";
            return delta.y >= 0f ? "bottom" : "top";
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

        private void BuildGrid(IReadOnlyList<Rect> obstacles)
        {
            var xValues = new List<float>(obstacles.Count * 3 + 2);
            var yValues = new List<float>(obstacles.Count * 3 + 2);
            foreach (var rect in obstacles)
            {
                xValues.Add(rect.xMin - Clearance);
                xValues.Add(rect.center.x);
                xValues.Add(rect.xMax + Clearance);
                yValues.Add(rect.yMin - Clearance);
                yValues.Add(rect.center.y);
                yValues.Add(rect.yMax + Clearance);
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
                horizontalThrough = new bool[cellCount];
                verticalThrough = new bool[cellCount];
                costs = new float[cellCount * 4];
                parents = new int[cellCount * 4];
                seen = new int[cellCount * 4];
                closed = new int[cellCount * 4];
                stamp = 0;
            }
            else
            {
                Array.Clear(blocked, 0, cellCount * 2);
                Array.Clear(usage, 0, cellCount * 2);
                Array.Clear(horizontalThrough, 0, cellCount);
                Array.Clear(verticalThrough, 0, cellCount);
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

        private List<Vector2> FindPath(WorkRoute route)
        {
            var goalX = IndexOf(xs, route.TargetStub.x);
            var goalY = IndexOf(ys, route.TargetStub.y);
            var goalCell = Cell(goalX, goalY);
            var goalDirection = DirectionOf(route.TargetSide) ^ 1;
            var startX = IndexOf(xs, route.SourceStub.x);
            var startY = IndexOf(ys, route.SourceStub.y);
            var startState = Cell(startX, startY) * 4 + DirectionOf(route.SourceSide);

            stamp++;
            heapCount = 0;
            costs[startState] = 0f;
            parents[startState] = -1;
            seen[startState] = stamp;
            Push(Heuristic(startX, startY, goalX, goalY), startState);

            while (heapCount > 0)
            {
                var state = Pop();
                if (closed[state] == stamp)
                    continue;
                closed[state] = stamp;
                var cell = state >> 2;
                var direction = state & 3;
                if (cell == goalCell && direction == goalDirection)
                    return Reconstruct(route, state);

                var x = cell % width;
                var y = cell / width;
                for (var next = 0; next < 4; next++)
                {
                    if (next == (direction ^ 1))
                        continue;
                    var nextX = next == Right ? x + 1 : next == Left ? x - 1 : x;
                    var nextY = next == Down ? y + 1 : next == Up ? y - 1 : y;
                    if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height)
                        continue;
                    var step = StepIndex(x, y, next);
                    if (blocked[step])
                        continue;

                    var length = Mathf.Abs(xs[nextX] - xs[x]) + Mathf.Abs(ys[nextY] - ys[y]);
                    var nextCell = Cell(nextX, nextY);
                    var cost = costs[state] + length * (1f + OverlapCost * Mathf.Min(usage[step], OverlapLimit));
                    if (next != direction)
                        cost += BendCost;
                    if (next < Down ? verticalThrough[nextCell] : horizontalThrough[nextCell])
                        cost += CrossingCost;

                    var nextState = nextCell * 4 + next;
                    if (seen[nextState] == stamp && costs[nextState] <= cost)
                        continue;
                    seen[nextState] = stamp;
                    costs[nextState] = cost;
                    parents[nextState] = state;
                    Push(cost + Heuristic(nextX, nextY, goalX, goalY), nextState);
                }
            }
            return null;
        }

        private float Heuristic(int x, int y, int goalX, int goalY)
        {
            return Mathf.Abs(xs[x] - xs[goalX]) + Mathf.Abs(ys[y] - ys[goalY]);
        }

        private List<Vector2> Reconstruct(WorkRoute route, int goalState)
        {
            var states = new List<int>();
            for (var state = goalState; state >= 0; state = parents[state])
                states.Add(state);
            states.Reverse();

            var points = new List<Vector2>(states.Count + 2) { route.SourceAnchor };
            for (var index = 0; index < states.Count; index++)
            {
                var cell = states[index] >> 2;
                var direction = states[index] & 3;
                var x = cell % width;
                var y = cell / width;
                points.Add(new Vector2(xs[x], ys[y]));
                if (direction < Down)
                    horizontalThrough[cell] = true;
                else
                    verticalThrough[cell] = true;
                if (index + 1 < states.Count)
                    usage[StepIndex(x, y, states[index + 1] & 3)]++;
            }
            points.Add(route.TargetAnchor);
            Clean(points);
            return points;
        }

        private static void AssignPorts(IEnumerable<WorkRoute> routes)
        {
            var groups = new Dictionary<(string node, string side), List<Endpoint>>();
            foreach (var route in routes)
            {
                AddEndpoint(groups, route, true);
                AddEndpoint(groups, route, false);
            }
            foreach (var group in groups)
            {
                var endpoints = group.Value
                    .OrderBy(endpoint => endpoint.leaveKey)
                    .ThenBy(endpoint => endpoint.farKey)
                    .ThenBy(endpoint => endpoint.route.Id, StringComparer.Ordinal)
                    .ThenBy(endpoint => endpoint.source)
                    .ToArray();
                var length = IsHorizontalSide(group.Key.side) ? endpoints[0].rect.height : endpoints[0].rect.width;
                var spacing = endpoints.Length < 2
                    ? 0f
                    : Mathf.Min(PortSpacing, Mathf.Max(0f, length - PortMargin * 2f) / (endpoints.Length - 1));
                for (var index = 0; index < endpoints.Length; index++)
                    ShiftTerminal(endpoints[index], (index - (endpoints.Length - 1) * 0.5f) * spacing);
            }
        }

        private static void AddEndpoint(
            IDictionary<(string node, string side), List<Endpoint>> groups,
            WorkRoute route,
            bool source)
        {
            var side = source ? route.SourceSide : route.TargetSide;
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

        private static void ShiftTerminal(Endpoint endpoint, float offset)
        {
            var points = endpoint.route.Points;
            if (points.Count == 2)
            {
                var middle = (points[0] + points[1]) * 0.5f;
                points.Insert(1, middle);
                points.Insert(1, middle);
            }
            var shift = IsHorizontalSide(endpoint.side) ? new Vector2(0f, offset) : new Vector2(offset, 0f);
            var end = endpoint.source ? 0 : points.Count - 1;
            var corner = endpoint.source ? 1 : points.Count - 2;
            points[end] += shift;
            points[corner] += shift;
        }

        private static void SeparateTracks(IEnumerable<WorkRoute> routes, IReadOnlyList<Rect> obstacles, bool horizontal)
        {
            var segments = new List<TrackSegment>();
            foreach (var route in routes)
            {
                Clean(route.Points);
                var points = route.Points;
                for (var index = 1; index + 2 < points.Count; index++)
                {
                    var start = points[index];
                    var end = points[index + 1];
                    if (horizontal != Mathf.Abs(start.y - end.y) < 0.01f)
                        continue;
                    segments.Add(horizontal
                        ? new TrackSegment(route, index, start.y, Mathf.Min(start.x, end.x), Mathf.Max(start.x, end.x))
                        : new TrackSegment(route, index, start.x, Mathf.Min(start.y, end.y), Mathf.Max(start.y, end.y)));
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
            for (var index = 0; index < bundle.Count; index++)
            {
                var track = first + spacing * index;
                var segment = bundle[index];
                var points = segment.route.Points;
                points[segment.index] = WithAcross(points[segment.index], track, horizontal);
                points[segment.index + 1] = WithAcross(points[segment.index + 1], track, horizontal);
            }
        }

        private static Vector2 WithAcross(Vector2 point, float value, bool horizontal)
        {
            return horizontal ? new Vector2(point.x, value) : new Vector2(value, point.y);
        }

        private static int CompareTracks(TrackSegment left, TrackSegment right, bool horizontal)
        {
            if (TryAttachment(left, horizontal, out var leftAttachment)
                && TryAttachment(right, horizontal, out var rightAttachment)
                && leftAttachment.node == rightAttachment.node
                && leftAttachment.side == rightAttachment.side
                && leftAttachment.travel == rightAttachment.travel)
            {
                var order = (rightAttachment.port - leftAttachment.port) * leftAttachment.travel * leftAttachment.outward;
                if (Mathf.Abs(order) > 0.01f)
                    return order > 0f ? 1 : -1;
            }

            var key = TrackKey(left, horizontal).CompareTo(TrackKey(right, horizontal));
            return key != 0 ? key : string.CompareOrdinal(left.route.Id, right.route.Id);
        }

        private static float TrackKey(TrackSegment segment, bool horizontal)
        {
            var points = segment.route.Points;
            var before = points[segment.index - 1];
            var after = points[segment.index + 2];
            return horizontal ? before.y + after.y : before.x + after.x;
        }

        private static bool TryAttachment(TrackSegment segment, bool horizontal, out Attachment attachment)
        {
            var route = segment.route;
            var points = route.Points;
            if (segment.index == 1)
            {
                attachment = CreateAttachment(route.Request.SourceNodeId, route.SourceSide, points[1], points[2], horizontal);
                return true;
            }
            if (segment.index == points.Count - 3)
            {
                attachment = CreateAttachment(route.Request.TargetNodeId, route.TargetSide, points[points.Count - 2], points[points.Count - 3], horizontal);
                return true;
            }
            attachment = default;
            return false;
        }

        private static Attachment CreateAttachment(string node, string side, Vector2 near, Vector2 far, bool horizontal)
        {
            var delta = horizontal ? far.x - near.x : far.y - near.y;
            return new Attachment(
                node,
                side,
                delta >= 0f ? 1 : -1,
                side == "right" || side == "bottom" ? 1 : -1,
                horizontal ? near.x : near.y);
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
                var selfLoop = request.SourceNodeId == request.TargetNodeId;
                SourceSide = selfLoop ? "right" : ResolveSide(request.SourceSide, sourceRect, targetRect);
                TargetSide = selfLoop ? "top" : ResolveSide(request.TargetSide, targetRect, sourceRect);
                SourceAnchor = Anchor(sourceRect, SourceSide);
                TargetAnchor = Anchor(targetRect, TargetSide);
                SourceStub = SourceAnchor + DirectionVector(DirectionOf(SourceSide)) * Clearance;
                TargetStub = TargetAnchor + DirectionVector(DirectionOf(TargetSide)) * Clearance;
                Span = Mathf.Abs(SourceAnchor.x - TargetAnchor.x) + Mathf.Abs(SourceAnchor.y - TargetAnchor.y);
            }

            public ArchitectorRouteRequest Request { get; }
            public string Id => Request.Id;
            public Rect SourceRect { get; }
            public Rect TargetRect { get; }
            public string SourceSide { get; }
            public string TargetSide { get; }
            public Vector2 SourceAnchor { get; }
            public Vector2 TargetAnchor { get; }
            public Vector2 SourceStub { get; }
            public Vector2 TargetStub { get; }
            public float Span { get; }
            public List<Vector2> Points { get; set; }
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
            public TrackSegment(WorkRoute route, int index, float position, float min, float max)
            {
                this.route = route;
                this.index = index;
                this.position = position;
                this.min = min;
                this.max = max;
            }

            public readonly WorkRoute route;
            public readonly int index;
            public readonly float position;
            public readonly float min;
            public readonly float max;
        }

        private readonly struct Attachment
        {
            public Attachment(string node, string side, int travel, int outward, float port)
            {
                this.node = node;
                this.side = side;
                this.travel = travel;
                this.outward = outward;
                this.port = port;
            }

            public readonly string node;
            public readonly string side;
            public readonly int travel;
            public readonly int outward;
            public readonly float port;
        }
    }
}
