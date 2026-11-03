using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorRelationshipMergingTests
    {
        private static Dictionary<string, Rect> Nodes()
        {
            return new Dictionary<string, Rect>
            {
                ["a"] = new(0f, 0f, 240f, 160f),
                ["b"] = new(0f, 360f, 240f, 160f),
                ["c"] = new(0f, 720f, 240f, 160f),
                ["hub"] = new(700f, 360f, 280f, 180f)
            };
        }

        private static ArchitectorRouteRequest[] Requests()
        {
            return new[] { "a", "b", "c" }.Select(id => new ArchitectorRouteRequest(id, id, "hub", "right", "left", ArchitectorEdgeKinds.DataFlow)).ToArray();
        }

        [Test]
        public void OffKeepsExistingRoutesAndPorts()
        {
            var nodes = Nodes();
            var requests = Requests();
            var expected = new ArchitectorEdgeRouter().Route(nodes, requests);
            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Off);
            foreach (var request in requests)
                Assert.That(network.Routes[request.Id], Is.EqualTo(expected[request.Id]));
            Assert.That(network.Strokes.All(stroke => stroke.EdgeIds.Length == 1 && stroke.Arrow), Is.True);
        }

        [TestCase(ArchitectorRelationshipMerging.ThreeOrMore, 2, 2)]
        [TestCase(ArchitectorRelationshipMerging.ThreeOrMore, 3, 1)]
        [TestCase(ArchitectorRelationshipMerging.TwoOrMore, 2, 1)]
        [TestCase(ArchitectorRelationshipMerging.Maximum, 3, 1)]
        public void ThresholdControlsTheNumberOfArrowheads(ArchitectorRelationshipMerging mode, int count, int arrows)
        {
            var network = new ArchitectorEdgeRouter().RouteMerged(Nodes(), Requests().Take(count).ToArray(), mode);
            Assert.That(network.Strokes.Count(stroke => stroke.Arrow), Is.EqualTo(arrows));
            Assert.That(network.Routes.Count, Is.EqualTo(count));
            if (arrows == 1)
                Assert.That(network.Routes.Values.Select(route => route.Last()).Distinct().Count(), Is.EqualTo(1));
        }

        [Test]
        public void BranchesAndTrunkAreDrawnOnceWithoutCollinearOverlap()
        {
            var network = new ArchitectorEdgeRouter().RouteMerged(Nodes(), Requests(), ArchitectorRelationshipMerging.Maximum);
            AssertNoCollinearOverlap(network);
            Assert.That(network.Strokes.Count(stroke => stroke.JunctionAtStart), Is.InRange(1, 2));
        }

        private static void AssertNoCollinearOverlap(ArchitectorRouteNetwork network)
        {
            var segments = network.Strokes.SelectMany(stroke => Enumerable.Range(1, stroke.Points.Count - 1)
                .Select(index => (stroke.Id, first: stroke.Points[index - 1], last: stroke.Points[index]))).ToArray();
            for (var first = 0; first < segments.Length; first++)
            {
                var a = segments[first];
                var horizontal = Mathf.Abs(a.first.y - a.last.y) < 0.01f;
                Assert.That(horizontal || Mathf.Abs(a.first.x - a.last.x) < 0.01f, Is.True);
                for (var second = first + 1; second < segments.Length; second++)
                {
                    var b = segments[second];
                    if (a.Id == b.Id || horizontal != (Mathf.Abs(b.first.y - b.last.y) < 0.01f))
                        continue;
                    if (Mathf.Abs(horizontal ? a.first.y - b.first.y : a.first.x - b.first.x) > 0.01f)
                        continue;
                    var overlap = horizontal
                        ? Mathf.Min(Mathf.Max(a.first.x, a.last.x), Mathf.Max(b.first.x, b.last.x)) - Mathf.Max(Mathf.Min(a.first.x, a.last.x), Mathf.Min(b.first.x, b.last.x))
                        : Mathf.Min(Mathf.Max(a.first.y, a.last.y), Mathf.Max(b.first.y, b.last.y)) - Mathf.Max(Mathf.Min(a.first.y, a.last.y), Mathf.Min(b.first.y, b.last.y));
                    Assert.That(overlap, Is.LessThanOrEqualTo(0.01f), a.Id + " / " + b.Id);
                }
            }
        }

        [Test]
        public void TreesToDifferentTargetsUseSeparateSharedTracks()
        {
            var nodes = Nodes();
            nodes["upper"] = new Rect(700f, 0f, 280f, 180f);
            var requests = Requests().Concat(new[] { "a", "b", "c" }.Select(id =>
                new ArchitectorRouteRequest("upper-" + id, id, "upper", "right", "left", ArchitectorEdgeKinds.Dependency))).ToArray();
            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Maximum);
            Assert.That(network.Strokes.Count(stroke => stroke.Arrow), Is.EqualTo(2));
            AssertNoCollinearOverlap(network);
        }

        [Test]
        public void AJunctionInANarrowGapKeepsRoutesOrthogonalAndOutsideNodes()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["hub"] = new(0f, 0f, 300f, 225f),
                ["a"] = new(375f, 0f, 300f, 150f),
                ["b"] = new(375f, 500f, 300f, 150f),
                ["c"] = new(375f, 1000f, 300f, 150f)
            };
            var requests = new[] { "a", "b", "c" }.Select(id => new ArchitectorRouteRequest(id, id, "hub", "left", "right")).ToArray();
            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Maximum);
            Assert.That(network.Strokes.Count(stroke => stroke.Arrow), Is.EqualTo(1));
            foreach (var request in requests)
            {
                var points = network.Routes[request.Id];
                for (var index = 1; index < points.Count; index++)
                {
                    var start = points[index - 1];
                    var end = points[index];
                    Assert.That(Mathf.Min(Mathf.Abs(start.x - end.x), Mathf.Abs(start.y - end.y)), Is.LessThan(0.01f));
                    var bounds = Rect.MinMaxRect(Mathf.Min(start.x, end.x), Mathf.Min(start.y, end.y), Mathf.Max(start.x, end.x), Mathf.Max(start.y, end.y));
                    foreach (var node in nodes)
                        Assert.That(bounds.Overlaps(node.Value), Is.False, request.Id + " / " + node.Key);
                }
            }
        }

        [Test]
        public void DifferentKindsNeverShareAStroke()
        {
            var requests = Requests().Concat(new[]
            {
                new ArchitectorRouteRequest("event-a", "a", "hub", "right", "left", ArchitectorEdgeKinds.Event),
                new ArchitectorRouteRequest("event-b", "b", "hub", "right", "left", ArchitectorEdgeKinds.Event)
            }).ToArray();
            var kinds = requests.ToDictionary(request => request.Id, request => request.Kind);
            var network = new ArchitectorEdgeRouter().RouteMerged(Nodes(), requests, ArchitectorRelationshipMerging.Maximum);
            foreach (var stroke in network.Strokes)
                Assert.That(stroke.EdgeIds.Select(id => kinds[id]).Distinct().ToArray(), Is.EqualTo(new[] { stroke.Kind }));
            Assert.That(network.Routes.Keys, Is.EquivalentTo(requests.Select(request => request.Id)));
        }

        [Test]
        public void OutgoingRelationshipsShareTheSourcePortAndSplitAtAJunction()
        {
            var requests = new[] { "a", "b", "c" }
                .Select(id => new ArchitectorRouteRequest(id, "hub", id, "left", "right", ArchitectorEdgeKinds.DataFlow)).ToArray();
            var nodes = Nodes();

            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Maximum);

            Assert.That(network.Routes.Values.Select(route => route[0]).Distinct().Count(), Is.EqualTo(1));
            Assert.That(network.Strokes.Count(stroke => stroke.Arrow), Is.EqualTo(3));
            Assert.That(network.Strokes.Count(stroke => stroke.Points[0] == network.Routes["a"][0]), Is.EqualTo(1));
            Assert.That(network.Strokes.Any(stroke => stroke.JunctionAtStart && network.IsJunction(stroke, _ => true)), Is.True);
            AssertNoCollinearOverlap(network);
            AssertHonest(network, requests);
        }

        [Test]
        public void ABusSharesOneTrunkWhenEverySourceReachesEveryTarget()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["s1"] = new(0f, 0f, 240f, 160f),
                ["s2"] = new(0f, 480f, 240f, 160f),
                ["t1"] = new(1200f, 0f, 240f, 160f),
                ["t2"] = new(1200f, 480f, 240f, 160f)
            };
            var requests = new[] { ("s1", "t1"), ("s1", "t2"), ("s2", "t1"), ("s2", "t2") }
                .Select(pair => new ArchitectorRouteRequest(pair.Item1 + pair.Item2, pair.Item1, pair.Item2, "right", "left")).ToArray();

            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Maximum);

            Assert.That(network.Strokes.Any(stroke => stroke.EdgeIds.Length == 4), Is.True);
            Assert.That(network.Strokes.Count(stroke => stroke.Arrow), Is.EqualTo(2));
            Assert.That(network.Routes.Keys, Is.EquivalentTo(requests.Select(request => request.Id)));
            AssertHonest(network, requests);
        }

        [Test]
        public void RelationshipsWithoutACommonEndNeverShareAStroke()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["s1"] = new(0f, 0f, 240f, 160f),
                ["s2"] = new(0f, 240f, 240f, 160f),
                ["t1"] = new(1200f, 0f, 240f, 160f),
                ["t2"] = new(1200f, 240f, 240f, 160f)
            };
            var requests = new[]
            {
                new ArchitectorRouteRequest("first", "s1", "t1", "right", "left"),
                new ArchitectorRouteRequest("second", "s2", "t2", "right", "left")
            };

            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Maximum);

            Assert.That(network.Strokes.All(stroke => stroke.EdgeIds.Length == 1), Is.True);
            Assert.That(network.Strokes.Any(stroke => stroke.JunctionAtStart), Is.False);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        public void SharedStrokesNeverSuggestARelationshipThatDoesNotExist(int seed)
        {
            var random = new System.Random(seed);
            var nodes = new Dictionary<string, Rect>();
            for (var index = 0; index < 9; index++)
                nodes["n" + index] = new Rect(index % 3 * 520f, index / 3 * 360f, 240f, 160f);
            var requests = new List<ArchitectorRouteRequest>();
            foreach (var source in nodes.Keys)
                foreach (var target in nodes.Keys)
                    if (source != target && random.NextDouble() < 0.3)
                        requests.Add(new ArchitectorRouteRequest(source + ">" + target, source, target, "auto", "auto"));

            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Maximum);

            Assert.That(network.Routes.Keys, Is.EquivalentTo(requests.Select(request => request.Id)));
            AssertHonest(network, requests);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void CellRoutingKeepsMergedLinesApartAndConnected(int seed)
        {
            var random = new System.Random(seed);
            var nodes = new Dictionary<string, Rect>();
            for (var index = 0; index < 9; index++)
                nodes["n" + index] = new Rect(index % 3 * 450f, index / 3 * 300f, 225f, 150f);
            var requests = new List<ArchitectorRouteRequest>();
            foreach (var source in nodes.Keys)
                foreach (var target in nodes.Keys)
                    if (source != target && random.NextDouble() < 0.3)
                        requests.Add(new ArchitectorRouteRequest(source + ">" + target, source, target, "auto", "auto"));

            var network = new ArchitectorEdgeRouter { CellSize = 75f }.RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Maximum);

            Assert.That(network.Routes.Keys, Is.EquivalentTo(requests.Select(request => request.Id)));
            foreach (var request in requests)
            {
                var route = network.Routes[request.Id];
                Assert.That(OnBorder(nodes[request.SourceNodeId], route[0]), Is.True, request.Id + " start");
                Assert.That(OnBorder(nodes[request.TargetNodeId], route[route.Count - 1]), Is.True, request.Id + " end");
            }
            AssertHonest(network, requests);
            AssertNoCollinearOverlap(network);
        }

        private static bool OnBorder(Rect rect, Vector2 point)
        {
            var inside = point.x >= rect.xMin - 0.01f && point.x <= rect.xMax + 0.01f && point.y >= rect.yMin - 0.01f && point.y <= rect.yMax + 0.01f;
            var onEdge = Mathf.Abs(point.x - rect.xMin) < 0.01f || Mathf.Abs(point.x - rect.xMax) < 0.01f
                         || Mathf.Abs(point.y - rect.yMin) < 0.01f || Mathf.Abs(point.y - rect.yMax) < 0.01f;
            return inside && onEdge;
        }

        private static void AssertHonest(ArchitectorRouteNetwork network, IReadOnlyList<ArchitectorRouteRequest> requests)
        {
            var byId = requests.ToDictionary(request => request.Id);
            foreach (var stroke in network.Strokes.Where(stroke => stroke.EdgeIds.Length > 1))
            {
                var carried = stroke.EdgeIds.Select(id => byId[id]).ToArray();
                var pairs = carried.Select(request => (request.SourceNodeId, request.TargetNodeId)).Distinct().Count();
                var sources = carried.Select(request => request.SourceNodeId).Distinct().Count();
                var targets = carried.Select(request => request.TargetNodeId).Distinct().Count();
                Assert.That(pairs, Is.EqualTo(sources * targets), $"Stroke {stroke.Id} suggests a relationship that does not exist.");
            }
        }

        [TestCase(ArchitectorRelationshipMerging.ThreeOrMore, false)]
        [TestCase(ArchitectorRelationshipMerging.TwoOrMore, false)]
        [TestCase(ArchitectorRelationshipMerging.Maximum, false)]
        [TestCase(ArchitectorRelationshipMerging.ThreeOrMore, true)]
        [TestCase(ArchitectorRelationshipMerging.TwoOrMore, true)]
        [TestCase(ArchitectorRelationshipMerging.Maximum, true)]
        public void EnabledMergingCombinesParallelRelationshipsWithoutLosingIdentities(ArchitectorRelationshipMerging mode, bool sharedTree)
        {
            var requests = new List<ArchitectorRouteRequest>
            {
                new ArchitectorRouteRequest("first", "a", "hub", "right", "left"),
                new ArchitectorRouteRequest("second", "a", "hub", "right", "left")
            };
            if (sharedTree)
                requests.Add(new ArchitectorRouteRequest("third", "c", "hub", "right", "left"));
            var combined = new ArchitectorEdgeRouter().RouteMerged(Nodes(), requests, mode);
            Assert.That(combined.Routes.Keys, Is.EquivalentTo(requests.Select(request => request.Id)));
            Assert.That(combined.Routes["first"], Is.EqualTo(combined.Routes["second"]));
            Assert.That(combined.Strokes.Count(stroke => stroke.Arrow), Is.EqualTo(1));
            Assert.That(combined.Strokes.Count(stroke => stroke.Points[0] == combined.Routes["first"][0]), Is.EqualTo(1));
            foreach (var stroke in combined.Strokes)
                Assert.That(stroke.EdgeIds.Contains("first"), Is.EqualTo(stroke.EdgeIds.Contains("second")));
            AssertNoCollinearOverlap(combined);
            if (!sharedTree)
            {
                Assert.That(combined.Strokes.Count, Is.EqualTo(1));
                Assert.That(combined.Strokes[0].JunctionAtStart, Is.False);
            }
        }

        [Test]
        public void OffKeepsParallelRelationshipsOnSeparatePorts()
        {
            var requests = new[]
            {
                new ArchitectorRouteRequest("first", "a", "hub", "right", "left"),
                new ArchitectorRouteRequest("second", "a", "hub", "right", "left")
            };
            var network = new ArchitectorEdgeRouter().RouteMerged(Nodes(), requests, ArchitectorRelationshipMerging.Off);
            Assert.That(network.Strokes.Count, Is.EqualTo(2));
            Assert.That(network.Routes["first"][0], Is.Not.EqualTo(network.Routes["second"][0]));
            Assert.That(network.Routes["first"].Last(), Is.Not.EqualTo(network.Routes["second"].Last()));
        }

        [TestCase(ArchitectorRelationshipMerging.ThreeOrMore)]
        [TestCase(ArchitectorRelationshipMerging.TwoOrMore)]
        [TestCase(ArchitectorRelationshipMerging.Maximum)]
        public void DifferentPreferredSidesKeepSeparateBranches(ArchitectorRelationshipMerging mode)
        {
            var requests = new[]
            {
                new ArchitectorRouteRequest("first", "a", "hub", "right", "left"),
                new ArchitectorRouteRequest("second", "a", "hub", "bottom", "left")
            };
            var network = new ArchitectorEdgeRouter().RouteMerged(Nodes(), requests, mode);
            Assert.That(network.Routes["first"][0], Is.Not.EqualTo(network.Routes["second"][0]));
        }

        [Test]
        public void MergingIsDeterministicAcrossRequestOrder()
        {
            var router = new ArchitectorEdgeRouter();
            var expected = router.RouteMerged(Nodes(), Requests(), ArchitectorRelationshipMerging.Maximum);
            var reversed = router.RouteMerged(Nodes(), Requests().Reverse().ToArray(), ArchitectorRelationshipMerging.Maximum);
            foreach (var route in expected.Routes)
                Assert.That(reversed.Routes[route.Key], Is.EqualTo(route.Value));
            Assert.That(reversed.Strokes.Select(stroke => stroke.Id), Is.EqualTo(expected.Strokes.Select(stroke => stroke.Id)));
        }

        private static float Ink(IEnumerable<ArchitectorRouteStroke> strokes)
        {
            return strokes.Sum(stroke => Enumerable.Range(1, stroke.Points.Count - 1)
                .Sum(index => Vector2.Distance(stroke.Points[index - 1], stroke.Points[index])));
        }

        [Test]
        public void LongParallelRunsMergeNearTheSourcesAndReduceInk()
        {
            var nodes = Nodes();
            var router = new ArchitectorEdgeRouter();
            var off = router.RouteMerged(nodes, Requests(), ArchitectorRelationshipMerging.Off);
            var merged = router.RouteMerged(nodes, Requests(), ArchitectorRelationshipMerging.Maximum);
            Assert.That(Ink(merged.Strokes), Is.LessThan(Ink(off.Strokes) * 0.7f));
            Assert.That(Ink(merged.Strokes.Where(stroke => stroke.EdgeIds.Length == 1)), Is.LessThan(Ink(off.Strokes) * 0.5f));
            var trunk = merged.Strokes.Single(stroke => stroke.Arrow);
            Assert.That(trunk.EdgeIds, Is.EquivalentTo(new[] { "a", "b", "c" }));
            Assert.That(Ink(new[] { trunk }), Is.GreaterThan(300f));
            Assert.That(nodes["hub"].xMin - trunk.Points[0].x, Is.GreaterThan(300f));
        }

        [Test]
        public void AnObstacleDeflectsTheSharedTreeWithoutCreatingATerminalRail()
        {
            var nodes = Nodes();
            nodes["obstacle"] = new Rect(450f, 320f, 180f, 260f);
            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, Requests(), ArchitectorRelationshipMerging.Maximum);
            Assert.That(network.Strokes.Count(stroke => stroke.Arrow), Is.EqualTo(1));
            foreach (var stroke in network.Strokes)
                for (var index = 1; index < stroke.Points.Count; index++)
                {
                    var from = stroke.Points[index - 1];
                    var to = stroke.Points[index];
                    var bounds = Rect.MinMaxRect(Mathf.Min(from.x, to.x), Mathf.Min(from.y, to.y), Mathf.Max(from.x, to.x), Mathf.Max(from.y, to.y));
                    Assert.That(bounds.Overlaps(nodes["obstacle"]), Is.False);
                }
        }

        [Test]
        public void CrossingRelationshipsDoNotCreateAJunction()
        {
            var nodes = new Dictionary<string, Rect>
            {
                ["left"] = new(-600f, -50f, 100f, 100f),
                ["right"] = new(500f, -50f, 100f, 100f),
                ["top"] = new(-50f, -600f, 100f, 100f),
                ["bottom"] = new(-50f, 500f, 100f, 100f)
            };
            var requests = new[]
            {
                new ArchitectorRouteRequest("horizontal", "left", "right", "right", "left"),
                new ArchitectorRouteRequest("vertical", "top", "bottom", "bottom", "top", ArchitectorEdgeKinds.Event)
            };
            var network = new ArchitectorEdgeRouter().RouteMerged(nodes, requests, ArchitectorRelationshipMerging.Maximum);
            Assert.That(ArchitectorEdgeRouter.CrossingCounts(network.Routes).Values.Sum(), Is.EqualTo(2));
            Assert.That(network.Strokes.Any(stroke => stroke.JunctionAtStart), Is.False);
        }

        [Test]
        public void MovingASourceDetachesItsBranchAndKeepsOtherGeometry()
        {
            var nodes = Nodes();
            var router = new ArchitectorEdgeRouter();
            var previous = router.RouteMerged(nodes, Requests(), ArchitectorRelationshipMerging.Maximum);
            var delta = new Vector2(10f, 30f);
            nodes["a"] = new Rect(nodes["a"].position + delta, nodes["a"].size);
            var moved = router.RouteMovedMerged(nodes, Requests(), new[] { "a" }, previous, delta);
            foreach (var id in new[] { "b", "c" })
                Assert.That(moved.Routes[id], Is.EqualTo(previous.Routes[id]));
            Assert.That(moved.Strokes.Any(stroke => stroke.EdgeIds.Contains("a") && stroke.EdgeIds.Length > 1), Is.False);
            foreach (var stroke in previous.Strokes.Where(stroke => stroke.EdgeIds.Any(id => id != "a")))
                Assert.That(moved.Strokes.Single(candidate => candidate.Id == stroke.Id).Points, Is.EqualTo(stroke.Points));
        }

        [Test]
        public void DetachingTheOnlySiblingRemovesTheJunctionMarker()
        {
            var nodes = Nodes();
            var requests = Requests().Take(2).ToArray();
            var router = new ArchitectorEdgeRouter();
            var previous = router.RouteMerged(nodes, requests, ArchitectorRelationshipMerging.TwoOrMore);
            Assert.That(previous.Strokes.Any(stroke => stroke.JunctionAtStart), Is.True);
            var delta = new Vector2(10f, 30f);
            nodes["a"] = new Rect(nodes["a"].position + delta, nodes["a"].size);
            var moved = router.RouteMovedMerged(nodes, requests, new[] { "a" }, previous, delta);
            Assert.That(moved.Strokes.Any(stroke => stroke.JunctionAtStart), Is.False);
        }

        [Test]
        public void MovingBothEndsTranslatesTheirSharedGeometry()
        {
            var nodes = Nodes();
            var router = new ArchitectorEdgeRouter();
            var previous = router.RouteMerged(nodes, Requests(), ArchitectorRelationshipMerging.Maximum);
            var delta = new Vector2(10f, 30f);
            foreach (var id in new[] { "a", "hub" })
                nodes[id] = new Rect(nodes[id].position + delta, nodes[id].size);
            var moved = router.RouteMovedMerged(nodes, Requests(), new[] { "a", "hub" }, previous, delta);
            Assert.That(moved.Routes["a"], Is.EqualTo(previous.Routes["a"].Select(point => point + delta)));
            foreach (var stroke in previous.Strokes.Where(stroke => stroke.EdgeIds.Contains("a")))
                Assert.That(moved.Strokes.Single(candidate => candidate.Id == stroke.Id).Points, Is.EqualTo(stroke.Points.Select(point => point + delta)));
        }

        [Test]
        public void TrunkClicksCycleMembersWhileABranchSelectsOnlyItsRelationship()
        {
            var network = new ArchitectorEdgeRouter().RouteMerged(Nodes(), Requests(), ArchitectorRelationshipMerging.Maximum);
            var canvas = new ArchitectorLayerCanvas("merging-test");
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ArchitectorLayerCanvas).GetField("pan", flags).SetValue(canvas, Vector2.zero);
            typeof(ArchitectorLayerCanvas).GetMethod("SetRouteNetwork", flags).Invoke(canvas, new object[] { network, network.Decorate(8f, 5f) });
            typeof(ArchitectorLayerCanvas).GetField("edgeRouteCacheRevision", flags).SetValue(canvas, 0);
            var documents = new ArchitectorLayerDocuments(new ArchitectorLayerDocument
            {
                edges = Requests().Select(request => new ArchitectorEdgeRecord
                {
                    id = request.Id, sourceNodeId = request.SourceNodeId, targetNodeId = request.TargetNodeId, kind = request.Kind
                }).ToList()
            }, new ArchitectorLayerLayoutDocument());
            var hit = typeof(ArchitectorLayerCanvas).GetMethod("HitTestEdge", flags);
            var stem = network.Strokes.Single(stroke => stroke.Arrow);
            var mouse = (stem.Points[0] + stem.Points.Last()) * 0.5f;
            var cycle = Enumerable.Range(0, 3).Select(_ => (string)hit.Invoke(canvas, new object[] { mouse, documents })).ToArray();
            Assert.That(cycle, Is.EquivalentTo(new[] { "a", "b", "c" }));
            var branch = network.Strokes.Single(stroke => stroke.EdgeIds.Length == 1 && stroke.EdgeIds[0] == "a");
            mouse = (branch.Points[0] + branch.Points[1]) * 0.5f;
            Assert.That(hit.Invoke(canvas, new object[] { mouse, documents }), Is.EqualTo("a"));
            typeof(ArchitectorLayerCanvas).GetField("selectedEdgeId", flags).SetValue(canvas, "a");
            Assert.That(canvas.SelectedTrunkEdges, Is.EquivalentTo(new[] { "a", "b", "c" }));
        }

        [Test]
        public void OldSettingsDefaultToOffAndAllModesRoundTrip()
        {
            var settings = ArchitectorJson.Deserialize<ArchitectorSettingsDocument>("{\"optimization\":{}}");
            Assert.That(settings.optimization.relationshipMerging, Is.EqualTo(ArchitectorRelationshipMerging.Off));
            foreach (ArchitectorRelationshipMerging mode in Enum.GetValues(typeof(ArchitectorRelationshipMerging)))
            {
                settings.optimization.relationshipMerging = mode;
                Assert.That(ArchitectorJson.Clone(settings).optimization.relationshipMerging, Is.EqualTo(mode));
            }
        }

        [Test]
        public void BackgroundCalculationProducesTheSameNetwork()
        {
            var expected = new ArchitectorEdgeRouter().RouteMerged(Nodes(), Requests(), ArchitectorRelationshipMerging.Maximum);
            var job = new ArchitectorRouteJob(Nodes(), Requests(), 8f, 5f, ArchitectorRelationshipMerging.Maximum);
            Assert.That(job.Wait(30000), Is.True);
            Assert.That(job.Failure, Is.Null);
            Assert.That(job.Succeeded, Is.True);
            foreach (var route in expected.Routes)
                Assert.That(job.Routes[route.Key], Is.EqualTo(route.Value));
            Assert.That(job.DrawnRoutes.Count, Is.EqualTo(expected.Strokes.Count));
        }
    }
}
