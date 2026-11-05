using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Architector.Editor.Agents;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorAgentApiTests
    {
        private string temporaryRoot;
        private ArchitectorRepository repository;

        [SetUp]
        public void SetUp()
        {
            temporaryRoot = Path.Combine(Path.GetTempPath(), "ArchitectorAgentTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            repository = new ArchitectorRepository(new ArchitectorPaths(temporaryRoot));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, true);
        }

        [Test]
        public void QueryRunsWithoutAnyTransportPackage()
        {
            repository.CreateLayer(string.Empty, "Runtime");

            var result = ArchitectorAgentQuery.Handle(
                repository,
                new ArchitectorAiWorkCoordinator(),
                JObject.Parse("{ 'action': 'project_summary' }"));

            var json = JObject.Parse(result.ToJson());
            Assert.That(json["success"]?.Value<bool>(), Is.True);
            Assert.That(json["data"]?["layer_count"]?.Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void ArrangeChangedLayersLaysOutNewLayersAndPlacesAddedNodesWithoutMovingTheRest()
        {
            var oldLayer = repository.CreateLayer(string.Empty, "Old");
            var anchor = repository.CreateEmptyNode(oldLayer, 0f, 0f);
            var other = repository.CreateEmptyNode(oldLayer, 900f, 0f);
            Vector2 Position(string layer, string node)
            {
                var layout = repository.LoadLayer(layer).layout.nodes.Single(item => item.nodeId == node);
                return new Vector2(layout.x, layout.y);
            }
            var anchorBefore = Position(oldLayer, anchor);
            var otherBefore = Position(oldLayer, other);
            var work = new ArchitectorAiWorkCoordinator();
            JObject Edit(string json) => JObject.Parse(ArchitectorAgentEdit.Handle(repository, work, JObject.Parse(json)).ToJson());
            var token = Edit($"{{ 'action': 'begin_work', 'expected_revision': {repository.Revision} }}")["data"]?["work_token"]?.Value<string>();

            var added = repository.CreateEmptyNode(oldLayer, 0f, 1800f);
            repository.CreateEdge(oldLayer, added, anchor, ArchitectorEdgeKinds.Dependency, "auto");
            var newLayer = repository.CreateLayer(string.Empty, "New");
            var first = repository.CreateEmptyNode(newLayer, 0f, 0f);
            var second = repository.CreateEmptyNode(newLayer, 0f, 0f);
            var third = repository.CreateEmptyNode(newLayer, 0f, 0f);
            repository.CreateEdge(newLayer, first, second, ArchitectorEdgeKinds.Dependency, "auto");
            repository.CreateEdge(newLayer, first, third, ArchitectorEdgeKinds.Dependency, "auto");
            var distanceBefore = Vector2.Distance(Position(oldLayer, added), anchorBefore);

            var result = Edit($"{{ 'action': 'arrange_changed_layers', 'expected_revision': {repository.Revision}, 'work_token': '{token}' }}");

            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["data"]?["arranged"]?.Select(item => item["layer"]?.Value<string>()), Is.EquivalentTo(new[] { oldLayer, newLayer }));
            Assert.That(Position(oldLayer, anchor), Is.EqualTo(anchorBefore));
            Assert.That(Position(oldLayer, other), Is.EqualTo(otherBefore));
            Assert.That(Vector2.Distance(Position(oldLayer, added), anchorBefore), Is.LessThan(distanceBefore));
            var rects = new[] { first, second, third }
                .Select(node => repository.LoadLayer(newLayer).layout.nodes.Single(item => item.nodeId == node))
                .Select(node => new Rect(node.x, node.y, node.width, node.height))
                .ToArray();
            for (var index = 1; index < rects.Length; index++)
                for (var previous = 0; previous < index; previous++)
                    Assert.That(rects[index].Overlaps(rects[previous]), Is.False);
            Assert.That(Edit($"{{ 'action': 'end_work', 'work_token': '{token}' }}")["success"]?.Value<bool>(), Is.True);
            Assert.That(Edit($"{{ 'action': 'arrange_changed_layers', 'expected_revision': {repository.Revision} }}")["success"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void QueriesReportAndTraverseDependencyCycles()
        {
            repository.UpdateSettings("Report Cycles", settings =>
                settings.rules.dependencyCycles = ArchitectorDependencyCyclePolicy.Report);
            var first = repository.CreateLayer(string.Empty, "First");
            var second = repository.CreateLayer(string.Empty, "Second");
            var third = repository.CreateLayer(string.Empty, "Third");
            var firstNode = repository.CreateEmptyNode(first, 0f, 0f);
            var secondNode = repository.CreateEmptyNode(second, 0f, 0f);
            var thirdNode = repository.CreateEmptyNode(third, 0f, 0f);
            repository.CreateCrossLayerEdge(first, firstNode, second, secondNode, ArchitectorEdgeKinds.Dependency);
            repository.CreateCrossLayerEdge(second, secondNode, first, firstNode, ArchitectorEdgeKinds.Dependency);
            repository.CreateCrossLayerEdge(second, secondNode, third, thirdNode, ArchitectorEdgeKinds.Dependency);
            var work = new ArchitectorAiWorkCoordinator();
            JObject Query(string json) => JObject.Parse(ArchitectorAgentQuery.Handle(repository, work, JObject.Parse(json)).ToJson());

            var capabilities = Query("{ 'action': 'capabilities' }");
            var validation = Query("{ 'action': 'validate' }");
            var path = Query("{ 'action': 'dependency_path', 'target': 'First', 'to': 'Third' }");
            var none = Query("{ 'action': 'dependency_path', 'target': 'Third', 'to': 'First' }");

            Assert.That(capabilities["data"]?["dependency_cycles"]?.Value<string>(), Is.EqualTo("report"));
            Assert.That(validation["success"]?.Value<bool>(), Is.True);
            Assert.That(validation["data"]?["dependency_cycle_policy"]?.Value<string>(), Is.EqualTo("report"));
            Assert.That(((JArray)validation["data"]?["dependency_cycles"])?.Count, Is.EqualTo(1));
            Assert.That(validation["data"]?["dependency_cycles"]?[0]?["path"]?.Value<string>(),
                Is.EqualTo("First/New Node → Second/New Node → First/New Node"));
            Assert.That(path["data"]?["connected"]?.Value<bool>(), Is.True);
            Assert.That(((JArray)path["data"]?["steps"])?.Count, Is.EqualTo(2));
            Assert.That(none["data"]?["connected"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void LayerSummaryIsCompactTextWithoutIds()
        {
            var core = repository.CreateLayer(string.Empty, "Core");
            var data = repository.CreateLayer(string.Empty, "Data");
            string Node(string layer, string title)
            {
                var id = repository.CreateEmptyNode(layer, 0f, 0f);
                repository.RenameEmptyNode(layer, id, title);
                return id;
            }
            var store = Node(core, "Store");
            var cache = Node(core, "Cache");
            var reader = Node(data, "Reader");
            var writer = Node(data, "Writer");
            repository.UpdateNodeResponsibility(core, store, "Keeps the records.");
            repository.CreateEdge(core, cache, store, ArchitectorEdgeKinds.Dependency, "auto");
            repository.CreateCrossLayerEdge(data, reader, core, store, ArchitectorEdgeKinds.Dependency);
            repository.CreateCrossLayerEdge(data, writer, core, store, ArchitectorEdgeKinds.Event);
            repository.CreateCrossLayerEdge(data, writer, core, cache, ArchitectorEdgeKinds.Dependency);

            var result = JObject.Parse(ArchitectorAgentQuery.Handle(
                repository,
                new ArchitectorAiWorkCoordinator(),
                JObject.Parse("{ 'action': 'layer_graph', 'target': 'Core', 'detail': 'summary' }")).ToJson());
            var summary = result["data"]?["summary"]?.Value<string>();

            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(summary, Does.Contain("Layer: Core"));
            Assert.That(summary, Does.Contain("- Store — Keeps the records."));
            Assert.That(summary, Does.Contain("- Cache -> Store dependency"));
            Assert.That(summary, Does.Contain("Incoming (3 from 1 layers;"));
            Assert.That(summary, Does.Contain("- Data: 3 (dependency 2, event 1) to Store, Cache"));
            Assert.That(summary, Does.Not.Contain(store).And.Not.Contain(core).And.Not.Contain("portal"));
        }

        [Test]
        public void BridgeRefusesAWrongToken()
        {
            Assume.That(ArchitectorAgentBridge.Port, Is.GreaterThan(0), "The bridge runs only in an interactive Editor.");
            using var client = new TcpClient("127.0.0.1", ArchitectorAgentBridge.Port);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(stream);

            writer.WriteLine("{ 'token': 'wrong', 'tool': 'query', 'parameters': { 'action': 'project_summary' } }");

            Assert.That(JObject.Parse(reader.ReadLine())["code"]?.Value<string>(), Is.EqualTo("unauthorized"));
        }

        [Test]
        public void EditKeepsRevisionCheckAndReportsItsCode()
        {
            var result = ArchitectorAgentEdit.Handle(
                repository,
                new ArchitectorAiWorkCoordinator(),
                JObject.Parse("{ 'action': 'apply' }"));

            var json = JObject.Parse(result.ToJson());
            Assert.That(json["success"]?.Value<bool>(), Is.False);
            Assert.That(json["code"]?.Value<string>(), Is.EqualTo("expected_revision_required"));
        }
    }
}
