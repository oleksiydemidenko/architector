using System.Threading.Tasks;

namespace Architector.Editor
{
    public sealed class ArchitectorPackageUpdateProvider : IArchitectorIntegrationUpdateProvider
    {
        public static ArchitectorPackageUpdateProvider Instance { get; } = new();
        public string Id => "architector";

        public async Task<ArchitectorIntegrationUpdate> CheckAsync()
        {
            await ArchitectorPackageUpdates.CheckAsync();
            return Snapshot();
        }

        public ArchitectorIntegrationUpdate Snapshot()
        {
            var state = ArchitectorPackageUpdates.State;
            return new ArchitectorIntegrationUpdate
            {
                installedVersion = ArchitectorPackagePaths.Version,
                availableVersion = state.latestTag,
                compatible = string.IsNullOrEmpty(state.error) && ArchitectorPackageUpdates.SourceWarning.Length == 0,
                details = state.error ?? state.notice,
                releaseNotes = state.releaseNotes
            };
        }

        public Task UpdateAsync(ArchitectorIntegrationUpdate update, ArchitectorAiWorkCoordinator work) =>
            ArchitectorPackageUpdates.InstallAsync(work);
    }
}
