using System;
using System.Linq;
using Architector.Editor.Agents;
using NUnit.Framework;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorIntegrationCatalogTests
    {
        [TestCase("lazy", "lazy")]
        [TestCase("extension", "lazy")]
        [TestCase("tag:Extension", "lazy")]
        [TestCase("#MCP", "server")]
        [TestCase("TAG:mcp architector", "server")]
        [TestCase("mcp lazy", "")]
        public void SearchMatchesNamesAndTagsAndCombinesTerms(string search, string expected)
        {
            var entries = new[] { Entry("server", "Architector server", "MCP"), Entry("lazy", "Lazy Mode", "Extension") };
            Assert.That(string.Join(",", ArchitectorIntegrationCatalog.Filter(entries, search).Select(entry => entry.Id)), Is.EqualTo(expected));
        }

        [Test]
        public void TagFilterKeepsExtensionsBeforeMcpAndCombinesWithSearch()
        {
            var entries = new[] { Entry("server", "Architector server", "MCP"), Entry("lazy", "Lazy Mode", "Extension") };
            Assert.That(ArchitectorIntegrationCatalog.Filter(entries, "").Select(entry => entry.Id), Is.EqualTo(new[] { "lazy", "server" }));
            Assert.That(ArchitectorIntegrationCatalog.Filter(entries, "lazy", "MCP"), Is.Empty);
            Assert.That(ArchitectorIntegrationCatalog.Filter(entries, "lazy", "extension").Single().Id, Is.EqualTo("lazy"));
        }

        [Test]
        public void AnExternalEntryCanRegisterDrawAndUnregisterWithoutChangingBuiltIns()
        {
            var drawn = false;
            var entry = new ArchitectorIntegrationEntry("catalog-test", "External tool", new[] { "Extension", "Checks" }, () => drawn = true);
            try
            {
                ArchitectorIntegrationCatalog.Register(entry);
                var found = ArchitectorIntegrationCatalog.Entries.Single(item => item.Id == entry.Id);
                Assert.That(ArchitectorIntegrationCatalog.Filter(new[] { found }, "#Checks").Single(), Is.SameAs(entry));
                found.DrawDetails();
                Assert.That(drawn, Is.True);
                Assert.Throws<InvalidOperationException>(() => ArchitectorIntegrationCatalog.Register(entry));
                Assert.Throws<InvalidOperationException>(() => ArchitectorIntegrationCatalog.Register(Entry("lazy", "Collision", "Extension")));
            }
            finally
            {
                ArchitectorIntegrationCatalog.Unregister(entry.Id);
            }
            Assert.That(ArchitectorIntegrationCatalog.Entries.Any(item => item.Id == entry.Id), Is.False);
        }

        private static ArchitectorIntegrationEntry Entry(string id, string name, string tag) =>
            new(id, name, new[] { tag }, () => { });
    }
}
