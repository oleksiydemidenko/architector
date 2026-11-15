using System;
using System.IO;
using System.Linq;
using Architector.Editor;
using NUnit.Framework;

namespace Architector.Core.Tests
{
    public sealed class RepositoryTests
    {
        private string project;

        [SetUp]
        public void SetUp()
        {
            ArchitectorHost.Current = new ArchitectorProcessHost(Path.GetTempPath());
            var source = TestData.RepositoryWithArchitecture();
            if (source == null)
                Assert.Ignore("No Architector repository found; set ARCHITECTOR_TEST_PROJECT.");
            project = TestData.CopyOfArchitecture(source);
        }

        [TearDown]
        public void TearDown()
        {
            if (project != null && Directory.Exists(project))
                Directory.Delete(project, true);
        }

        private ArchitectorRepository Open(bool newSession = false) => new(new ArchitectorPaths(project), newSession);

        private static ArchitectorSemanticPlan NewLayer(string parent, string name) => new()
        {
            layers = { new ArchitectorPlanLayer { reference = "layer", name = name, parent = parent, purpose = "Test." } },
            nodes = { new ArchitectorPlanNode { reference = "node", layer = "layer", title = "Node", responsibility = "Tests." } }
        };

        private static string UnlockedRootGroup(ArchitectorRepository repository) =>
            repository.Project.groups.First(group => string.IsNullOrEmpty(group.parentGroupId) && !repository.IsGroupLocked(group.id)).name;

        [Test]
        public void ACommandLineProcessKeepsTheStoredRevisionAndStoresItsOwnChange()
        {
            var first = Open();
            var second = Open();
            Assert.That(second.Revision, Is.EqualTo(first.Revision), "reading does not start a new revision");

            first.ApplySemanticPlan(NewLayer(UnlockedRootGroup(first), "Core Test Layer"));

            var next = Open();
            Assert.That(next.Revision, Is.EqualTo(first.Revision));
            Assert.That(next.Project.layers.Any(layer => layer.name == "Core Test Layer"), Is.True);
            Assert.That(Open(newSession: true).Revision, Is.EqualTo(first.Revision + 1), "the Editor opens a new revision");
        }

        [Test]
        public void AWriterThatReadBeforeAnotherWriteIsRefusedAndReloads()
        {
            var editor = Open(newSession: true);
            var commandLine = Open();
            commandLine.ApplySemanticPlan(NewLayer(UnlockedRootGroup(commandLine), "Written Elsewhere"));

            Assert.Throws<ArchitectorDataException>(() => editor.ApplySemanticPlan(NewLayer(UnlockedRootGroup(editor), "Stale Writer")));
            Assert.That(editor.Project.layers.Any(layer => layer.name == "Written Elsewhere"), Is.True, "the refused writer reloaded the change");
            Assert.That(editor.Revision, Is.GreaterThanOrEqualTo(commandLine.Revision));
        }

        [Test]
        public void TheEditorFollowsACommandLineEditAndItsWorkSession()
        {
            var editor = Open(newSession: true);
            var paths = new ArchitectorPaths(project);
            var editorWork = new ArchitectorAiWorkCoordinator(true, "Test.EditorWork", paths.AiWorkRecoveryPath);
            var commandLine = Open();
            var commandLineWork = new ArchitectorAiWorkCoordinator(true, "Test.CommandLineWork", paths.AiWorkRecoveryPath);

            var token = commandLineWork.Begin("Command line", commandLine.Revision);
            Assert.That(editorWork.RefreshFromRecoveryMarker(), Is.True);
            Assert.That(editorWork.WorkToken, Is.EqualTo(token), "the Editor locks its windows for the agent's work");

            commandLine.ApplySemanticPlan(NewLayer(UnlockedRootGroup(commandLine), "From The Command Line"));
            Assert.That(editor.StoredRevision, Is.GreaterThan(editor.Revision));
            Assert.That(editor.RefreshExternalChanges(), Is.True);
            editor.AdoptStoredRevision();
            Assert.That(editor.Revision, Is.EqualTo(commandLine.Revision), "both give agents the same revision");
            Assert.That(editor.Project.layers.Any(layer => layer.name == "From The Command Line"), Is.True);

            commandLineWork.End(token);
            Assert.That(editorWork.RefreshFromRecoveryMarker(), Is.True);
            Assert.That(editorWork.IsActive, Is.False);
        }

        [Test]
        public void ASecondWriterWaitsForTheWriteLockAndIsRefusedWithoutAChange()
        {
            var repository = Open();
            var before = TestData.CanonicalFiles(project);
            var wait = ArchitectorWriteLock.WaitMilliseconds;
            var lockPath = ArchitectorWriteLock.LockPath(repository.Paths);
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
            ArchitectorWriteLock.WaitMilliseconds = 200;
            try
            {
                using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    Assert.Throws<ArchitectorWriteBusyException>(() => repository.ApplySemanticPlan(NewLayer(UnlockedRootGroup(repository), "Blocked")));
                Assert.That(TestData.CanonicalFiles(project), Is.EqualTo(before));
                repository.ApplySemanticPlan(NewLayer(UnlockedRootGroup(repository), "Free Again"));
            }
            finally
            {
                ArchitectorWriteLock.WaitMilliseconds = wait;
            }
        }

        [Test]
        public void ALockedGroupRefusesAChangeWithoutUnlock()
        {
            var repository = Open();
            var locked = repository.Project.groups.FirstOrDefault(group => repository.IsGroupLocked(group.id));
            if (locked == null)
                Assert.Ignore("The test repository has no locked group.");
            var before = TestData.CanonicalFiles(project);

            Assert.Throws<ArchitectorDataException>(() => repository.ApplySemanticPlan(NewLayer(locked.name, "Forbidden")));
            Assert.That(TestData.CanonicalFiles(project), Is.EqualTo(before));
        }
    }
}
