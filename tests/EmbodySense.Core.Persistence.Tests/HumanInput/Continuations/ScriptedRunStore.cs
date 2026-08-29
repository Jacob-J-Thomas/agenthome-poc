using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal sealed class ScriptedRunStore : ICustomLoopRunStore
{
    private readonly CustomLoopRunSummary _summary;

    internal ScriptedRunStore(CustomLoopRunRecord run)
    {
        _summary = Summary(run);
        Run = run;
    }

    internal Exception? GetException { get; set; }

    internal int GetCount { get; private set; }

    internal int ListPageCount { get; private set; }

    internal Exception? ListPageException { get; set; }

    internal CustomLoopRunPage? PageOverride { get; set; }

    internal CustomLoopRunRecord? Run { get; set; }

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        GetCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (GetException is not null)
        {
            throw GetException;
        }

        return Task.FromResult(string.Equals(runId, _summary.Id, StringComparison.Ordinal) ? Run : null);
    }

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);

    public Task<CustomLoopRunPage> ListPageAsync(CustomLoopRunPageRequest request, CancellationToken cancellationToken = default)
    {
        ListPageCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (ListPageException is not null)
        {
            throw ListPageException;
        }

        if (PageOverride is not null)
        {
            return Task.FromResult(PageOverride);
        }

        return Task.FromResult(new CustomLoopRunPage(request.Cursor is null ? [_summary] : [], null));
    }

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>([]);

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord value, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private static CustomLoopRunSummary Summary(CustomLoopRunRecord value)
        => new(
            value.Id,
            value.LoopId,
            value.AdmissionOperationId,
            value.AdmittedDefinition.DefinitionVersion,
            value.LifecycleVersion,
            value.Status,
            value.CreatedAtUtc,
            value.UpdatedAtUtc,
            value.CompletedAtUtc,
            value.Checkpoint.Iteration,
            value.Checkpoint.NextStepIndex,
            value.FailureCode,
            false);
}
