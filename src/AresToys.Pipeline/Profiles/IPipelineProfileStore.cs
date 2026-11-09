using AresToys.Core.Pipeline;

namespace AresToys.Pipeline.Profiles;

public interface IPipelineProfileStore
{
    Task<PipelineProfile?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PipelineProfile>> ListAsync(CancellationToken cancellationToken);
    Task UpsertAsync(PipelineProfile profile, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}
