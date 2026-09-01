using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

public sealed class GovernedLoopCoordinatorEvidenceContractTests
{
    private static readonly DateTimeOffset _observedAtUtc = DateTimeOffset.UnixEpoch.AddHours(4);

    [Fact]
    public void Public_port_exposes_only_fenced_read_acquire_and_append_operations()
    {
        var methods = typeof(IGovernedLoopCoordinatorEvidencePort).GetMethods().OrderBy(item => item.Name).ToArray();

        Assert.Equal(
            ["AppendFailureAsync", "AppendLifecycleAsync", "ReadAsync", "RenewHeartbeatAsync", "TryAcquireAsync"],
            methods.Select(item => item.Name));
        Assert.DoesNotContain(methods, item => item.Name.Contains("Delete", StringComparison.Ordinal) || item.Name.Contains("Release", StringComparison.Ordinal));
        Assert.Equal(typeof(string), methods.Single(item => item.Name == "ReadAsync").GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Closed_statuses_are_exact_and_include_duplicate_outcomes()
    {
        Assert.Equal(["Found", "NotFound", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopCoordinatorReadStatus>());
        Assert.Equal(["Acquired", "Duplicate", "OwnedByLivePeer", "LeaseNotExpired", "Conflict", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopCoordinatorAcquisitionStatus>());
        Assert.Equal(["Renewed", "Duplicate", "OwnershipLost", "Conflict", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopCoordinatorHeartbeatMutationStatus>());
        Assert.Equal(["Appended", "Duplicate", "OwnershipLost", "Conflict", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopCoordinatorLifecycleMutationStatus>());
        Assert.Equal(["Appended", "Duplicate", "OwnershipLost", "Conflict", "Corrupt", "Unavailable"], Enum.GetNames<GovernedLoopCoordinatorFailureMutationStatus>());
        Assert.Equal(["NotFound", "Existing", "TerminalSameOwner"], Enum.GetNames<GovernedLoopCoordinatorPriorEvidenceExpectation>());
        Assert.Equal(["None", "Existing"], Enum.GetNames<GovernedLoopCoordinatorPriorFailureExpectation>());
    }

    [Fact]
    public void Valid_contracts_cover_initial_acquisition_and_all_contiguous_compare_and_swaps()
    {
        var ownership = Ownership();
        var lifecycle = Lifecycle(ownership, 1, GovernedLoopCoordinatorStatus.Starting);
        var heartbeat = Heartbeat(ownership, 1);
        var snapshot = new GovernedLoopCoordinatorSnapshot(ownership, lifecycle, heartbeat, 0, null);
        var acquisition = new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound,
            null,
            null,
            ownership,
            lifecycle,
            heartbeat);
        var nextHeartbeat = Heartbeat(ownership, 2, _observedAtUtc.AddMinutes(2));
        var nextLifecycle = Lifecycle(ownership, 2, GovernedLoopCoordinatorStatus.Running, _observedAtUtc.AddMinutes(2));
        var failure = Failure(ownership, 1);

        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValidCoordinatorId("background-coordinator"));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(snapshot));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.Found, snapshot)));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(acquisition));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Acquired, snapshot)));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorHeartbeatMutationRequest(
            ownership,
            ownership.ContentHash,
            heartbeat.HeartbeatSequence,
            heartbeat.ContentHash,
            nextHeartbeat)));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorLifecycleMutationRequest(
            ownership,
            ownership.ContentHash,
            lifecycle.LifecycleVersion,
            lifecycle.ContentHash,
            nextLifecycle)));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorFailureMutationRequest(
            ownership,
            ownership.ContentHash,
            GovernedLoopCoordinatorPriorFailureExpectation.None,
            0,
            null,
            failure)));
    }

    [Fact]
    public void Acquisition_requires_atomic_starting_evidence_and_explicit_prior_posture()
    {
        var initial = Ownership();
        var successor = Ownership(epoch: 2, ownerId: "owner-2");
        var validHandoff = new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            initial.ContentHash,
            Heartbeat(initial, 3).ContentHash,
            successor,
            Lifecycle(successor, 1, GovernedLoopCoordinatorStatus.Starting),
            Heartbeat(successor, 1));

        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(validHandoff));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(validHandoff with { ExpectedHeartbeatHash = null }));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(validHandoff with { PriorEvidenceExpectation = (GovernedLoopCoordinatorPriorEvidenceExpectation)99 }));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorAcquisitionRequest(
            validHandoff.PriorEvidenceExpectation,
            validHandoff.ExpectedOwnershipHash,
            validHandoff.ExpectedHeartbeatHash,
            successor,
            Lifecycle(successor, 1, GovernedLoopCoordinatorStatus.Running),
            validHandoff.InitialHeartbeat)));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorAcquisitionRequest(
            validHandoff.PriorEvidenceExpectation,
            validHandoff.ExpectedOwnershipHash,
            validHandoff.ExpectedHeartbeatHash,
            successor,
            validHandoff.StartingLifecycle,
            Heartbeat(successor, 2))));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorAcquisitionRequest(
            validHandoff.PriorEvidenceExpectation,
            validHandoff.ExpectedOwnershipHash,
            validHandoff.ExpectedHeartbeatHash,
            initial,
            validHandoff.StartingLifecycle,
            validHandoff.InitialHeartbeat)));
    }

    [Fact]
    public void Repair_acquisition_cannot_precede_the_authorizing_repair_disposition()
    {
        var failed = Ownership();
        var failedHeartbeat = Heartbeat(failed, 1);
        var recordedAtUtc = failedHeartbeat.LeaseExpiresAtUtc.AddMinutes(1);
        var readiness = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairReadiness(
            GovernedLoopCoordinatorRepairReadiness.CurrentSchemaVersion,
            "workspace-sha256:" + new string('a', 64),
            failed.CoordinatorId,
            true,
            true,
            true,
            true,
            true,
            recordedAtUtc,
            string.Empty));
        var repair = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairDisposition(
            GovernedLoopCoordinatorRepairDisposition.CurrentSchemaVersion,
            readiness.WorkspaceId,
            failed.CoordinatorId,
            "repair-operation",
            "operator-1",
            failed,
            new string('a', 64),
            failedHeartbeat.ContentHash,
            new string('b', 64),
            readiness,
            recordedAtUtc,
            string.Empty));
        var successor = Ownership(epoch: 2, ownerId: "owner-2", acquiredAtUtc: recordedAtUtc.AddTicks(-1));
        var acquisition = new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            failed.ContentHash,
            failedHeartbeat.ContentHash,
            successor,
            Lifecycle(successor, 1, GovernedLoopCoordinatorStatus.Starting, successor.AcquiredAtUtc),
            Heartbeat(successor, 1, successor.AcquiredAtUtc));

        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorRepairAcquisitionRequest(repair, acquisition)));

        var causalSuccessor = Ownership(epoch: 2, ownerId: "owner-2", acquiredAtUtc: recordedAtUtc);
        var causalAcquisition = new GovernedLoopCoordinatorAcquisitionRequest(
            acquisition.PriorEvidenceExpectation,
            acquisition.ExpectedOwnershipHash,
            acquisition.ExpectedHeartbeatHash,
            causalSuccessor,
            Lifecycle(causalSuccessor, 1, GovernedLoopCoordinatorStatus.Starting, causalSuccessor.AcquiredAtUtc),
            Heartbeat(causalSuccessor, 1, causalSuccessor.AcquiredAtUtc));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorRepairAcquisitionRequest(repair, causalAcquisition)));
    }

    [Fact]
    public void Terminal_same_owner_restart_requires_a_durable_stopped_predecessor_without_weakening_handoff_validation()
    {
        var initial = Ownership();
        var terminal = Lifecycle(initial, 2, GovernedLoopCoordinatorStatus.Stopped, _observedAtUtc.AddMinutes(1));
        var heartbeat = Heartbeat(initial, 2, _observedAtUtc.AddMinutes(1));
        var restartedOwner = Ownership(epoch: 2, ownerId: initial.OwnerId, acquiredAtUtc: heartbeat.LeaseExpiresAtUtc.AddTicks(1));
        var restart = new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.TerminalSameOwner,
            initial.ContentHash,
            heartbeat.ContentHash,
            restartedOwner,
            Lifecycle(restartedOwner, 1, GovernedLoopCoordinatorStatus.Starting, restartedOwner.AcquiredAtUtc),
            Heartbeat(restartedOwner, 1, restartedOwner.AcquiredAtUtc));

        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(restart));
        Assert.True(GovernedLoopSleepContractValidator.ValidateTerminalSameOwnerRestart(initial, terminal, heartbeat, restartedOwner).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.ValidateHandoff(initial, heartbeat, restartedOwner).IsValid);
        var foreignOwner = Ownership(epoch: 2, ownerId: "foreign-owner", acquiredAtUtc: restartedOwner.AcquiredAtUtc);
        Assert.False(GovernedLoopSleepContractValidator.ValidateTerminalSameOwnerRestart(initial, terminal, heartbeat, foreignOwner).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.ValidateTerminalSameOwnerRestart(
            initial,
            Lifecycle(initial, 2, GovernedLoopCoordinatorStatus.Failed, _observedAtUtc.AddMinutes(1)),
            heartbeat,
            restartedOwner).IsValid);
    }

    [Fact]
    public void Malformed_snapshots_requests_and_result_shapes_fail_closed()
    {
        var ownership = Ownership();
        var foreign = Ownership(ownerId: "foreign-owner");
        var lifecycle = Lifecycle(ownership, 1, GovernedLoopCoordinatorStatus.Starting);
        var heartbeat = Heartbeat(ownership, 1);
        var snapshot = new GovernedLoopCoordinatorSnapshot(ownership, lifecycle, heartbeat, 0, null);

        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValidCoordinatorId("NOT CANONICAL"));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid((GovernedLoopCoordinatorSnapshot?)null));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorSnapshot(
            ownership,
            lifecycle,
            Heartbeat(foreign, 1),
            0,
            null)));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(snapshot with { LatestFailureSequence = 1, LatestFailureHash = null }));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.NotFound, snapshot)));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorReadResult((GovernedLoopCoordinatorReadStatus)99)));

        var heartbeatMutation = new GovernedLoopCoordinatorHeartbeatMutationRequest(
            ownership,
            ownership.ContentHash,
            1,
            heartbeat.ContentHash,
            Heartbeat(ownership, 2));
        var lifecycleMutation = new GovernedLoopCoordinatorLifecycleMutationRequest(
            ownership,
            ownership.ContentHash,
            1,
            lifecycle.ContentHash,
            Lifecycle(ownership, 2, GovernedLoopCoordinatorStatus.Running));
        var failureMutation = new GovernedLoopCoordinatorFailureMutationRequest(
            ownership,
            ownership.ContentHash,
            GovernedLoopCoordinatorPriorFailureExpectation.None,
            0,
            null,
            Failure(ownership, 1));

        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(heartbeatMutation with { ExpectedOwnershipHash = new string('0', 64) }));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(heartbeatMutation with { ExpectedHeartbeatSequence = 2 }));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(lifecycleMutation with { ExpectedLifecycleHash = "bad" }));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorLifecycleMutationRequest(
            lifecycleMutation.ExpectedOwnership,
            lifecycleMutation.ExpectedOwnershipHash,
            lifecycleMutation.ExpectedLifecycleVersion,
            lifecycleMutation.ExpectedLifecycleHash,
            Lifecycle(ownership, 3, GovernedLoopCoordinatorStatus.Running))));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(failureMutation with { ExpectedFailureSequence = 1 }));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(failureMutation with { PriorFailureExpectation = (GovernedLoopCoordinatorPriorFailureExpectation)99 }));
    }

    [Fact]
    public void Duplicate_and_conflict_results_require_valid_detached_current_evidence()
    {
        var ownership = Ownership();
        var snapshot = new GovernedLoopCoordinatorSnapshot(
            ownership,
            Lifecycle(ownership, 1, GovernedLoopCoordinatorStatus.Starting),
            Heartbeat(ownership, 1),
            0,
            null);

        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, snapshot)));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Duplicate, snapshot)));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Duplicate, snapshot)));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Duplicate, snapshot)));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Conflict)));
        Assert.False(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt, snapshot)));
        Assert.True(GovernedLoopCoordinatorEvidenceContract.IsValid(new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Unavailable)));
    }

    [Fact]
    public void Models_detach_nested_evidence_from_caller_instances()
    {
        var ownership = Ownership();
        var lifecycle = Lifecycle(ownership, 1, GovernedLoopCoordinatorStatus.Starting);
        var heartbeat = Heartbeat(ownership, 1);
        var snapshot = new GovernedLoopCoordinatorSnapshot(ownership, lifecycle, heartbeat, 0, null);
        var request = new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound,
            null,
            null,
            ownership,
            lifecycle,
            heartbeat);
        var result = new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.Found, snapshot);

        Assert.NotSame(ownership, snapshot.Ownership);
        Assert.NotSame(lifecycle, snapshot.LatestLifecycle);
        Assert.NotSame(heartbeat, snapshot.LatestHeartbeat);
        Assert.NotSame(ownership, request.ProposedOwnership);
        Assert.NotSame(snapshot, result.Snapshot);
        Assert.NotSame(snapshot.Ownership, result.Snapshot!.Ownership);
    }

    private static GovernedLoopCoordinatorOwnership Ownership(
        long epoch = 1,
        string ownerId = "owner-1",
        DateTimeOffset? acquiredAtUtc = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(
            GovernedLoopCoordinatorOwnership.CurrentSchemaVersion,
            "background-coordinator",
            ownerId,
            epoch,
            acquiredAtUtc ?? _observedAtUtc,
            string.Empty));

    private static GovernedLoopCoordinatorLifecycle Lifecycle(
        GovernedLoopCoordinatorOwnership ownership,
        long version,
        GovernedLoopCoordinatorStatus status,
        DateTimeOffset? updatedAtUtc = null)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(
            GovernedLoopCoordinatorLifecycle.CurrentSchemaVersion,
            version,
            ownership,
            status,
            updatedAtUtc ?? _observedAtUtc,
            status is GovernedLoopCoordinatorStatus.Stopped or GovernedLoopCoordinatorStatus.Failed ? updatedAtUtc ?? _observedAtUtc : null,
            string.Empty));

    private static GovernedLoopCoordinatorHeartbeat Heartbeat(
        GovernedLoopCoordinatorOwnership ownership,
        long sequence,
        DateTimeOffset? recordedAtUtc = null)
    {
        var recorded = recordedAtUtc ?? _observedAtUtc.AddMinutes(sequence - 1);
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(
            GovernedLoopCoordinatorHeartbeat.CurrentSchemaVersion,
            sequence,
            ownership,
            recorded,
            recorded.AddMinutes(5),
            string.Empty));
    }

    private static GovernedLoopCoordinatorFailure Failure(GovernedLoopCoordinatorOwnership ownership, long sequence)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorFailure(
            GovernedLoopCoordinatorFailure.CurrentSchemaVersion,
            sequence,
            ownership,
            GovernedLoopCoordinatorFailureKind.Unexpected,
            "evidence-failure",
            _observedAtUtc.AddMinutes(sequence),
            string.Empty));
}
