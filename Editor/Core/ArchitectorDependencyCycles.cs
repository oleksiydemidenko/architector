using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public readonly struct ArchitectorEndpoint
    {
        public ArchitectorEndpoint(string layerId, string nodeId)
        {
            LayerId = layerId;
            NodeId = nodeId;
        }

        public string LayerId { get; }
        public string NodeId { get; }
        internal string Key => $"{LayerId}\u001f{NodeId}";
    }

    public sealed class ArchitectorDependencyCycle
    {
        internal ArchitectorDependencyCycle(
            IReadOnlyList<ArchitectorEndpoint> members,
            IReadOnlyList<ArchitectorEdgeRecord> path)
        {
            Members = members;
            Path = path;
            LayerIds = members.Select(member => member.LayerId).Distinct(StringComparer.Ordinal).ToArray();
            memberKeys = new HashSet<string>(members.Select(member => member.Key), StringComparer.Ordinal);
        }

        private readonly HashSet<string> memberKeys;

        public IReadOnlyList<ArchitectorEndpoint> Members { get; }
        public IReadOnlyList<ArchitectorEdgeRecord> Path { get; }
        public IReadOnlyList<string> LayerIds { get; }

        public bool Contains(ArchitectorEdgeRecord relationship) =>
            string.Equals(relationship.kind, ArchitectorEdgeKinds.Dependency, StringComparison.Ordinal)
            && memberKeys.Contains(new ArchitectorEndpoint(relationship.sourceLayerId, relationship.sourceNodeId).Key)
            && memberKeys.Contains(new ArchitectorEndpoint(relationship.targetLayerId, relationship.targetNodeId).Key);

        public string Describe(Func<string, string, string> caption)
        {
            var steps = Path.Select(edge => caption(edge.sourceLayerId, edge.sourceNodeId))
                .Append(caption(Path[0].sourceLayerId, Path[0].sourceNodeId));
            return string.Join(" → ", steps);
        }
    }

    public static class ArchitectorDependencyCycles
    {
        public static IReadOnlyList<ArchitectorDependencyCycle> Find(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers)
        {
            var documents = InExplorerOrder(project)
                .Where(layer => layers.ContainsKey(layer.id))
                .Select(layer => layers[layer.id].semantic)
                .ToArray();
            return Find(
                documents.SelectMany(semantic => semantic.nodes.Select(node => new ArchitectorEndpoint(semantic.layerId, node.id))),
                documents.SelectMany(semantic => semantic.edges));
        }

        public static IReadOnlyList<ArchitectorDependencyCycle> Find(
            IEnumerable<ArchitectorEndpoint> endpoints,
            IEnumerable<ArchitectorEdgeRecord> relationships)
        {
            var known = new Dictionary<string, ArchitectorEndpoint>(StringComparer.Ordinal);
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var endpoint in endpoints)
            {
                known[endpoint.Key] = endpoint;
                if (!order.ContainsKey(endpoint.Key))
                    order[endpoint.Key] = order.Count;
            }
            var outgoing = known.Keys.ToDictionary(key => key, _ => new List<ArchitectorEdgeRecord>(), StringComparer.Ordinal);
            foreach (var relationship in relationships.Where(relationship =>
                         string.Equals(relationship.kind, ArchitectorEdgeKinds.Dependency, StringComparison.Ordinal)))
            {
                var source = new ArchitectorEndpoint(relationship.sourceLayerId, relationship.sourceNodeId).Key;
                var target = new ArchitectorEndpoint(relationship.targetLayerId, relationship.targetNodeId).Key;
                if (outgoing.ContainsKey(source) && outgoing.ContainsKey(target))
                    outgoing[source].Add(relationship);
            }
            foreach (var edges in outgoing.Values)
                edges.Sort((first, second) =>
                {
                    var byTarget = order[TargetKey(first)].CompareTo(order[TargetKey(second)]);
                    return byTarget != 0 ? byTarget : string.CompareOrdinal(first.id, second.id);
                });

            return StronglyConnectedComponents(order.OrderBy(pair => pair.Value).Select(pair => pair.Key), outgoing)
                .Where(component => component.Count > 1)
                .Select(component => component.OrderBy(key => order[key]).ToArray())
                .OrderBy(component => order[component[0]])
                .Select(component => new ArchitectorDependencyCycle(
                    component.Select(key => known[key]).ToArray(),
                    CyclePath(component, outgoing)))
                .ToArray();
        }

        internal static IEnumerable<ArchitectorLayerRecord> InExplorerOrder(ArchitectorProjectDocument project)
        {
            var ordered = ArchitectorDigest.OrderedLayers(project).ToList();
            var listed = new HashSet<string>(ordered.Select(layer => layer.id), StringComparer.Ordinal);
            return ordered.Concat(project.layers.Where(layer => !listed.Contains(layer.id)));
        }

        public static string Caption(
            ArchitectorProjectDocument project,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers,
            string layerId,
            string nodeId)
        {
            var layer = project.layers.FirstOrDefault(candidate => string.Equals(candidate.id, layerId, StringComparison.Ordinal));
            var node = layers.TryGetValue(layerId, out var documents)
                ? documents.semantic.nodes.FirstOrDefault(candidate => string.Equals(candidate.id, nodeId, StringComparison.Ordinal))
                : null;
            var nodeTitle = string.IsNullOrEmpty(node?.title) ? nodeId : node.title;
            return $"{layer?.name ?? layerId}/{nodeTitle}";
        }

        private static string TargetKey(ArchitectorEdgeRecord edge) =>
            new ArchitectorEndpoint(edge.targetLayerId, edge.targetNodeId).Key;

        private static List<List<string>> StronglyConnectedComponents(
            IEnumerable<string> roots,
            IReadOnlyDictionary<string, List<ArchitectorEdgeRecord>> outgoing)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var lowLink = new Dictionary<string, int>(StringComparer.Ordinal);
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            var components = new List<List<string>>();
            var next = 0;
            foreach (var root in roots)
            {
                if (index.ContainsKey(root))
                    continue;
                var work = new Stack<(string node, int edge)>();
                work.Push((root, 0));
                index[root] = lowLink[root] = next++;
                stack.Push(root);
                onStack.Add(root);
                while (work.Count > 0)
                {
                    var (node, edge) = work.Pop();
                    var edges = outgoing[node];
                    if (edge < edges.Count)
                    {
                        work.Push((node, edge + 1));
                        var target = TargetKey(edges[edge]);
                        if (!index.ContainsKey(target))
                        {
                            index[target] = lowLink[target] = next++;
                            stack.Push(target);
                            onStack.Add(target);
                            work.Push((target, 0));
                        }
                        else if (onStack.Contains(target))
                            lowLink[node] = Math.Min(lowLink[node], index[target]);
                        continue;
                    }
                    if (work.Count > 0)
                    {
                        var parent = work.Peek().node;
                        lowLink[parent] = Math.Min(lowLink[parent], lowLink[node]);
                    }
                    if (lowLink[node] != index[node])
                        continue;
                    var component = new List<string>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    } while (!string.Equals(member, node, StringComparison.Ordinal));
                    components.Add(component);
                }
            }
            return components;
        }

        private static IReadOnlyList<ArchitectorEdgeRecord> CyclePath(
            IReadOnlyList<string> component,
            IReadOnlyDictionary<string, List<ArchitectorEdgeRecord>> outgoing)
        {
            var members = new HashSet<string>(component, StringComparer.Ordinal);
            var start = component[0];
            var previous = new Dictionary<string, ArchitectorEdgeRecord>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            pending.Enqueue(start);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                foreach (var edge in outgoing[current])
                {
                    var target = TargetKey(edge);
                    if (!members.Contains(target))
                        continue;
                    if (string.Equals(target, start, StringComparison.Ordinal))
                    {
                        var path = new List<ArchitectorEdgeRecord> { edge };
                        for (var step = current; !string.Equals(step, start, StringComparison.Ordinal); step = new ArchitectorEndpoint(previous[step].sourceLayerId, previous[step].sourceNodeId).Key)
                            path.Add(previous[step]);
                        path.Reverse();
                        return path;
                    }
                    if (previous.ContainsKey(target))
                        continue;
                    previous[target] = edge;
                    pending.Enqueue(target);
                }
            }
            throw new InvalidOperationException("A strongly connected component has no cycle through its first member.");
        }
    }
}
