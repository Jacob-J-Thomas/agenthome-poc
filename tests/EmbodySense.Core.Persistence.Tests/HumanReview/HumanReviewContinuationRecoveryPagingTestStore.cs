using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

internal sealed class HumanReviewContinuationRecoveryPagingTestStore(
    CustomLoopRunPage page,
    CustomLoopRunRecord? run = null,
    Exception? listException = null,
    Exception? getException = null) : ICustomLoopRunStore
{
    public string? ReceivedCursor { get; private set; }

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => getException is null ? Task.FromResult(run) : Task.FromException<CustomLoopRunRecord?>(getException);

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunPage> ListPageAsync(CustomLoopRunPageRequest request, CancellationToken cancellationToken = default)
    {
        ReceivedCursor = request.Cursor;
        return listException is null ? Task.FromResult(page) : Task.FromException<CustomLoopRunPage>(listException);
    }

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
