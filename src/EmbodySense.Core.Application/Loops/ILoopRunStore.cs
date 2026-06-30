using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops;

public interface ILoopRunStore
{
    Task SaveAsync(LoopRunRecord run, CancellationToken cancellationToken = default);

    Task<LoopRunRecord?> LoadAsync(string loopId, string runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoopRunRecord>> ListAsync(string loopId, CancellationToken cancellationToken = default);
}
