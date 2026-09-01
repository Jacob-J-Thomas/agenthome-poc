using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryCanonicalRunStore : ICustomLoopRunStore
{
    public CustomLoopRunRecord? Current { get; private set; }

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Current is null)
        {
            Current = run;
            return Task.FromResult(CustomLoopRunStoreResult.Created(run));
        }

        return Task.FromResult(Current.Id == run.Id && Current.AdmissionOperationId == run.AdmissionOperationId
            ? CustomLoopRunStoreResult.AlreadyCreated(Current)
            : CustomLoopRunStoreResult.OperationConflict(Current));
    }

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current is not null && string.Equals(Current.Id, runId, StringComparison.Ordinal) ? Current : null);
    }

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current is not null && string.Equals(Current.AdmissionOperationId, admissionOperationId, StringComparison.Ordinal) ? Current : null);
    }

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current is not null && string.Equals(Current.LoopId, loopId, StringComparison.Ordinal) && !Current.IsTerminal ? Current : null);
    }

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);
    }

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>(Current is not null && !Current.IsTerminal ? [Current] : []);
    }

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Current is null || !string.Equals(Current.Id, run.Id, StringComparison.Ordinal))
        {
            return Task.FromResult(CustomLoopRunStoreResult.NotFound());
        }

        if (Current.LifecycleVersion != expectedLifecycleVersion)
        {
            return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion));
        }

        var validation = CustomLoopRunValidator.ValidateUpdate(Current, run);
        if (!validation.IsValid)
        {
            throw new FormatException(string.Join(Environment.NewLine, validation.Errors));
        }

        Current = run;
        return Task.FromResult(CustomLoopRunStoreResult.Updated(run));
    }
}
