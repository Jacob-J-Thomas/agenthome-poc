using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopRunStore
{
    Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default);

    Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default);
}
