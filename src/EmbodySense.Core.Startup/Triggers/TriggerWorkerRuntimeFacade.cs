using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Startup.Triggers.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Exposes explicit one-shot trigger worker execution and posture without adding a background host.</summary>
public sealed class TriggerWorkerRuntimeFacade
{
    private readonly ITriggerQueueQueryPort _query;
    private readonly TriggerWorkerService _worker;

    internal TriggerWorkerRuntimeFacade(ITriggerQueueQueryPort query, TriggerWorkerService worker)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
    }

    /// <summary>Loads deterministic queue, ownership, and dispatch posture at one UTC instant.</summary>
    /// <param name="observedAtUtc">The exact UTC observation instant.</param>
    /// <param name="cancellationToken">A token honored before durable expiry reconciliation commits.</param>
    /// <returns>The bounded Startup projection.</returns>
    public async Task<TriggerWorkerQueueSnapshot> GetSnapshotAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        var snapshot = await _query.GetSnapshotAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
        return new TriggerWorkerQueueSnapshot(snapshot.Generation, snapshot.PersistenceBackpressured, snapshot.Entries.Select(Map).ToArray());
    }

    /// <summary>Selects and dispatches at most one eligible entry through exact durable ownership and intent.</summary>
    /// <param name="input">The exact revision, fairness, and lease inputs.</param>
    /// <param name="cancellationToken">A token honored only before durable dispatch intent.</param>
    /// <returns>The latest durable worker posture.</returns>
    public async Task<TriggerWorkerRunResponse> RunOnceAsync(TriggerWorkerSelectionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var selection = new TriggerWorkerSelectionRequest(input.WorkerId, input.ExpectedQueueGeneration, input.ObservedAtUtc, input.LeaseDuration, input.RecentLoopIds, input.MaxConsecutiveSelectionsPerLoop);
        var result = await _worker.RunOnceAsync(new TriggerWorkerRunRequest(selection), cancellationToken).ConfigureAwait(false);
        return new TriggerWorkerRunResponse(result.SelectionStatus.ToString(), result.MutationStatus?.ToString(), result.Entry is null ? null : Map(result.Entry));
    }

    private static TriggerWorkerEntrySnapshot Map(TriggerQueueEntry entry)
    {
        return new TriggerWorkerEntrySnapshot(entry.DeliveryId.Value, entry.LoopId, entry.State.ToString(), entry.Revision, entry.WorkerLease?.WorkerId, entry.WorkerLease?.Generation, entry.WorkerLease?.ExpiresAtUtc, entry.WorkerLease?.ReleasedAtUtc, entry.Dispatch?.Outcome.ToString(), entry.Dispatch?.OperationId, entry.Dispatch?.Detail, entry.Dispatch?.GovernedInvocation?.RunId, entry.Dispatch?.GovernedInvocation?.AdmissionRequestHash, entry.Dispatch?.GovernedInvocation?.LoopReferenceHash);
    }
}
