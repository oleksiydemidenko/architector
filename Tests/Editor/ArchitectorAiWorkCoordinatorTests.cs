using System;
using System.IO;
using NUnit.Framework;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorAiWorkCoordinatorTests
    {
        [Test]
        public void UserOverridesAreIndependentAndEndClearsSession()
        {
            var coordinator = new ArchitectorAiWorkCoordinator();
            var changed = 0;
            coordinator.Changed += () => changed++;

            var token = coordinator.Begin("Designing architecture");

            Assert.That(coordinator.IsActive, Is.True);
            Assert.That(coordinator.WorkspacesLocked, Is.True);
            Assert.That(coordinator.SettingsLocked, Is.True);
            Assert.Throws<ArchitectorDataException>(() => coordinator.RequireMutationToken("wrong"));
            Assert.DoesNotThrow(() => coordinator.RequireMutationToken(token));

            coordinator.UnlockWorkspacesByUser();
            Assert.That(coordinator.WorkspacesLocked, Is.False);
            Assert.That(coordinator.SettingsLocked, Is.True);
            Assert.That(coordinator.WorkspacesUnlockedByUser, Is.True);
            Assert.That(coordinator.SettingsUnlockedByUser, Is.False);

            coordinator.UnlockSettingsByUser();
            Assert.That(coordinator.SettingsLocked, Is.False);
            coordinator.End(token);

            Assert.That(coordinator.IsActive, Is.False);
            Assert.That(coordinator.WorkspacesUnlockedByUser, Is.False);
            Assert.That(coordinator.SettingsUnlockedByUser, Is.False);
            Assert.That(changed, Is.EqualTo(4));
        }

        [Test]
        public void DisabledWindowLockingKeepsLeaseWithoutLockingWindows()
        {
            var locksWindows = false;
            var coordinator = new ArchitectorAiWorkCoordinator(locksWindows: () => locksWindows);

            var token = coordinator.Begin("Designing architecture");

            Assert.That(coordinator.IsActive, Is.True);
            Assert.That(coordinator.WorkspacesLocked, Is.False);
            Assert.That(coordinator.SettingsLocked, Is.False);
            Assert.Throws<ArchitectorDataException>(() => coordinator.RequireMutationToken("wrong"));
            locksWindows = true;
            Assert.That(coordinator.WorkspacesLocked, Is.True);
            coordinator.End(token);
        }

        [Test]
        public void DurableMarkerRestoresLockUntilAgentResumesOrUserAbandonsIt()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var recoveryDirectory = Path.Combine(Path.GetTempPath(), "ArchitectorAiWorkTests", suffix);
            var recoveryPath = Path.Combine(recoveryDirectory, "ai-work-session.json");
            var original = new ArchitectorAiWorkCoordinator(true, "Architector.Tests.AiWork.A." + suffix, recoveryPath);
            var token = original.Begin("Persistent test", 17);
            Assert.That(File.Exists(recoveryPath), Is.True);

            var restored = new ArchitectorAiWorkCoordinator(true, "Architector.Tests.AiWork.B." + suffix, recoveryPath);
            Assert.That(restored.IsActive, Is.True);
            Assert.That(restored.IsAwaitingAgentResume, Is.True);
            Assert.That(restored.StartedRevision, Is.EqualTo(17));
            Assert.That(restored.WorkspacesLocked, Is.True);
            Assert.That(restored.SettingsLocked, Is.True);
            Assert.Throws<ArchitectorDataException>(() => restored.Resume("wrong"));
            Assert.That(restored.IsAwaitingAgentResume, Is.True);

            restored.Resume(token);
            Assert.That(restored.IsAwaitingAgentResume, Is.False);
            restored.UnlockWorkspacesByUser();
            Assert.That(restored.WorkspacesUnlockedByUser, Is.True);

            var interruptedAgain = new ArchitectorAiWorkCoordinator(true, "Architector.Tests.AiWork.C." + suffix, recoveryPath);
            Assert.That(interruptedAgain.IsAwaitingAgentResume, Is.True);
            Assert.That(interruptedAgain.WorkspacesLocked, Is.True, "A domain reload must discard temporary user overrides.");
            interruptedAgain.AbandonInterruptedSessionByUser();
            Assert.That(File.Exists(recoveryPath), Is.False);

            var afterAbandon = new ArchitectorAiWorkCoordinator(true, "Architector.Tests.AiWork.D." + suffix, recoveryPath);
            Assert.That(afterAbandon.IsActive, Is.False);
            Assert.Throws<ArchitectorDataException>(() => afterAbandon.RequireMutationToken(token));
            Directory.Delete(recoveryDirectory, false);
        }
    }
}
