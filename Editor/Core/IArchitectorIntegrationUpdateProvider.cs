using System.Threading.Tasks;

namespace Architector.Editor
{
    public interface IArchitectorIntegrationUpdateProvider
    {
        string Id { get; }
        Task<ArchitectorIntegrationUpdate> CheckAsync();
        Task UpdateAsync(ArchitectorIntegrationUpdate update, ArchitectorAiWorkCoordinator work);
    }
}
