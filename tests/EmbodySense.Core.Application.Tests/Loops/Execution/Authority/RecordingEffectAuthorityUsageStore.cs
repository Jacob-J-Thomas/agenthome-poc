using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal sealed class RecordingEffectAuthorityUsageStore : IGovernedLoopEffectAuthorityUsageStore
{
    internal GovernedLoopEffectAuthorityUsageStoreStatus ReserveStatus { get; set; } = GovernedLoopEffectAuthorityUsageStoreStatus.Allowed;

    internal Queue<GovernedLoopEffectAuthorityUsageStoreStatus> ReserveStatuses { get; } = [];

    internal Queue<GovernedLoopEffectAuthorityUsageStoreStatus> BeginStatuses { get; } = [];

    internal Queue<GovernedLoopEffectAuthorityUsageStoreStatus> CompleteStatuses { get; } = [];

    internal List<GovernedLoopEffectAuthorityUsageRequest> Reservations { get; } = [];

    internal List<GovernedLoopEffectAuthorityCompletionUsageRequest> CompletionBegins { get; } = [];

    internal List<GovernedLoopEffectAuthorityCompletionUsageRequest> CompletionCompletes { get; } = [];

    internal Exception? Exception { get; set; }

    internal Exception? BeginException { get; set; }

    internal Exception? CompleteException { get; set; }

    internal bool? CompleteObservedCancellation { get; private set; }

    public Task<GovernedLoopEffectAuthorityUsageStoreResult> ReserveAsync(
        GovernedLoopEffectAuthorityUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Exception is { } exception)
        {
            return Task.FromException<GovernedLoopEffectAuthorityUsageStoreResult>(exception);
        }

        Reservations.Add(request);
        var status = ReserveStatuses.Count > 0 ? ReserveStatuses.Dequeue() : ReserveStatus;
        return Task.FromResult(new GovernedLoopEffectAuthorityUsageStoreResult(status));
    }

    public Task<GovernedLoopEffectAuthorityUsageStoreResult> BeginCompletionAsync(
        GovernedLoopEffectAuthorityCompletionUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((BeginException ?? Exception) is { } exception)
        {
            return Task.FromException<GovernedLoopEffectAuthorityUsageStoreResult>(exception);
        }

        CompletionBegins.Add(request);
        var status = BeginStatuses.Count > 0
            ? BeginStatuses.Dequeue()
            : GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending;
        return Task.FromResult(new GovernedLoopEffectAuthorityUsageStoreResult(status));
    }

    public Task<GovernedLoopEffectAuthorityUsageStoreResult> CompleteCompletionAsync(
        GovernedLoopEffectAuthorityCompletionUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        CompleteObservedCancellation = cancellationToken.IsCancellationRequested;
        cancellationToken.ThrowIfCancellationRequested();
        if ((CompleteException ?? Exception) is { } exception)
        {
            return Task.FromException<GovernedLoopEffectAuthorityUsageStoreResult>(exception);
        }

        CompletionCompletes.Add(request);
        var status = CompleteStatuses.Count > 0
            ? CompleteStatuses.Dequeue()
            : GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted;
        return Task.FromResult(new GovernedLoopEffectAuthorityUsageStoreResult(status));
    }
}
