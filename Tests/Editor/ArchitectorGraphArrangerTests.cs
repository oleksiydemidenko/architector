using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorGraphArrangerTests
    {
        private const float Gap = 48f;
        private const float Grid = 16f;

        private static ArchitectorArrangeNode Node(string id, bool portal = false, float x = 0f, float y = 0f)
        {
            return new ArchitectorArrangeNode(id, x, y, 280f, portal ? 120f : 160f, portal);
        }

        private static ArchitectorArrangeEdge Edge(string source, string target)
        {
            return new ArchitectorArrangeEdge(source, target);
        }

        private static (List<ArchitectorArrangeNode> nodes, List<ArchitectorArrangeEdge> edges) Star(string center, int portals)
        {
            var nodes = new List<ArchitectorArrangeNode> { Node(center) };
            var edges = new List<ArchitectorArrangeEdge>();
            for (var index = 0; index < portals; index++)
            {
                nodes.Add(Node($"{center}.portal{index}", true));
                edges.Add(Edge(center, $"{center}.portal{index}"));
            }
            return (nodes, edges);
        }

        private static void AssertNoOverlap(IReadOnlyList<ArchitectorArrangeNode> nodes, IReadOnlyDictionary<string, ArchitectorPoint> positions)
        {
            Assert.That(positions.Keys, Is.EquivalentTo(nodes.Select(node => node.Id)));
            var rects = nodes.Select(node => new Rect(positions[node.Id].x - Gap * 0.5f + 0.5f, positions[node.Id].y - Gap * 0.5f + 0.5f, node.Width + Gap - 1f, node.Height + Gap - 1f)).ToArray();
            for (var first = 0; first < rects.Length; first++)
            {
                for (var second = first + 1; second < rects.Length; second++)
                    Assert.That(rects[first].Overlaps(rects[second]), Is.False, $"{nodes[first].Id} and {nodes[second].Id} are closer than the node gap");
            }
            foreach (var position in positions.Values)
                Assert.That(Mathf.Repeat(position.x, Grid) + Mathf.Repeat(position.y, Grid), Is.Zero);
        }

        [Test]
        public void HubStaysInTheMiddleWithBalancedSidesAndItsChainBelow()
        {
            var nodes = new List<ArchitectorArrangeNode> { Node("module"), Node("parameter"), Node("data"), Node("handler") };
            var edges = new List<ArchitectorArrangeEdge> { Edge("module", "parameter"), Edge("parameter", "data"), Edge("module", "handler") };
            for (var index = 0; index < 10; index++)
            {
                nodes.Add(Node($"portal{index}", true));
                edges.Add(Edge("module", $"portal{index}"));
            }

            var positions = ArchitectorGraphArranger.Arrange(nodes, edges, Gap, Grid, 16);

            AssertNoOverlap(nodes, positions);
            var module = positions["module"];
            Assert.That(positions["parameter"].x, Is.EqualTo(module.x));
            Assert.That(positions["data"].x, Is.EqualTo(module.x));
            Assert.That(positions["handler"].x, Is.EqualTo(module.x));
            Assert.That(positions["handler"].y, Is.LessThan(module.y));
            Assert.That(positions["parameter"].y, Is.GreaterThan(module.y));
            Assert.That(positions["data"].y, Is.GreaterThan(positions["parameter"].y));
            var left = Enumerable.Range(0, 10).Count(index => positions[$"portal{index}"].x < module.x);
            Assert.That(left, Is.InRange(4, 6));
        }

        [Test]
        public void ConnectedHubsSplitOnlyWhenTheRelationshipLimitIsExceeded()
        {
            var first = Star("a", 7);
            var second = Star("b", 7);
            var nodes = first.nodes.Concat(second.nodes).ToList();
            var edges = first.edges.Concat(second.edges).Append(Edge("a", "b")).ToList();

            var split = ArchitectorGraphArranger.Arrange(nodes, edges, Gap, Grid, 8);
            var whole = ArchitectorGraphArranger.Arrange(nodes, edges, Gap, Grid, 32);

            AssertNoOverlap(nodes, split);
            AssertNoOverlap(nodes, whole);
            Rect Bounds(IReadOnlyDictionary<string, ArchitectorPoint> positions, string prefix)
            {
                var points = nodes.Where(node => node.Id.StartsWith(prefix)).Select(node => positions[node.Id]).ToArray();
                return Rect.MinMaxRect(points.Min(point => point.x), points.Min(point => point.y), points.Max(point => point.x) + 280f, points.Max(point => point.y) + 160f);
            }
            Assert.That(Bounds(split, "a").Overlaps(Bounds(split, "b")), Is.False);
            Assert.That(Bounds(whole, "a").Overlaps(Bounds(whole, "b")), Is.True);
        }

        [Test]
        public void SelectedCentersGetTheirOwnNeighbours()
        {
            var nodes = new List<ArchitectorArrangeNode> { Node("left"), Node("bridge"), Node("right"), Node("leftLeaf", true), Node("rightLeaf", true), Node("alone") };
            var edges = new List<ArchitectorArrangeEdge> { Edge("left", "bridge"), Edge("bridge", "right"), Edge("left", "leftLeaf"), Edge("right", "rightLeaf") };

            var positions = ArchitectorGraphArranger.Arrange(nodes, edges, Gap, Grid, 16, new[] { "left", "right" });

            AssertNoOverlap(nodes, positions);
            float Distance(string first, string second) =>
                Mathf.Abs(positions[first].x - positions[second].x) + Mathf.Abs(positions[first].y - positions[second].y);
            Assert.That(Distance("left", "leftLeaf"), Is.LessThan(Distance("right", "leftLeaf")));
            Assert.That(Distance("right", "rightLeaf"), Is.LessThan(Distance("left", "rightLeaf")));
        }

        [Test]
        public void ADenseGroupStaysValidAndDeterministicWhenRoutesScoreIt()
        {
            var nodes = Enumerable.Range(0, 14).Select(index => Node($"n{index:00}")).ToList();
            var edges = new List<ArchitectorArrangeEdge>();
            for (var index = 1; index < 14; index++)
            {
                edges.Add(Edge("n00", $"n{index:00}"));
                edges.Add(Edge($"n{index:00}", $"n{index % 13 + 1:00}"));
            }

            var first = ArchitectorGraphArranger.Arrange(nodes, edges, Gap, Grid, 64);
            var second = ArchitectorGraphArranger.Arrange(nodes, edges, Gap, Grid, 64);

            AssertNoOverlap(nodes, first);
            foreach (var pair in first)
                Assert.That(second[pair.Key], Is.EqualTo(pair.Value));
        }

        [Test]
        public void ArrangementKeepsItsPlaceAndIsDeterministic()
        {
            var (nodes, edges) = Star("hub", 9);
            nodes[0] = Node("hub", false, 1600f, 800f);
            nodes.Add(Node("alone", false, 640f, 480f));

            var first = ArchitectorGraphArranger.Arrange(nodes, edges, Gap, Grid, 16);
            nodes.Reverse();
            var second = ArchitectorGraphArranger.Arrange(nodes, edges, Gap, Grid, 16);

            Assert.That(second.Keys, Is.EquivalentTo(first.Keys));
            foreach (var pair in first)
                Assert.That(second[pair.Key], Is.EqualTo(pair.Value));
            Assert.That(first.Values.Min(point => point.x), Is.EqualTo(0f));
        }
    }
}
