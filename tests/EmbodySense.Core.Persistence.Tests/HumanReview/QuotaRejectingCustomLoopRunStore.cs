using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

internal sealed class QuotaRejectingCustomLoopRunStore(ICustomLoopRunStore inner) : ICustomLoopRunStore
{
    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => inner.CreateAsync(run, cancellationToken);

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default) => inner.GetAsync(runId, cancellationToken);

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => inner.GetByAdmissionOperationAsync(admissionOperationId, cancellationToken);

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => inner.GetNonterminalByLoopAsync(loopId, cancellationToken);

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => inner.ListRecentAsync(maximumCount, cancellationToken);

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => inner.ListNonterminalAsync(cancellationToken);

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopRunStoreResult.LimitExceeded());
}
