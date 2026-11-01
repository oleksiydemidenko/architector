using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Architector.Editor
{
    public readonly struct ArchitectorArrangeNode
    {
        public ArchitectorArrangeNode(string id, float x, float y, float width, float height, bool portal)
        {
            Id = id;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Portal = portal;
        }

        public string Id { get; }
        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }
        public bool Portal { get; }
    }

    public readonly struct ArchitectorArrangeEdge
    {
        public ArchitectorArrangeEdge(string sourceNodeId, string targetNodeId, string kind = ArchitectorEdgeKinds.Dependency)
        {
            SourceNodeId = sourceNodeId;
            TargetNodeId = targetNodeId;
            Kind = kind;
        }

        public string SourceNodeId { get; }
        public string TargetNodeId { get; }
        public string Kind { get; }
    }

    public static class ArchitectorGraphArranger
    {
        private const int MaximumColumnLength = 4;
        private const int MinimumRows = 3;
        private const int MinimumHubDegree = 4;
        private const int SwapSweeps = 8;
        private const int CrossingEdgeLimit = 40;
        private const int RouteEvaluations = 80;
        private const float RouteCrossingCost = 64f;
        private const float RouteBendCost = 64f;

        public static Dictionary<string, ArchitectorPoint> Arrange(
            IReadOnlyList<ArchitectorArrangeNode> nodes,
            IReadOnlyList<ArchitectorArrangeEdge> edges,
            float gap,
            float grid,
            int maxGroupRelationships,
            IReadOnlyCollection<string> centers = null,
            ArchitectorRelationshipMerging merging = ArchitectorRelationshipMerging.Off)
        {
            var result = new Dictionary<string, ArchitectorPoint>(StringComparer.Ordinal);
            if (nodes.Count == 0)
                return result;

            var graph = new Graph(nodes, edges);
            var groups = centers != null && centers.Count > 0
                ? GroupAroundCenters(graph, centers)
                : GroupByLimit(graph, maxGroupRelationships);
            var placements = OrderGroups(groups, graph).Select(group => LayOut(group, graph, gap, grid)).ToList();
            ImproveByRoutes(placements, graph, nodes, edges, gap, grid, merging);
            Pack(placements.Select(placement => ToBlock(placement.Cells, graph, gap, grid)).ToList(), nodes, gap, grid, result);
            return result;
        }

        private static void ImproveByRoutes(
            IReadOnlyList<Placement> placements,
            Graph graph,
            IReadOnlyList<ArchitectorArrangeNode> nodes,
            IReadOnlyList<ArchitectorArrangeEdge> edges,
            float gap,
            float grid,
            ArchitectorRelationshipMerging merging)
        {
            var requests = edges
                .Where(edge => edge.SourceNodeId != edge.TargetNodeId
                               && graph.Nodes.ContainsKey(edge.SourceNodeId)
                               && graph.Nodes.ContainsKey(edge.TargetNodeId))
                .GroupBy(edge => (edge.SourceNodeId, edge.TargetNodeId))
                .OrderBy(group => group.Key.SourceNodeId, StringComparer.Ordinal)
                .ThenBy(group => group.Key.TargetNodeId, StringComparer.Ordinal)
                .SelectMany(group => group.Select((edge, index) => new ArchitectorRouteRequest(
                    $"{edge.SourceNodeId}>{edge.TargetNodeId}#{index}",
                    edge.SourceNodeId,
                    edge.TargetNodeId,
                    "auto",
                    "auto",
                    edge.Kind)))
                .ToArray();
            if (requests.Length < 2)
                return;

            var router = new ArchitectorEdgeRouter();
            var cost = RouteCost(router, placements, graph, nodes, requests, gap, grid, out var busy, merging);
            var evaluations = 0;
            var improved = true;
            while (improved && evaluations < RouteEvaluations)
            {
                improved = false;
                foreach (var pair in SwapCandidates(placements, graph, busy))
                {
                    if (evaluations >= RouteEvaluations)
                        break;
                    var cells = pair.placement.Cells;
                    var occupants = pair.placement.Occupants();
                    occupants.TryGetValue(pair.first, out var firstId);
                    occupants.TryGetValue(pair.second, out var secondId);
                    if (firstId == null && secondId == null)
                        continue;

                    Place(cells, occupants, pair.first, secondId);
                    Place(cells, occupants, pair.second, firstId);
                    evaluations++;
                    var nextCost = RouteCost(router, placements, graph, nodes, requests, gap, grid, out var nextBusy, merging);
                    if (nextCost < cost - 0.001f)
                    {
                        cost = nextCost;
                        busy = nextBusy;
                        improved = true;
                        break;
                    }
                    Place(cells, occupants, pair.first, firstId);
                    Place(cells, occupants, pair.second, secondId);
                }
            }
        }

        private static IEnumerable<(Placement placement, Vector2Int first, Vector2Int second)> SwapCandidates(
            IReadOnlyList<Placement> placements,
            Graph graph,
            IReadOnlyDictionary<string, int> busy)
        {
            var candidates = new List<(Placement placement, Vector2Int first, Vector2Int second, int busy, int distance)>();
            foreach (var placement in placements)
            {
                var occupants = placement.Occupants();
                foreach (var first in placement.FreeCells)
                {
                    if (!occupants.TryGetValue(first, out var id) || !busy.TryGetValue(id, out var crossings) || crossings == 0)
                        continue;
                    foreach (var second in placement.FreeCells.Where(second => second != first))
                        candidates.Add((placement, first, second, crossings * 1000 + graph.Order.Count - graph.Rank[id], Mathf.Abs(first.x - second.x) + Mathf.Abs(first.y - second.y)));
                }
            }
            return candidates
                .OrderByDescending(candidate => candidate.busy)
                .ThenBy(candidate => candidate.distance)
                .ThenBy(candidate => candidate.second.x)
                .ThenBy(candidate => candidate.second.y)
                .Select(candidate => (candidate.placement, candidate.first, candidate.second));
        }

        private static float RouteCost(
            ArchitectorEdgeRouter router,
            IReadOnlyList<Placement> placements,
            Graph graph,
            IReadOnlyList<ArchitectorArrangeNode> nodes,
            IReadOnlyList<ArchitectorRouteRequest> requests,
            float gap,
            float grid,
            out Dictionary<string, int> busy,
            ArchitectorRelationshipMerging merging)
        {
            var positions = new Dictionary<string, ArchitectorPoint>(StringComparer.Ordinal);
            Pack(placements.Select(placement => ToBlock(placement.Cells, graph, gap, grid)).ToList(), nodes, gap, grid, positions);
            var rects = positions.ToDictionary(
                pair => pair.Key,
                pair => new Rect(pair.Value.x, pair.Value.y, graph.Nodes[pair.Key].Width, graph.Nodes[pair.Key].Height),
                StringComparer.Ordinal);
            var network = merging == ArchitectorRelationshipMerging.Off ? null : router.RouteMerged(rects, requests, merging);
            var routes = network == null ? router.Route(rects, requests, false) : network.Strokes.ToDictionary(stroke => stroke.Id, stroke => stroke.Points, StringComparer.Ordinal);
            var crossings = ArchitectorEdgeRouter.CrossingCounts(routes);
            busy = new Dictionary<string, int>(StringComparer.Ordinal);
            var cost = 0f;
            if (network == null)
            {
                foreach (var request in requests)
                {
                    var route = routes[request.Id];
                    var count = crossings[request.Id];
                    cost += count * RouteCrossingCost * 0.5f + (route.Count - 2) * RouteBendCost;
                    for (var index = 1; index < route.Count; index++)
                        cost += Mathf.Abs(route[index].x - route[index - 1].x) + Mathf.Abs(route[index].y - route[index - 1].y);
                    busy[request.SourceNodeId] = busy.GetValueOrDefault(request.SourceNodeId) + count;
                    busy[request.TargetNodeId] = busy.GetValueOrDefault(request.TargetNodeId) + count;
                }
            }
            else
            {
                var requestsById = requests.ToDictionary(request => request.Id, StringComparer.Ordinal);
                foreach (var stroke in network.Strokes)
                {
                    var route = stroke.Points;
                    var count = crossings[stroke.Id];
                    cost += count * RouteCrossingCost * 0.5f + (route.Count - 2) * RouteBendCost;
                    for (var index = 1; index < route.Count; index++)
                        cost += Mathf.Abs(route[index].x - route[index - 1].x) + Mathf.Abs(route[index].y - route[index - 1].y);
                    foreach (var id in stroke.EdgeIds)
                    {
                        var request = requestsById[id];
                        busy[request.SourceNodeId] = busy.GetValueOrDefault(request.SourceNodeId) + count;
                        busy[request.TargetNodeId] = busy.GetValueOrDefault(request.TargetNodeId) + count;
                    }
                }
            }
            return cost + Balance(placements) * gap;
        }

        private static int Balance(IEnumerable<Placement> placements)
        {
            var total = 0;
            foreach (var placement in placements.Where(placement => placement.FreeCells.Count > 0))
                total += Mathf.Abs(placement.Cells.Values.Sum(cell => Math.Sign(cell.x)));
            return total;
        }

        private static List<Group> GroupByLimit(Graph graph, int limit)
        {
            var groups = new List<Group>();
            var owners = new Dictionary<string, Group>(StringComparer.Ordinal);
            foreach (var component in graph.Components())
            {
                var relationships = component.Sum(id => graph.Links[id].Values.Sum()) / 2;
                if (relationships <= limit)
                {
                    var group = new Group(component[0]);
                    group.Members.AddRange(component.Skip(1));
                    groups.Add(group);
                    foreach (var id in component)
                        owners[id] = group;
                    continue;
                }
                SplitComponent(component, graph, limit, groups, owners);
            }

            foreach (var id in graph.Order.Where(id => !owners.ContainsKey(id) && graph.Links[id].Count > 0))
            {
                var best = graph.Links[id]
                    .Where(link => owners.ContainsKey(link.Key))
                    .OrderByDescending(link => link.Value)
                    .ThenBy(link => graph.Rank[link.Key])
                    .First();
                owners[id] = owners[best.Key];
                owners[id].Members.Add(id);
            }

            AddLooseGroup(graph, owners, groups);
            return groups;
        }

        private static void SplitComponent(
            IReadOnlyList<string> component,
            Graph graph,
            int limit,
            ICollection<Group> groups,
            IDictionary<string, Group> owners)
        {
            var hubDegree = Mathf.Max(MinimumHubDegree, limit / 3);
            foreach (var id in component)
            {
                if (owners.ContainsKey(id) || graph.Links[id].Keys.All(owners.ContainsKey))
                    continue;
                var group = new Group(id);
                owners[id] = group;
                groups.Add(group);
                Grow(group, graph, limit, hubDegree, owners);
            }
        }

        private static void Grow(Group group, Graph graph, int limit, int hubDegree, IDictionary<string, Group> owners)
        {
            var relationships = 0;
            while (true)
            {
                string best = null;
                var bestLinks = 0;
                var bestScore = -1;
                foreach (var member in group.Members)
                {
                    foreach (var neighbour in graph.Links[member].Keys)
                    {
                        if (owners.ContainsKey(neighbour) || graph.Links[neighbour].Count >= hubDegree)
                            continue;
                        var links = group.Members.Sum(other => graph.LinkCount(other, neighbour));
                        var score = links * 2 + (graph.LinkCount(group.Center, neighbour) > 0 ? 1 : 0);
                        if (score > bestScore || score == bestScore && graph.Rank[neighbour] < graph.Rank[best])
                        {
                            best = neighbour;
                            bestLinks = links;
                            bestScore = score;
                        }
                    }
                }
                if (best == null || relationships + bestLinks > limit)
                    return;
                relationships += bestLinks;
                group.Members.Add(best);
                owners[best] = group;
            }
        }

        private static List<Group> GroupAroundCenters(Graph graph, IReadOnlyCollection<string> centers)
        {
            var groups = new List<Group>();
            var owners = new Dictionary<string, Group>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (var center in centers.Where(graph.Rank.ContainsKey).OrderBy(id => graph.Rank[id]))
            {
                var group = new Group(center);
                groups.Add(group);
                owners[center] = group;
                queue.Enqueue(center);
            }
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var neighbour in graph.Links[current].Keys.OrderBy(id => graph.Rank[id]))
                {
                    if (owners.ContainsKey(neighbour))
                        continue;
                    owners[neighbour] = owners[current];
                    owners[neighbour].Members.Add(neighbour);
                    queue.Enqueue(neighbour);
                }
            }
            AddLooseGroup(graph, owners, groups);
            return groups;
        }

        private static void AddLooseGroup(Graph graph, IDictionary<string, Group> owners, ICollection<Group> groups)
        {
            var loose = graph.Order.Where(id => !owners.ContainsKey(id)).ToList();
            if (loose.Count == 0)
                return;
            var group = new Group(null);
            group.Members.AddRange(loose);
            groups.Add(group);
        }

        private static IEnumerable<Group> OrderGroups(List<Group> groups, Graph graph)
        {
            var remaining = groups.Where(group => group.Center != null).OrderByDescending(group => group.Members.Count).ToList();
            var placed = new List<Group>();
            while (remaining.Count > 0)
            {
                var next = remaining
                    .OrderByDescending(group => placed.Sum(other => graph.LinksBetween(group, other)))
                    .First();
                remaining.Remove(next);
                placed.Add(next);
            }
            return placed.Concat(groups.Where(group => group.Center == null));
        }

        private static Placement LayOut(Group group, Graph graph, float gap, float grid)
        {
            if (group.Center == null)
                return LayOutLoose(group, graph);

            var members = new HashSet<string>(group.Members, StringComparer.Ordinal);
            var cells = new Dictionary<string, Vector2Int>(StringComparer.Ordinal);
            foreach (var (id, row) in BuildColumn(group.Center, graph, members))
                cells[id] = new Vector2Int(0, row);
            var others = group.Members
                .Where(id => !cells.ContainsKey(id))
                .OrderByDescending(id => cells.Keys.Sum(placed => graph.LinkCount(placed, id)))
                .ThenBy(id => graph.Rank[id])
                .ToList();
            var freeCells = BuildSideCells(others.Count);
            for (var index = 0; index < others.Count; index++)
                cells[others[index]] = freeCells[index];

            var cellWidth = group.Members.Max(id => graph.Nodes[id].Width) + gap;
            var cellHeight = group.Members.Max(id => graph.Nodes[id].Height) + gap;
            ImprovePlacement(group, graph, cells, freeCells, cellWidth, cellHeight);
            return new Placement(cells, freeCells);
        }

        private static IEnumerable<(string id, int row)> BuildColumn(string center, Graph graph, ISet<string> members)
        {
            yield return (center, 0);
            if (graph.Nodes[center].Portal)
                yield break;

            var down = LongestChain(center, graph, members, new HashSet<string>(StringComparer.Ordinal) { center }, MaximumColumnLength - 2);
            for (var index = 0; index < down.Count; index++)
                yield return (down[index], index + 1);

            var above = graph.Links[center].Keys
                .Where(id => members.Contains(id) && !graph.Nodes[id].Portal && !down.Contains(id))
                .OrderBy(id => graph.Links[id].Count)
                .ThenBy(id => graph.Rank[id])
                .FirstOrDefault();
            if (above != null)
                yield return (above, -1);
        }

        private static List<string> LongestChain(string from, Graph graph, ISet<string> members, ISet<string> visited, int depth)
        {
            var best = new List<string>();
            if (depth <= 0)
                return best;
            foreach (var next in graph.Outgoing[from])
            {
                if (!members.Contains(next) || graph.Nodes[next].Portal || !visited.Add(next))
                    continue;
                var chain = LongestChain(next, graph, members, visited, depth - 1);
                visited.Remove(next);
                if (chain.Count + 1 > best.Count)
                {
                    best = new List<string> { next };
                    best.AddRange(chain);
                }
            }
            return best;
        }

        private static List<Vector2Int> BuildSideCells(int count)
        {
            var cells = new List<Vector2Int>();
            if (count == 0)
                return cells;
            var rows = Mathf.Max(MinimumRows, Mathf.CeilToInt(Mathf.Sqrt(count * 0.5f)));
            var columns = Mathf.Max(1, Mathf.CeilToInt(count / (2f * rows)));
            var top = -(rows - 1) / 2;
            for (var column = 1; column <= columns; column++)
            {
                for (var row = top; row < top + rows; row++)
                {
                    cells.Add(new Vector2Int(-column, row));
                    cells.Add(new Vector2Int(column, row));
                }
            }
            return cells
                .OrderBy(cell => Mathf.Abs(cell.x) * 2 + Mathf.Abs(cell.y))
                .ThenBy(cell => cell.y)
                .ThenBy(cell => cell.x)
                .ToList();
        }

        private static void ImprovePlacement(
            Group group,
            Graph graph,
            IDictionary<string, Vector2Int> cells,
            IReadOnlyList<Vector2Int> freeCells,
            float cellWidth,
            float cellHeight)
        {
            if (freeCells.Count < 2)
                return;

            var occupants = new Dictionary<Vector2Int, string>();
            foreach (var pair in cells)
                occupants[pair.Value] = pair.Key;
            var links = group.Members
                .SelectMany(source => graph.Links[source]
                    .Where(link => cells.ContainsKey(link.Key) && string.CompareOrdinal(source, link.Key) < 0)
                    .Select(link => (source, target: link.Key, weight: link.Value)))
                .ToArray();

            var cost = PlacementCost(links, cells, occupants, cellWidth, cellHeight);
            for (var sweep = 0; sweep < SwapSweeps; sweep++)
            {
                var improved = false;
                for (var first = 0; first < freeCells.Count; first++)
                {
                    for (var second = first + 1; second < freeCells.Count; second++)
                    {
                        var firstCell = freeCells[first];
                        var secondCell = freeCells[second];
                        occupants.TryGetValue(firstCell, out var firstId);
                        occupants.TryGetValue(secondCell, out var secondId);
                        if (firstId == null && secondId == null)
                            continue;

                        Place(cells, occupants, firstCell, secondId);
                        Place(cells, occupants, secondCell, firstId);
                        var nextCost = PlacementCost(links, cells, occupants, cellWidth, cellHeight);
                        if (nextCost < cost - 0.001f)
                        {
                            cost = nextCost;
                            improved = true;
                            continue;
                        }
                        Place(cells, occupants, firstCell, firstId);
                        Place(cells, occupants, secondCell, secondId);
                    }
                }
                if (!improved)
                    return;
            }
        }

        private static void Place(
            IDictionary<string, Vector2Int> cells,
            IDictionary<Vector2Int, string> occupants,
            Vector2Int cell,
            string id)
        {
            if (id == null)
            {
                occupants.Remove(cell);
                return;
            }
            occupants[cell] = id;
            cells[id] = cell;
        }

        private static float PlacementCost(
            IReadOnlyList<(string source, string target, int weight)> links,
            IDictionary<string, Vector2Int> cells,
            IDictionary<Vector2Int, string> occupants,
            float cellWidth,
            float cellHeight)
        {
            var cost = 0f;
            foreach (var (source, target, weight) in links)
            {
                var from = cells[source];
                var to = cells[target];
                cost += (Mathf.Abs(from.x - to.x) * cellWidth + Mathf.Abs(from.y - to.y) * cellHeight) * weight;
                cost += BlockedCells(from, to, occupants) * cellWidth * 0.5f;
            }
            if (links.Count <= CrossingEdgeLimit)
            {
                for (var first = 0; first < links.Count; first++)
                {
                    for (var second = first + 1; second < links.Count; second++)
                    {
                        if (Cross(cells[links[first].source], cells[links[first].target], cells[links[second].source], cells[links[second].target]))
                            cost += cellHeight * 0.5f;
                    }
                }
            }
            var balance = 0;
            foreach (var cell in cells.Values)
                balance += Math.Sign(cell.x);
            return cost + Mathf.Abs(balance) * cellWidth * 0.1f;
        }

        private static int BlockedCells(Vector2Int from, Vector2Int to, IDictionary<Vector2Int, string> occupants)
        {
            var blocked = 0;
            if (from.y == to.y)
            {
                for (var x = Mathf.Min(from.x, to.x) + 1; x < Mathf.Max(from.x, to.x); x++)
                    blocked += occupants.ContainsKey(new Vector2Int(x, from.y)) ? 1 : 0;
            }
            else if (from.x == to.x)
            {
                for (var y = Mathf.Min(from.y, to.y) + 1; y < Mathf.Max(from.y, to.y); y++)
                    blocked += occupants.ContainsKey(new Vector2Int(from.x, y)) ? 1 : 0;
            }
            return blocked;
        }

        private static bool Cross(Vector2Int a, Vector2Int b, Vector2Int c, Vector2Int d)
        {
            if (a == c || a == d || b == c || b == d)
                return false;
            return Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;
        }

        private static int Side(Vector2Int a, Vector2Int b, Vector2Int point)
        {
            return Math.Sign((b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x));
        }

        private static Block ToBlock(IReadOnlyDictionary<string, Vector2Int> cells, Graph graph, float gap, float grid)
        {
            var columns = Slots(cells, graph, gap, grid, true);
            var rows = Slots(cells, graph, gap, grid, false);
            var block = new Block
            {
                Width = columns.Values.Max(slot => slot.start + slot.size) - gap,
                Height = rows.Values.Max(slot => slot.start + slot.size) - gap
            };
            foreach (var pair in cells)
            {
                var node = graph.Nodes[pair.Key];
                var column = columns[pair.Value.x];
                var row = rows[pair.Value.y];
                block.Positions[pair.Key] = new Vector2(
                    column.start + SnapDown((column.size - gap - node.Width) * 0.5f, grid),
                    row.start + SnapDown((row.size - gap - node.Height) * 0.5f, grid));
            }
            return block;
        }

        private static Dictionary<int, (float start, float size)> Slots(
            IReadOnlyDictionary<string, Vector2Int> cells,
            Graph graph,
            float gap,
            float grid,
            bool horizontal)
        {
            var sizes = new SortedDictionary<int, float>();
            foreach (var pair in cells)
            {
                var index = horizontal ? pair.Value.x : pair.Value.y;
                var size = horizontal ? graph.Nodes[pair.Key].Width : graph.Nodes[pair.Key].Height;
                sizes[index] = sizes.TryGetValue(index, out var known) ? Mathf.Max(known, size) : size;
            }
            var slots = new Dictionary<int, (float start, float size)>();
            var cursor = 0f;
            foreach (var pair in sizes)
            {
                var size = SnapUp(pair.Value + gap, grid);
                slots[pair.Key] = (cursor, size);
                cursor += size;
            }
            return slots;
        }

        private static Placement LayOutLoose(Group group, Graph graph)
        {
            var columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(group.Members.Count)));
            var cells = new Dictionary<string, Vector2Int>(StringComparer.Ordinal);
            for (var index = 0; index < group.Members.Count; index++)
                cells[group.Members[index]] = new Vector2Int(index % columns, index / columns);
            return new Placement(cells, Array.Empty<Vector2Int>());
        }

        private static void Pack(
            IReadOnlyList<Block> blocks,
            IReadOnlyList<ArchitectorArrangeNode> nodes,
            float gap,
            float grid,
            IDictionary<string, ArchitectorPoint> result)
        {
            var originX = SnapDown(nodes.Min(node => node.X), grid);
            var originY = SnapDown(nodes.Min(node => node.Y), grid);
            var spacing = SnapUp(gap * 2f, grid);
            var shelfWidth = Mathf.Max(
                blocks.Max(block => block.Width),
                Mathf.Sqrt(blocks.Sum(block => (block.Width + spacing) * (block.Height + spacing))) * 1.4f);
            var x = 0f;
            var y = 0f;
            var shelfHeight = 0f;
            foreach (var block in blocks)
            {
                if (x > 0f && x + block.Width > shelfWidth)
                {
                    x = 0f;
                    y += shelfHeight + spacing;
                    shelfHeight = 0f;
                }
                foreach (var pair in block.Positions)
                    result[pair.Key] = new ArchitectorPoint(originX + x + pair.Value.x, originY + y + pair.Value.y);
                x += SnapUp(block.Width, grid) + spacing;
                shelfHeight = Mathf.Max(shelfHeight, SnapUp(block.Height, grid));
            }
        }

        private static float SnapUp(float value, float grid)
        {
            return Mathf.Ceil(value / grid - 0.0001f) * grid;
        }

        private static float SnapDown(float value, float grid)
        {
            return Mathf.Floor(value / grid + 0.0001f) * grid;
        }

        private sealed class Group
        {
            public Group(string center)
            {
                Center = center;
                if (center != null)
                    Members.Add(center);
            }

            public string Center { get; }
            public List<string> Members { get; } = new();
        }

        private sealed class Placement
        {
            public Placement(Dictionary<string, Vector2Int> cells, IReadOnlyList<Vector2Int> freeCells)
            {
                Cells = cells;
                FreeCells = freeCells;
            }

            public Dictionary<string, Vector2Int> Cells { get; }
            public IReadOnlyList<Vector2Int> FreeCells { get; }

            public Dictionary<Vector2Int, string> Occupants()
            {
                var occupants = new Dictionary<Vector2Int, string>();
                foreach (var pair in Cells)
                    occupants[pair.Value] = pair.Key;
                return occupants;
            }
        }

        private sealed class Block
        {
            public float Width { get; set; }
            public float Height { get; set; }
            public Dictionary<string, Vector2> Positions { get; } = new(StringComparer.Ordinal);
        }

        private sealed class Graph
        {
            public Graph(IReadOnlyList<ArchitectorArrangeNode> nodes, IReadOnlyList<ArchitectorArrangeEdge> edges)
            {
                foreach (var node in nodes)
                {
                    Nodes[node.Id] = node;
                    Links[node.Id] = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    Outgoing[node.Id] = new List<string>();
                }
                foreach (var edge in edges)
                {
                    if (edge.SourceNodeId == edge.TargetNodeId
                        || !Nodes.ContainsKey(edge.SourceNodeId)
                        || !Nodes.ContainsKey(edge.TargetNodeId))
                        continue;
                    Links[edge.SourceNodeId].TryGetValue(edge.TargetNodeId, out var count);
                    Links[edge.SourceNodeId][edge.TargetNodeId] = count + 1;
                    Links[edge.TargetNodeId][edge.SourceNodeId] = count + 1;
                    if (!Outgoing[edge.SourceNodeId].Contains(edge.TargetNodeId))
                        Outgoing[edge.SourceNodeId].Add(edge.TargetNodeId);
                }
                foreach (var targets in Outgoing.Values)
                    targets.Sort(StringComparer.Ordinal);
                Order = Nodes.Keys
                    .OrderByDescending(id => Links[id].Values.Sum())
                    .ThenBy(id => id, StringComparer.Ordinal)
                    .ToList();
                for (var index = 0; index < Order.Count; index++)
                    Rank[Order[index]] = index;
            }

            public Dictionary<string, ArchitectorArrangeNode> Nodes { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, SortedDictionary<string, int>> Links { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, List<string>> Outgoing { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, int> Rank { get; } = new(StringComparer.Ordinal);
            public List<string> Order { get; }

            public int LinkCount(string first, string second)
            {
                return Links[first].TryGetValue(second, out var count) ? count : 0;
            }

            public int LinksBetween(Group first, Group second)
            {
                return first.Members.Sum(source => second.Members.Sum(target => LinkCount(source, target)));
            }

            public List<List<string>> Components()
            {
                var components = new List<List<string>>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                foreach (var start in Order.Where(id => Links[id].Count > 0))
                {
                    if (!visited.Add(start))
                        continue;
                    var component = new List<string>();
                    var stack = new Stack<string>();
                    stack.Push(start);
                    while (stack.Count > 0)
                    {
                        var current = stack.Pop();
                        component.Add(current);
                        foreach (var next in Links[current].Keys)
                        {
                            if (visited.Add(next))
                                stack.Push(next);
                        }
                    }
                    components.Add(component.OrderBy(id => Rank[id]).ToList());
                }
                return components;
            }
        }
    }
}
