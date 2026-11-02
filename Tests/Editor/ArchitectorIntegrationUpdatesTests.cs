using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorIntegrationUpdatesTests
    {
        private ArchitectorUpdateTestProvider provider;

        [SetUp]
        public void SetUp() { provider = new ArchitectorUpdateTestProvider(); }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.DeleteKey("Architector.IntegrationUpdate." + Application.dataPath + "." + provider.Id);
            ArchitectorIntegrationUpdates.Unregister(provider.Id);
        }

        [Test]
        public void CheckingNeverInstallsAndTheExplicitUpdateConfirmsTheInstalledVersion()
        {
            ArchitectorIntegrationUpdates.CheckAsync(provider).GetAwaiter().GetResult();
            Assert.That(provider.Installs, Is.Zero);
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Update.Available, Is.True);
            ArchitectorIntegrationUpdates.InstallAsync(provider, new ArchitectorAiWorkCoordinator()).GetAwaiter().GetResult();
            Assert.That(provider.Installs, Is.EqualTo(1));
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Error, Is.Null);
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Update.installedVersion, Is.EqualTo("1.1.0"));
        }

        [Test]
        public void IncompatibleOffersAndActiveLeasesDoNotInstall()
        {
            provider.Compatible = false;
            ArchitectorIntegrationUpdates.CheckAsync(provider).GetAwaiter().GetResult();
            ArchitectorIntegrationUpdates.InstallAsync(provider, new ArchitectorAiWorkCoordinator()).GetAwaiter().GetResult();
            Assert.That(provider.Installs, Is.Zero);
            provider.Compatible = true;
            ArchitectorIntegrationUpdates.CheckAsync(provider).GetAwaiter().GetResult();
            var work = new ArchitectorAiWorkCoordinator(locksWindows: () => false);
            work.Begin("test");
            ArchitectorIntegrationUpdates.InstallAsync(provider, work).GetAwaiter().GetResult();
            Assert.That(provider.Installs, Is.Zero);
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Error, Does.Contain("agent work"));
        }

        [Test]
        public void FailuresAreVisibleAndAnExplicitRetryCanSucceed()
        {
            provider.FailCheck = true;
            ArchitectorIntegrationUpdates.CheckAsync(provider).GetAwaiter().GetResult();
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Error, Is.EqualTo("Check failed"));
            provider.FailCheck = false;
            ArchitectorIntegrationUpdates.CheckAsync(provider).GetAwaiter().GetResult();
            provider.FailInstall = true;
            ArchitectorIntegrationUpdates.InstallAsync(provider, new ArchitectorAiWorkCoordinator()).GetAwaiter().GetResult();
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Error, Is.EqualTo("Install failed"));
            provider.FailInstall = false;
            ArchitectorIntegrationUpdates.InstallAsync(provider, new ArchitectorAiWorkCoordinator()).GetAwaiter().GetResult();
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Error, Is.Null);
        }

        [Test]
        public void ALeaseStartedDuringRevalidationStillPreventsInstallation()
        {
            ArchitectorIntegrationUpdates.CheckAsync(provider).GetAwaiter().GetResult();
            var work = new ArchitectorAiWorkCoordinator(locksWindows: () => false);
            provider.OnCheck = () => work.Begin("started during check");
            ArchitectorIntegrationUpdates.InstallAsync(provider, work).GetAwaiter().GetResult();
            Assert.That(provider.Installs, Is.Zero);
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Error, Does.Contain("agent work"));
        }

        [Test]
        public void RecheckingAfterReloadAcknowledgesPendingCompletionWithoutReinstalling()
        {
            ArchitectorIntegrationUpdates.Register(provider);
            EditorPrefs.SetString("Architector.IntegrationUpdate." + Application.dataPath + "." + provider.Id, "v1.1.0");
            provider.Installed = "1.1.0";
            ArchitectorIntegrationUpdates.CheckAsync(provider).GetAwaiter().GetResult();
            Assert.That(provider.Installs, Is.Zero);
            Assert.That(ArchitectorIntegrationUpdates.State(provider.Id).Notice, Does.Contain("installed"));
        }
    }
}
