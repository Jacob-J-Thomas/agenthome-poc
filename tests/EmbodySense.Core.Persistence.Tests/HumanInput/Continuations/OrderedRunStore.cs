using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal sealed class OrderedRunStore : ICustomLoopRunStore
{
    private readonly IReadOnlyDictionary<string, CustomLoopRunRecord> _runs;
    private readonly Queue<IReadOnlyList<CustomLoopRunRecord>> _pages;

    internal OrderedRunStore(IReadOnlyList<CustomLoopRunRecord> runs, params IReadOnlyList<CustomLoopRunRecord>[] pages)
    {
        _runs = runs.ToDictionary(run => run.Id, StringComparer.Ordinal);
        _pages = new Queue<IReadOnlyList<CustomLoopRunRecord>>(pages);
    }

    internal int GetCount { get; private set; }

    internal int ListPageCount { get; private set; }

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCount++;
        return Task.FromResult(_runs.GetValueOrDefault(runId));
    }

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);

    public Task<CustomLoopRunPage> ListPageAsync(CustomLoopRunPageRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListPageCount++;
        var page = _pages.Count == 0 ? [] : _pages.Dequeue();
        return Task.FromResult(new CustomLoopRunPage(page.Select(Summary).ToArray(), null));
    }

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>([]);

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord value, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private static CustomLoopRunSummary Summary(CustomLoopRunRecord value)
        => new(value.Id, value.LoopId, value.AdmissionOperationId, value.AdmittedDefinition.DefinitionVersion, value.LifecycleVersion, value.Status, value.CreatedAtUtc, value.UpdatedAtUtc, value.CompletedAtUtc, value.Checkpoint.Iteration, value.Checkpoint.NextStepIndex, value.FailureCode, false);
}
