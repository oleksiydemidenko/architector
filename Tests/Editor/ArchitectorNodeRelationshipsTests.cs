using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorNodeRelationshipsTests
    {
        private static readonly Dictionary<(string, string), ArchitectorNodeRecord> Nodes = new()
        {
            [("core", "hub")] = Node("hub", "Hub", "class"),
            [("core", "zeta")] = Node("zeta", "Zeta", "class"),
            [("core", "alpha")] = Node("alpha", "alpha", "interface"),
            [("core", "beta")] = Node("beta", "Beta", "struct"),
            [("ui", "view")] = Node("view", "View", "class"),
            [("ui", "concept")] = new ArchitectorNodeRecord { id = "concept", title = "Concept" }
        };

        private static readonly Dictionary<string, string> Layers = new()
        {
            ["core"] = "Core",
            ["ui"] = "UI"
        };

        private static ArchitectorNodeRecord Node(string id, string title, string kind)
        {
            return new ArchitectorNodeRecord
            {
                id = id,
                title = title,
                type = new ArchitectorTypeRecord { mode = "linked", kind = kind }
            };
        }

        private static ArchitectorEdgeRecord Edge(
            string id,
            string sourceLayerId,
            string sourceNodeId,
            string targetLayerId,
            string targetNodeId,
            string kind = ArchitectorEdgeKinds.Dependency,
            string label = "")
        {
            return new ArchitectorEdgeRecord
            {
                id = id,
                sourceLayerId = sourceLayerId,
                sourceNodeId = sourceNodeId,
                targetLayerId = targetLayerId,
                targetNodeId = targetNodeId,
                kind = kind,
                label = label
            };
        }

        private static IReadOnlyList<ArchitectorNodeRelationshipGroup> Build(params ArchitectorEdgeRecord[] edges)
        {
            return ArchitectorNodeRelationships.Build(
                "core",
                "hub",
                edges,
                (layerId, nodeId) => Nodes.TryGetValue((layerId, nodeId), out var node) ? node : null,
                layerId => Layers.TryGetValue(layerId, out var name) ? name : null);
        }

        [Test]
        public void OutgoingGroupsComeBeforeIncomingAndIncludeCrossLayerRelationships()
        {
            var groups = Build(
                Edge("e1", "core", "beta", "core", "hub"),
                Edge("e2", "core", "hub", "ui", "view"),
                Edge("e3", "ui", "view", "core", "hub", ArchitectorEdgeKinds.Event),
                Edge("e4", "core", "hub", "core", "zeta"));

            Assert.That(groups.Select(group => group.Incoming), Is.EqualTo(new[] { false, true, true }));
            Assert.That(groups[0].Rows.Select(row => row.NodeId), Is.EqualTo(new[] { "view", "zeta" }));
            Assert.That(groups[0].Rows.Select(row => row.CrossLayer), Is.EqualTo(new[] { true, false }));
            Assert.That(groups[0].Rows[0].LayerId, Is.EqualTo("ui"));
            Assert.That(groups[0].Rows[0].LayerName, Is.EqualTo("UI"));
            Assert.That(groups[1].Rows.Single().NodeId, Is.EqualTo("beta"));
            Assert.That(groups[2].Kind, Is.EqualTo(ArchitectorEdgeKinds.Event));
            Assert.That(groups[2].Rows.Single().LayerId, Is.EqualTo("ui"));
            Assert.That(groups[2].Rows.Single().Incoming, Is.True);
        }

        [Test]
        public void DependencyLabelsAreGroupsOfTheirOwnAndOtherKindsFollowTheKindOrder()
        {
            var groups = Build(
                Edge("e1", "core", "hub", "core", "zeta", ArchitectorEdgeKinds.Callback, "done"),
                Edge("e2", "core", "hub", "core", "alpha", label: "implements"),
                Edge("e3", "core", "hub", "core", "beta", ArchitectorEdgeKinds.DataFlow, "values"),
                Edge("e4", "core", "hub", "core", "zeta", label: "extends"),
                Edge("e5", "core", "hub", "ui", "view"),
                Edge("e6", "core", "hub", "ui", "concept", label: "Implements"),
                Edge("e7", "core", "hub", "ui", "view", ArchitectorEdgeKinds.DataFlow, "other"));

            Assert.That(groups.All(group => !group.Incoming), Is.True);
            Assert.That(groups.Select(group => group.Kind), Is.EqualTo(new[]
            {
                ArchitectorEdgeKinds.Dependency,
                ArchitectorEdgeKinds.Dependency,
                ArchitectorEdgeKinds.Dependency,
                ArchitectorEdgeKinds.DataFlow,
                ArchitectorEdgeKinds.Callback
            }));
            Assert.That(groups.Select(group => group.Label.ToLowerInvariant()), Is.EqualTo(new[] { "", "extends", "implements", "", "" }));
            Assert.That(groups[0].Title, Is.EqualTo("Dependency"));
            Assert.That(groups[1].Title, Is.EqualTo("Dependency · extends"));
            Assert.That(groups[2].Rows.Select(row => row.Title), Is.EqualTo(new[] { "alpha", "Concept" }));
            Assert.That(groups[3].Title, Is.EqualTo("Data Flow"));
            Assert.That(groups[3].Rows.Select(row => row.Label), Is.EqualTo(new[] { "values", "other" }));
        }

        [Test]
        public void RowsAreSortedByTitleAndCarryTheTypeKindOfTheOtherNode()
        {
            var groups = Build(
                Edge("e1", "core", "hub", "core", "zeta"),
                Edge("e2", "core", "hub", "ui", "concept"),
                Edge("e3", "core", "hub", "core", "beta"),
                Edge("e4", "core", "hub", "core", "alpha"));

            var rows = groups.Single().Rows;
            Assert.That(rows.Select(row => row.Title), Is.EqualTo(new[] { "alpha", "Beta", "Concept", "Zeta" }));
            Assert.That(rows.Select(row => row.TypeKind), Is.EqualTo(new[] { "interface", "struct", "", "class" }));
        }

        [Test]
        public void OtherNodesAndLayersAreIgnoredAndMissingEndpointsFallBackToIds()
        {
            var groups = Build(
                Edge("e1", "core", "beta", "core", "zeta"),
                Edge("e2", "ui", "hub", "ui", "view"),
                Edge("e3", "core", "hub", "gone", "lost"));

            var row = groups.Single().Rows.Single();
            Assert.That(row.EdgeId, Is.EqualTo("e3"));
            Assert.That(row.Title, Is.EqualTo("lost"));
            Assert.That(row.LayerName, Is.EqualTo("gone"));
            Assert.That(row.TypeKind, Is.Empty);
        }

        [Test]
        public void ASelfRelationshipIsBothOutgoingAndIncoming()
        {
            var groups = Build(Edge("e1", "core", "hub", "core", "hub", ArchitectorEdgeKinds.Event));

            Assert.That(groups.Select(group => group.Incoming), Is.EqualTo(new[] { false, true }));
            Assert.That(groups.All(group => group.Rows.Single().NodeId == "hub"), Is.True);
            Assert.That(ArchitectorNodeRelationships.Build("core", "hub", null, null, null), Is.Empty);
        }
    }
}
