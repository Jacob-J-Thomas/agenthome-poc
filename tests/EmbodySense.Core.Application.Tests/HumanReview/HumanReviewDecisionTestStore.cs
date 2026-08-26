using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewDecisionTestStore(CustomLoopRunRecord run) : ICustomLoopRunStore
{
    private readonly object _gate = new();
    private bool _beforeUpdateInvoked;

    public CustomLoopRunRecord? Run { get; private set; } = run;
    public int ReadCount { get; private set; }
    public int UpdateCount { get; private set; }
    public int UpdateAttempts { get; private set; }
    public Func<string, CancellationToken, Task<CustomLoopRunRecord?>>? GetOverrideAsync { get; set; }
    public Func<CustomLoopRunRecord, int, CancellationToken, Task>? BeforeFirstUpdateAsync { get; set; }
    public Func<CustomLoopRunRecord, int, CancellationToken, Task<CustomLoopRunStoreResult>>? UpdateOverrideAsync { get; set; }
    public int VersionConflictsRemaining { get; set; }
    public bool PersistThenThrowOnce { get; set; }
    public bool PersistThenCancelOnce { get; set; }

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord candidate, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetOverrideAsync is not null)
        {
            return GetOverrideAsync(runId, cancellationToken);
        }

        lock (_gate)
        {
            ReadCount++;
            return Task.FromResult(Run);
        }
    }

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord candidate, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<CustomLoopRunRecord, int, CancellationToken, Task>? before = null;
        lock (_gate)
        {
            UpdateAttempts++;
            if (!_beforeUpdateInvoked && BeforeFirstUpdateAsync is not null)
            {
                _beforeUpdateInvoked = true;
                before = BeforeFirstUpdateAsync;
            }
        }

        if (before is not null)
        {
            await before(candidate, expectedLifecycleVersion, cancellationToken);
        }

        lock (_gate)
        {
            if (VersionConflictsRemaining > 0 && Run is not null)
            {
                VersionConflictsRemaining--;
                return CustomLoopRunStoreResult.VersionConflict(Run, expectedLifecycleVersion);
            }
        }

        if (UpdateOverrideAsync is not null)
        {
            return await UpdateOverrideAsync(candidate, expectedLifecycleVersion, cancellationToken);
        }

        lock (_gate)
        {
            if (Run is null)
            {
                return CustomLoopRunStoreResult.NotFound();
            }

            if (Run.LifecycleVersion != expectedLifecycleVersion)
            {
                return CustomLoopRunStoreResult.VersionConflict(Run, expectedLifecycleVersion);
            }

            var validation = CustomLoopRunValidator.ValidateUpdate(Run, candidate);
            if (!validation.IsValid)
            {
                throw new FormatException(string.Join(Environment.NewLine, validation.Errors));
            }

            Run = candidate;
            UpdateCount++;
            if (PersistThenThrowOnce)
            {
                PersistThenThrowOnce = false;
                throw new IOException("The durable write response was lost after commit.");
            }

            if (PersistThenCancelOnce)
            {
                PersistThenCancelOnce = false;
                throw new OperationCanceledException("The durable write completed before cancellation was observed.");
            }

            return CustomLoopRunStoreResult.Updated(candidate);
        }
    }
}
