using System.Globalization;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;

namespace EmbodySense.HumanInputContinuationHost;

internal static class GovernedLoopCoordinatorHost
{
    private const string CoordinatorId = "background-coordinator";

    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var mode, var workspaceRoot, var ownerId, var epochText, var acquiredTicksText, var resultPath]
            || !long.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || !long.TryParse(acquiredTicksText, NumberStyles.None, CultureInfo.InvariantCulture, out var acquiredTicks)
            || epoch < 1)
        {
            return 2;
        }

        var acquiredAtUtc = new DateTimeOffset(acquiredTicks, TimeSpan.Zero);
        var store = new GovernedLoopCoordinatorEvidenceStore(new WorkspacePaths(workspaceRoot));
        var status = mode switch
        {
            "initial" => await AcquireAsync(store, ownerId, epoch, acquiredAtUtc, GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound, null, null).ConfigureAwait(false),
            "handoff" => await HandoffAsync(store, ownerId, epoch, acquiredAtUtc).ConfigureAwait(false),
            "renew" => await RenewAsync(store, ownerId, epoch, acquiredAtUtc).ConfigureAwait(false),
            _ => "Invalid",
        };
        await File.WriteAllTextAsync(resultPath, status).ConfigureAwait(false);
        return mode is "initial" or "handoff" or "renew" ? 0 : 2;
    }

    private static async Task<string> HandoffAsync(GovernedLoopCoordinatorEvidenceStore store, string ownerId, long epoch, DateTimeOffset acquiredAtUtc)
    {
        var current = await store.ReadAsync(CoordinatorId).ConfigureAwait(false);
        if (current?.Snapshot is not { } snapshot)
        {
            return current?.Status.ToString() ?? "Unavailable";
        }

        return await AcquireAsync(
            store,
            ownerId,
            epoch,
            acquiredAtUtc,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            snapshot.Ownership.ContentHash,
            snapshot.LatestHeartbeat.ContentHash).ConfigureAwait(false);
    }

    private static async Task<string> AcquireAsync(
        GovernedLoopCoordinatorEvidenceStore store,
        string ownerId,
        long epoch,
        DateTimeOffset acquiredAtUtc,
        GovernedLoopCoordinatorPriorEvidenceExpectation expectation,
        string? expectedOwnershipHash,
        string? expectedHeartbeatHash)
    {
        var ownership = Ownership(ownerId, epoch, acquiredAtUtc);
        var lifecycle = Lifecycle(ownership, acquiredAtUtc);
        var heartbeat = Heartbeat(ownership, acquiredAtUtc);
        var result = await store.TryAcquireAsync(new GovernedLoopCoordinatorAcquisitionRequest(
            expectation,
            expectedOwnershipHash,
            expectedHeartbeatHash,
            ownership,
            lifecycle,
            heartbeat)).ConfigureAwait(false);
        return result?.Status.ToString() ?? "Unavailable";
    }

    private static async Task<string> RenewAsync(GovernedLoopCoordinatorEvidenceStore store, string ownerId, long epoch, DateTimeOffset acquiredAtUtc)
    {
        var ownership = Ownership(ownerId, epoch, acquiredAtUtc);
        var current = Heartbeat(ownership, acquiredAtUtc);
        var renewal = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(
            GovernedLoopCoordinatorHeartbeat.CurrentSchemaVersion,
            2,
            ownership,
            acquiredAtUtc.AddSeconds(1),
            acquiredAtUtc.AddMinutes(2),
            string.Empty));
        var result = await store.RenewHeartbeatAsync(new GovernedLoopCoordinatorHeartbeatMutationRequest(
            ownership,
            ownership.ContentHash,
            current.HeartbeatSequence,
            current.ContentHash,
            renewal)).ConfigureAwait(false);
        return result?.Status.ToString() ?? "Unavailable";
    }

    private static GovernedLoopCoordinatorOwnership Ownership(string ownerId, long epoch, DateTimeOffset acquiredAtUtc)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(
            GovernedLoopCoordinatorOwnership.CurrentSchemaVersion,
            CoordinatorId,
            ownerId,
            epoch,
            acquiredAtUtc,
            string.Empty));

    private static GovernedLoopCoordinatorLifecycle Lifecycle(GovernedLoopCoordinatorOwnership ownership, DateTimeOffset acquiredAtUtc)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(
            GovernedLoopCoordinatorLifecycle.CurrentSchemaVersion,
            1,
            ownership,
            GovernedLoopCoordinatorStatus.Starting,
            acquiredAtUtc,
            null,
            string.Empty));

    private static GovernedLoopCoordinatorHeartbeat Heartbeat(GovernedLoopCoordinatorOwnership ownership, DateTimeOffset acquiredAtUtc)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(
            GovernedLoopCoordinatorHeartbeat.CurrentSchemaVersion,
            1,
            ownership,
            acquiredAtUtc,
            acquiredAtUtc.AddMinutes(1),
            string.Empty));
}
