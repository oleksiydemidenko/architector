using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorEdgeRouterTests
    {
        private static ArchitectorRouteRequest Edge(string source, string target)
        {
            return new ArchitectorRouteRequest($"{source}>{target}", source, target, "auto", "auto");
        }

        private static Dictionary<string, Rect> Grid(int columns, int rows)
        {
            var nodes = new Dictionary<string, Rect>();
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                    nodes[$"n{x}{y}"] = new Rect(x * 480f, y * 320f, 280f, 160f);
            }
            return nodes;
        }

        private static IEnumerable<(Vector2 start, Vector2 end)> Segments(IReadOnlyList<Vector2> route)
        {
            for (var index = 1; index < route.Count; index++)
                yield return (route[index - 1], route[index]);
        }

        [Test]
        public void RoutesAreOrthogonalAndKeepOutOfUnrelatedNodes()
        {
            var nodes = Grid(4, 3);
            var requests = new[] { Edge("n00", "n32"), Edge("n30", "n02"), Edge("n01", "n31"), Edge("n11", "n21"), Edge("n10", "n12") };

            var routes = new ArchitectorEdgeRouter().Route(nodes, requests);

            Assert.That(routes, Has.Count.EqualTo(requests.Length));
            foreach (var request in requests)
            {
                foreach (var (start, end) in Segments(routes[request.Id]))
                {
                    Assert.That(Mathf.Min(Mathf.Abs(start.x - end.x), Mathf.Abs(start.y - end.y)), Is.LessThan(0.01f), request.Id);
                    var bounds = Rect.MinMaxRect(
                        Mathf.Min(start.x, end.x), Mathf.Min(start.y, end.y),
                        Mathf.Max(start.x, end.x), Mathf.Max(start.y, end.y));
                    foreach (var node in nodes.Where(node => node.Key != request.SourceNodeId && node.Key != request.TargetNodeId))
                        Assert.That(bounds.Overlaps(node.Value), Is.False, $"{request.Id} crosses {node.Key}");
                }
            }
        }

        [Test]
        public void EveryRelationshipIntoANodeEndsAtItsOwnPoint()
        {
            var nodes = Grid(3, 3);
            var requests = new[] { Edge("n00", "n11"), Edge("n01", "n11"), Edge("n02", "n11"), Edge("n11", "n21") };

            var routes = new ArchitectorEdgeRouter().Route(nodes, requests);

            var tips = requests.Where(request => request.TargetNodeId == "n11").Select(request => routes[request.Id].Last()).ToArray();
            Assert.That(tips.Distinct().Count(), Is.EqualTo(tips.Length));
        }

        [Test]
        public void ParallelRelationshipsDoNotShareATrack()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["a"] = new Rect(0f, 0f, 280f, 160f),
                ["b"] = new Rect(0f, 320f, 280f, 160f),
                ["wall"] = new Rect(400f, -100f, 280f, 700f),
                ["c"] = new Rect(800f, 0f, 280f, 160f),
                ["d"] = new Rect(800f, 320f, 280f, 160f)
            };
            var requests = new[] { Edge("a", "c"), Edge("b", "d"), Edge("a", "d"), Edge("b", "c") };

            var routes = new ArchitectorEdgeRouter().Route(nodes, requests);

            var segments = routes.SelectMany(route => Segments(route.Value).Select(segment => (route.Key, segment.start, segment.end))).ToArray();
            for (var first = 0; first < segments.Length; first++)
            {
                for (var second = first + 1; second < segments.Length; second++)
                {
                    if (segments[first].Key == segments[second].Key)
                        continue;
                    var a = segments[first];
                    var b = segments[second];
                    var sameRow = Mathf.Abs(a.start.y - a.end.y) < 0.01f && Mathf.Abs(b.start.y - b.end.y) < 0.01f && Mathf.Abs(a.start.y - b.start.y) < 0.5f;
                    var sameColumn = Mathf.Abs(a.start.x - a.end.x) < 0.01f && Mathf.Abs(b.start.x - b.end.x) < 0.01f && Mathf.Abs(a.start.x - b.start.x) < 0.5f;
                    var overlap = sameRow
                        ? Mathf.Min(Mathf.Max(a.start.x, a.end.x), Mathf.Max(b.start.x, b.end.x)) - Mathf.Max(Mathf.Min(a.start.x, a.end.x), Mathf.Min(b.start.x, b.end.x))
                        : sameColumn
                            ? Mathf.Min(Mathf.Max(a.start.y, a.end.y), Mathf.Max(b.start.y, b.end.y)) - Mathf.Max(Mathf.Min(a.start.y, a.end.y), Mathf.Min(b.start.y, b.end.y))
                            : 0f;
                    Assert.That(overlap, Is.LessThanOrEqualTo(0.5f), $"{a.Key} and {b.Key} share a track at {a.start}");
                }
            }
        }

        [Test]
        public void RoutingIsDeterministicAndDoesNotDependOnRequestOrder()
        {
            var nodes = Grid(3, 3);
            var requests = new[] { Edge("n00", "n22"), Edge("n20", "n02"), Edge("n10", "n12"), Edge("n01", "n21") };

            var first = new ArchitectorEdgeRouter().Route(nodes, requests);
            var second = new ArchitectorEdgeRouter().Route(nodes, requests.Reverse().ToArray());

            foreach (var request in requests)
                Assert.That(second[request.Id], Is.EqualTo(first[request.Id]));
        }

        [Test]
        public void OneRelationshipBetweenAlignedNodesIsAStraightLine()
        {
            var nodes = Grid(2, 1);

            var route = new ArchitectorEdgeRouter().Route(nodes, new[] { Edge("n00", "n10") })["n00>n10"];

            Assert.That(route, Is.EqualTo(new[] { new Vector2(280f, 80f), new Vector2(480f, 80f) }));
        }
    }
}
