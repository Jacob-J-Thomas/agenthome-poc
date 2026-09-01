using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal sealed class RecordingRunStore : ICustomLoopRunStore
{
    public CustomLoopRunPage? Page { get; init; }
    public Exception? PageException { get; init; }
    public CustomLoopRunRecord? Run { get; init; }
    public Exception? GetException { get; init; }
    public List<string?> Cursors { get; } = [];
    public List<string> GetIds { get; } = [];

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopRunStoreResult.NotFound());

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        GetIds.Add(runId);
        if (GetException is not null)
        {
            throw GetException;
        }

        return Task.FromResult(Run);
    }

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);

    public Task<CustomLoopRunPage> ListPageAsync(CustomLoopRunPageRequest request, CancellationToken cancellationToken = default)
    {
        Cursors.Add(request.Cursor);
        if (PageException is not null)
        {
            throw PageException;
        }

        return Task.FromResult(Page ?? new CustomLoopRunPage([], null));
    }

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>([]);

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopRunStoreResult.NotFound());
}
