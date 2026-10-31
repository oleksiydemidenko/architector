using System;
using System.IO;
using Architector.Editor.Agents;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

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
