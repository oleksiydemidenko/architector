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

        [Test]
        public void ARouteArrivingAlongTheEntryColumnTurnsIntoTheNodeWithoutAHook()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["a"] = new Rect(240f, 0f, 280f, 160f),
                ["b"] = new Rect(400f, 400f, 280f, 160f)
            };
            var request = new ArchitectorRouteRequest("a>b", "a", "b", "bottom", "left");

            var route = new ArchitectorEdgeRouter().Route(nodes, new[] { request })["a>b"];

            Assert.That(route, Is.EqualTo(new[] { new Vector2(380f, 160f), new Vector2(380f, 480f), new Vector2(400f, 480f) }));
        }

        [Test]
        public void ABusyHubGetsNoHooksShortEndsOrRoutesThroughNodes()
        {
            var nodes = Hub();
            var requests = HubRequests(nodes).ToArray();

            var routes = new ArchitectorEdgeRouter().Route(nodes, requests);

            foreach (var request in requests)
            {
                var route = routes[request.Id];
                Assert.That((route[1] - route[0]).magnitude, Is.GreaterThanOrEqualTo(ArchitectorEdgeRouter.Clearance - 0.01f), request.Id);
                Assert.That((route[route.Count - 1] - route[route.Count - 2]).magnitude, Is.GreaterThanOrEqualTo(ArchitectorEdgeRouter.Clearance - 0.01f), request.Id);
                for (var index = 0; index + 3 < route.Count; index++)
                {
                    var first = route[index + 1] - route[index];
                    var bridge = route[index + 2] - route[index + 1];
                    var third = route[index + 3] - route[index + 2];
                    Assert.That(Vector2.Dot(first, third) < 0f && bridge.magnitude <= ArchitectorEdgeRouter.Clearance * 2f, Is.False, $"{request.Id} hooks at {route[index + 1]}");
                }
                foreach (var (start, end) in Segments(route))
                {
                    var bounds = Rect.MinMaxRect(
                        Mathf.Min(start.x, end.x), Mathf.Min(start.y, end.y),
                        Mathf.Max(start.x, end.x), Mathf.Max(start.y, end.y));
                    foreach (var node in nodes.Where(node => node.Key != request.SourceNodeId && node.Key != request.TargetNodeId))
                        Assert.That(bounds.Overlaps(node.Value), Is.False, $"{request.Id} crosses {node.Key}");
                }
            }
        }

        [Test]
        public void SeveralRelationshipsBetweenFacingNodesAreStraightLines()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["a"] = new Rect(0f, 0f, 280f, 200f),
                ["b"] = new Rect(500f, 40f, 280f, 200f)
            };
            var requests = new[]
            {
                new ArchitectorRouteRequest("first", "a", "b", "auto", "auto"),
                new ArchitectorRouteRequest("second", "a", "b", "auto", "auto"),
                new ArchitectorRouteRequest("third", "b", "a", "auto", "auto")
            };

            var routes = new ArchitectorEdgeRouter().Route(nodes, requests);

            foreach (var request in requests)
                Assert.That(routes[request.Id], Has.Count.EqualTo(2), request.Id);
            Assert.That(routes.Values.Select(route => route[0].y).Distinct().Count(), Is.EqualTo(requests.Length));
        }

        [Test]
        public void ADiagonalRelationshipTakesOneTurnThroughSidesThatFaceEachOther()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["a"] = new Rect(0f, 0f, 200f, 200f),
                ["b"] = new Rect(600f, 500f, 200f, 200f)
            };

            var route = new ArchitectorEdgeRouter().Route(nodes, new[] { Edge("a", "b") })["a>b"];

            Assert.That(route, Has.Count.EqualTo(3));
            Assert.That(route[0].x >= 200f || route[0].y >= 200f, Is.True);
            Assert.That(route[2].x <= 600f || route[2].y <= 500f, Is.True);
        }

        [Test]
        public void AnExplicitSideIsKept()
        {
            var nodes = Grid(2, 1);
            var request = new ArchitectorRouteRequest("a", "n00", "n10", "right", "top");

            var route = new ArchitectorEdgeRouter().Route(nodes, new[] { request })["a"];

            Assert.That(route[route.Count - 1].y, Is.EqualTo(0f).Within(0.01f));
            Assert.That(route[route.Count - 2].y, Is.LessThan(0f));
        }

        [Test]
        public void MovingANodeLeavesUnrelatedRoutesExactlyWhereTheyWere()
        {
            var nodes = Grid(3, 3);
            var requests = new[] { Edge("n00", "n20"), Edge("n02", "n22"), Edge("n11", "n21"), Edge("n01", "n11") };
            var router = new ArchitectorEdgeRouter();
            var before = router.Route(nodes, requests);
            var delta = new Vector2(30f, 20f);
            var moved = new Dictionary<string, Rect>(nodes);
            moved["n11"] = new Rect(nodes["n11"].x + delta.x, nodes["n11"].y + delta.y, 280f, 160f);

            var after = router.RouteMoved(moved, requests, new[] { "n11" }, before, delta);

            Assert.That(after["n00>n20"], Is.EqualTo(before["n00>n20"]));
            Assert.That(after["n02>n22"], Is.EqualTo(before["n02>n22"]));
            Assert.That(after["n11>n21"][0].x, Is.EqualTo(moved["n11"].xMax).Within(0.01f));
            Assert.That(after["n01>n11"][after["n01>n11"].Count - 1].x, Is.EqualTo(moved["n11"].xMin).Within(0.01f));
        }

        [Test]
        public void ARouteInsideAMovingSelectionTravelsWithIt()
        {
            var nodes = Grid(2, 2);
            var requests = new[] { Edge("n00", "n11"), Edge("n10", "n01") };
            var router = new ArchitectorEdgeRouter();
            var before = router.Route(nodes, requests);
            var delta = new Vector2(40f, 60f);
            var moved = nodes.ToDictionary(node => node.Key, node => new Rect(node.Value.x + delta.x, node.Value.y + delta.y, 280f, 160f));

            var after = router.RouteMoved(moved, requests, nodes.Keys.ToArray(), before, delta);

            foreach (var request in requests)
                Assert.That(after[request.Id], Is.EqualTo(before[request.Id].Select(point => point + delta).ToList()));
        }

        [Test]
        public void ARecalculatedRouteDoesNotTakeThePortOfAFrozenOne()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["a"] = new Rect(0f, 0f, 280f, 160f),
                ["b"] = new Rect(0f, 0f + 320f, 280f, 160f),
                ["hub"] = new Rect(600f, 160f, 280f, 160f)
            };
            var requests = new[] { Edge("a", "hub"), Edge("b", "hub") };
            var router = new ArchitectorEdgeRouter();
            var before = router.Route(nodes, requests);
            var moved = new Dictionary<string, Rect>(nodes) { ["b"] = new Rect(0f, 40f + 320f, 280f, 160f) };

            var after = router.RouteMoved(moved, requests, new[] { "b" }, before, new Vector2(0f, 40f));

            Assert.That(after["a>hub"], Is.EqualTo(before["a>hub"]));
            var tips = requests.Select(request => after[request.Id].Last()).ToArray();
            Assert.That((tips[0] - tips[1]).magnitude, Is.GreaterThan(1f));
        }

        [Test]
        public void AHorizontalLineBridgesAVerticalOneAndKeepsItsEnds()
        {
            var routes = new Dictionary<string, List<Vector2>>
            {
                ["across"] = new() { new Vector2(0f, 100f), new Vector2(200f, 100f) },
                ["down"] = new() { new Vector2(100f, 0f), new Vector2(100f, 200f) }
            };

            var drawn = ArchitectorEdgeRouter.Decorate(routes, 8f, 5f);

            var across = drawn["across"];
            Assert.That(across[0], Is.EqualTo(new Vector2(0f, 100f)));
            Assert.That(across[across.Count - 1], Is.EqualTo(new Vector2(200f, 100f)));
            Assert.That(across.Min(point => point.y), Is.EqualTo(95f).Within(0.01f));
            Assert.That(across.Where(point => point.y < 99.99f).Min(point => point.x), Is.GreaterThan(95f - 0.01f));
            Assert.That(across.Where(point => point.y < 99.99f).Max(point => point.x), Is.LessThan(105f + 0.01f));
            Assert.That(drawn["down"], Has.Count.EqualTo(2));
        }

        [Test]
        public void CloseCrossingsShareOneBridge()
        {
            var routes = new Dictionary<string, List<Vector2>>
            {
                ["across"] = new() { new Vector2(0f, 100f), new Vector2(300f, 100f) },
                ["first"] = new() { new Vector2(100f, 0f), new Vector2(100f, 200f) },
                ["second"] = new() { new Vector2(110f, 0f), new Vector2(110f, 200f) },
                ["far"] = new() { new Vector2(200f, 0f), new Vector2(200f, 200f) }
            };

            var drawn = ArchitectorEdgeRouter.Decorate(routes, 8f, 5f);

            var bridges = 0;
            var raised = false;
            foreach (var point in drawn["across"])
            {
                var isRaised = point.y < 99.99f;
                if (isRaised && !raised)
                    bridges++;
                raised = isRaised;
            }
            Assert.That(bridges, Is.EqualTo(2));
        }

        [Test]
        public void TracksOnThePortSpacingShareOneBridge()
        {
            var routes = new Dictionary<string, List<Vector2>>
            {
                ["across"] = new() { new Vector2(0f, 100f), new Vector2(300f, 100f) },
                ["first"] = new() { new Vector2(100f, 0f), new Vector2(100f, 200f) },
                ["second"] = new() { new Vector2(114f, 0f), new Vector2(114f, 200f) }
            };

            var drawn = ArchitectorEdgeRouter.Decorate(routes, 8f, 5f)["across"];

            Assert.That(drawn.Count(point => Mathf.Abs(point.y - 100f) < 0.01f), Is.EqualTo(4));
        }

        [Test]
        public void ACornerShrinksToFitABridgeNextToIt()
        {
            var routes = new Dictionary<string, List<Vector2>>
            {
                ["turning"] = new() { new Vector2(0f, 100f), new Vector2(200f, 100f), new Vector2(200f, 300f) },
                ["down"] = new() { new Vector2(188f, 90f), new Vector2(188f, 200f) }
            };

            var drawn = ArchitectorEdgeRouter.Decorate(routes, 8f, 5f);

            Assert.That(drawn["turning"].Min(point => point.y), Is.EqualTo(95f).Within(0.01f));
            Assert.That(drawn["turning"].Where(point => point.y > 100.01f).Min(point => point.x), Is.GreaterThan(193f));
            Assert.That(drawn["down"], Has.Count.EqualTo(2));
        }

        [Test]
        public void TheVerticalLineHopsWhenTheHorizontalOneHasNoRoom()
        {
            var routes = new Dictionary<string, List<Vector2>>
            {
                ["turning"] = new() { new Vector2(0f, 100f), new Vector2(200f, 100f), new Vector2(200f, 300f) },
                ["down"] = new() { new Vector2(195f, 0f), new Vector2(195f, 200f) }
            };

            var drawn = ArchitectorEdgeRouter.Decorate(routes, 8f, 5f);

            Assert.That(drawn["down"].Max(point => point.x), Is.EqualTo(200f).Within(0.01f));
            Assert.That(drawn["turning"].Min(point => point.y), Is.EqualTo(100f).Within(0.01f));
        }

        [Test]
        public void ParallelRelationshipsOfOnePairDoNotCrossEachOther()
        {
            var nodes = Hub();
            var requests = HubRequests(nodes).ToArray();

            var routes = new ArchitectorEdgeRouter().Route(nodes, requests);

            foreach (var node in nodes.Keys.Where(node => node != "hub"))
            {
                var pair = new Dictionary<string, List<Vector2>>
                {
                    ["first"] = routes[$"{node}>hub"],
                    ["second"] = routes[$"{node}>hub2"]
                };
                Assert.That(ArchitectorEdgeRouter.CrossingCounts(pair)["first"], Is.Zero, node);
            }
        }

        [Test]
        public void NoBridgeSitsOnAnArrowhead()
        {
            var routes = new Dictionary<string, List<Vector2>>
            {
                ["across"] = new() { new Vector2(0f, 100f), new Vector2(200f, 100f) },
                ["down"] = new() { new Vector2(190f, 0f), new Vector2(190f, 200f) }
            };

            var drawn = ArchitectorEdgeRouter.Decorate(routes, 8f, 5f);

            Assert.That(drawn["across"], Has.Count.EqualTo(2));
            Assert.That(drawn["down"].Max(point => point.x), Is.EqualTo(195f).Within(0.01f));
        }

        [Test]
        public void CrossingsAreCountedForBothRoutes()
        {
            var routes = new Dictionary<string, List<Vector2>>
            {
                ["across"] = new() { new Vector2(0f, 100f), new Vector2(200f, 100f) },
                ["down"] = new() { new Vector2(100f, 0f), new Vector2(100f, 200f) },
                ["apart"] = new() { new Vector2(300f, 0f), new Vector2(300f, 200f) }
            };

            var counts = ArchitectorEdgeRouter.CrossingCounts(routes);

            Assert.That(counts["across"], Is.EqualTo(1));
            Assert.That(counts["down"], Is.EqualTo(1));
            Assert.That(counts["apart"], Is.Zero);
        }

        [Test]
        public void ABackgroundJobGivesTheSameRoutesAsTheRouter()
        {
            var nodes = Hub();
            var requests = HubRequests(nodes).ToArray();

            var job = new ArchitectorRouteJob(nodes, requests, 8f, 5f);

            Assert.That(job.Wait(30000), Is.True);
            Assert.That(job.Succeeded, Is.True);
            var routes = new ArchitectorEdgeRouter().Route(nodes, requests);
            foreach (var request in requests)
                Assert.That(job.Routes[request.Id], Is.EqualTo(routes[request.Id]), request.Id);
            Assert.That(job.DrawnRoutes.Keys, Is.EquivalentTo(routes.Keys));
        }

        [Test]
        public void ACancelledJobGivesNoRoutes()
        {
            var nodes = Hub();
            var requests = HubRequests(nodes).ToArray();

            var job = new ArchitectorRouteJob(nodes, requests, 8f, 5f);
            job.Cancel();

            Assert.That(job.Wait(30000), Is.True);
            if (job.Succeeded)
                Assert.Inconclusive("The job finished before the cancellation reached it.");
            Assert.That(job.Routes, Is.Null);
            Assert.That(job.Failure, Is.Null);
        }

        [Test]
        public void JobsReplacingEachOtherLeaveOnlyTheLastResult()
        {
            var nodes = Hub();
            var requests = HubRequests(nodes).ToArray();

            ArchitectorRouteJob job = null;
            for (var step = 0; step < 6; step++)
            {
                job?.Cancel();
                var moved = new Dictionary<string, Rect>(nodes) { ["hub"] = new Rect(600f + step * 25f, 500f, 300f, 225f) };
                job = new ArchitectorRouteJob(moved, requests, 8f, 5f);
            }

            Assert.That(job.Wait(30000), Is.True);
            Assert.That(job.Succeeded, Is.True);
            var last = new Dictionary<string, Rect>(nodes) { ["hub"] = new Rect(725f, 500f, 300f, 225f) };
            var expected = new ArchitectorEdgeRouter().Route(last, requests);
            foreach (var request in requests)
                Assert.That(job.Routes[request.Id], Is.EqualTo(expected[request.Id]), request.Id);
        }

        private static Dictionary<string, Rect> Hub()
        {
            var nodes = new Dictionary<string, Rect> { ["hub"] = new Rect(600f, 500f, 300f, 225f) };
            for (var row = 0; row < 3; row++)
            {
                nodes[$"left{row}"] = new Rect(0f, row * 500f - 200f, 300f, 225f);
                nodes[$"right{row}"] = new Rect(1200f, row * 500f - 200f, 300f, 225f);
                nodes[$"top{row}"] = new Rect(row * 500f + 100f, 0f, 300f, 150f);
                nodes[$"bottom{row}"] = new Rect(row * 500f + 100f, 1100f, 300f, 150f);
            }
            return nodes;
        }

        private static IEnumerable<ArchitectorRouteRequest> HubRequests(Dictionary<string, Rect> nodes)
        {
            foreach (var node in nodes.Keys.Where(node => node != "hub"))
            {
                yield return new ArchitectorRouteRequest($"{node}>hub", node, "hub", "auto", "auto");
                yield return new ArchitectorRouteRequest($"{node}>hub2", node, "hub", "auto", "auto");
            }
        }
    }
}
