using System;
using System.Threading.Tasks;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorUpdateTestProvider : IArchitectorIntegrationUpdateProvider
    {
        public string Id { get; } = "update-test-" + Guid.NewGuid().ToString("N");
        public string Installed = "1.0.0";
        public string Latest = "1.1.0";
        public bool Compatible = true;
        public bool FailCheck;
        public bool FailInstall;
        public int Installs;
        public Action OnCheck;

        public Task<ArchitectorIntegrationUpdate> CheckAsync()
        {
            OnCheck?.Invoke();
            if (FailCheck)
                throw new InvalidOperationException("Check failed");
            return Task.FromResult(new ArchitectorIntegrationUpdate
                { installedVersion = Installed, availableVersion = Latest, compatible = Compatible });
        }

        public Task UpdateAsync(ArchitectorIntegrationUpdate update, ArchitectorAiWorkCoordinator work)
        {
            Installs++;
            if (FailInstall)
                throw new InvalidOperationException("Install failed");
            Installed = update.availableVersion.TrimStart('v');
            return Task.CompletedTask;
        }
    }
}
