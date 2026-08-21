using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopCoordinatorContractTests
{
    [Fact]
    public void Ownership_lifecycle_heartbeat_and_every_failure_kind_are_hash_bound_and_valid()
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership();
        var lifecycle = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Starting, ownership: ownership);
        var heartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(ownership: ownership);

        Assert.True(GovernedLoopSleepContractValidator.Validate(ownership).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.Validate(lifecycle).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.Validate(heartbeat).IsValid);
        Assert.True(GovernedLoopSleepContractHash.Matches(ownership));
        Assert.True(GovernedLoopSleepContractHash.Matches(lifecycle));
        Assert.True(GovernedLoopSleepContractHash.Matches(heartbeat));
        Assert.NotSame(ownership, lifecycle.Ownership);
        Assert.NotSame(ownership, heartbeat.Ownership);

        foreach (var kind in Enum.GetValues<GovernedLoopCoordinatorFailureKind>())
        {
            var failure = GovernedLoopSleepContractTestFixture.Failure(ownership: ownership, kind: kind);
            Assert.True(GovernedLoopSleepContractValidator.Validate(failure).IsValid, kind.ToString());
            Assert.True(GovernedLoopSleepContractHash.Matches(failure));
            Assert.NotSame(ownership, failure.Ownership);
        }
    }

    [Fact]
    public void Coordinator_lifecycle_advances_through_closed_contiguous_transitions()
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership();
        var starting = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Starting, 1, ownership);
        var running = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Running, 2, ownership);
        var stopping = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Stopping, 3, ownership);
        var stopped = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Stopped, 4, ownership);
        var failed = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Failed, 3, ownership);
        var stoppedDuringStart = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Stopped, 2, ownership);

        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(starting, running).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(running, stopping).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(stopping, stopped).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(running, failed).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(starting, stoppedDuringStart).IsValid);
        Assert.True(GovernedLoopSleepStateMatrix.IsCoordinatorTransitionAllowed(GovernedLoopCoordinatorStatus.Starting, GovernedLoopCoordinatorStatus.Running));
        Assert.False(GovernedLoopSleepStateMatrix.IsCoordinatorTransitionAllowed(GovernedLoopCoordinatorStatus.Stopped, GovernedLoopCoordinatorStatus.Running));
    }

    [Fact]
    public void Coordinator_lifecycle_rejects_skips_owner_substitution_time_reversal_and_terminal_reopening()
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership();
        var starting = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Starting, 1, ownership);
        var runningGap = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Running, 3, ownership);
        var otherOwner = GovernedLoopSleepContractTestFixture.Ownership(ownerId: "process-owner-2");
        var substituted = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Running, 2, otherOwner);
        var reversed = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Running,
            2,
            ownership,
            starting.UpdatedAtUtc.AddTicks(-1));
        var stopped = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Stopped, 2, ownership);
        var reopened = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Running, 3, ownership);
        var directStopped = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Stopped, 3, ownership);

        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(starting, runningGap).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(starting, substituted).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(starting, reversed).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(stopped, reopened).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(
            GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Running, 2, ownership),
            directStopped).Errors,
            error => error.Code == GovernedLoopSleepValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Heartbeat_renews_only_contiguously_before_the_current_exclusive_lease_boundary()
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership();
        var current = GovernedLoopSleepContractTestFixture.Heartbeat(
            1,
            ownership,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddMinutes(1),
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddMinutes(2));
        var valid = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            ownership,
            current.LeaseExpiresAtUtc.AddTicks(-1),
            current.LeaseExpiresAtUtc.AddMinutes(1));
        var atBoundary = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            ownership,
            current.LeaseExpiresAtUtc,
            current.LeaseExpiresAtUtc.AddMinutes(1));
        var afterBoundary = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            ownership,
            current.LeaseExpiresAtUtc.AddTicks(1),
            current.LeaseExpiresAtUtc.AddMinutes(1));
        var shrinkingLease = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            ownership,
            current.RecordedAtUtc.AddTicks(1),
            current.LeaseExpiresAtUtc);
        var sequenceGap = GovernedLoopSleepContractTestFixture.Heartbeat(
            3,
            ownership,
            current.RecordedAtUtc.AddTicks(1),
            current.LeaseExpiresAtUtc.AddMinutes(1));

        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(current, valid).IsValid);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(current, atBoundary).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(current, afterBoundary).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(current, shrinkingLease).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IllegalTransition);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(current, sequenceGap).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion);
    }

    [Fact]
    public void Ownership_handoff_requires_one_new_owner_contiguous_epoch_and_expired_exclusive_lease()
    {
        var current = GovernedLoopSleepContractTestFixture.Ownership();
        var heartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
            ownership: current,
            recordedAtUtc: current.AcquiredAtUtc.AddMinutes(1),
            leaseExpiresAtUtc: current.AcquiredAtUtc.AddMinutes(2));
        var next = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: heartbeat.LeaseExpiresAtUtc);
        var beforeExpiry = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: heartbeat.LeaseExpiresAtUtc.AddTicks(-1));
        var repeatedOwner = GovernedLoopSleepContractTestFixture.Ownership(
            ownershipEpoch: 2,
            acquiredAtUtc: heartbeat.LeaseExpiresAtUtc);
        var epochGap = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 3,
            acquiredAtUtc: heartbeat.LeaseExpiresAtUtc);
        var otherCoordinator = GovernedLoopSleepContractTestFixture.Ownership(
            coordinatorId: "other-coordinator",
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: heartbeat.LeaseExpiresAtUtc);
        var reversedAcquisition = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: current.AcquiredAtUtc.AddTicks(-1));

        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(current, next).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateHandoff(current, heartbeat, next).IsValid);
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.IllegalTransition,
                "$.next.acquiredAtUtc"),
            SingleError(GovernedLoopSleepContractValidator.ValidateHandoff(current, heartbeat, beforeExpiry)));
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.IllegalTransition,
                "$.next.ownerId"),
            SingleError(GovernedLoopSleepContractValidator.ValidateTransition(current, repeatedOwner)));
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion,
                "$.next.ownershipEpoch"),
            SingleError(GovernedLoopSleepContractValidator.ValidateTransition(current, epochGap)));
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged,
                "$.next.coordinatorId"),
            SingleError(GovernedLoopSleepContractValidator.ValidateTransition(current, otherCoordinator)));
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.IllegalTransition,
                "$.next.acquiredAtUtc"),
            SingleError(GovernedLoopSleepContractValidator.ValidateTransition(current, reversedAcquisition)));
    }

    [Fact]
    public void Authoritative_ownership_composition_rejects_stale_owner_lifecycle_heartbeat_and_failure()
    {
        var stale = GovernedLoopSleepContractTestFixture.Ownership();
        var staleHeartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(ownership: stale);
        var authoritative = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: staleHeartbeat.LeaseExpiresAtUtc);
        var staleLifecycle = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Running,
            ownership: stale);
        var staleFailure = GovernedLoopSleepContractTestFixture.Failure(ownership: stale);
        var freshRecordedAtUtc = authoritative.AcquiredAtUtc.AddTicks(1);
        var currentLifecycle = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Starting,
            ownership: authoritative,
            updatedAtUtc: freshRecordedAtUtc);
        var currentHeartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
            ownership: authoritative,
            recordedAtUtc: freshRecordedAtUtc,
            leaseExpiresAtUtc: freshRecordedAtUtc.AddMinutes(1));
        var currentFailure = GovernedLoopSleepContractTestFixture.Failure(
            ownership: authoritative,
            occurredAtUtc: freshRecordedAtUtc);

        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(authoritative, currentLifecycle).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(authoritative, currentHeartbeat).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(authoritative, currentFailure).IsValid);
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.BindingMismatch,
                "$.lifecycle.ownership"),
            SingleError(GovernedLoopSleepContractValidator.ValidateComposition(authoritative, staleLifecycle)));
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.BindingMismatch,
                "$.heartbeat.ownership"),
            SingleError(GovernedLoopSleepContractValidator.ValidateComposition(authoritative, staleHeartbeat)));
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.BindingMismatch,
                "$.failure.ownership"),
            SingleError(GovernedLoopSleepContractValidator.ValidateComposition(authoritative, staleFailure)));
        Assert.Equal(
            new GovernedLoopSleepValidationErrorCodePath(
                GovernedLoopSleepValidationErrorCode.BindingMismatch,
                "$.currentHeartbeat.ownership"),
            SingleError(GovernedLoopSleepContractValidator.ValidateHandoff(authoritative, staleHeartbeat, GovernedLoopSleepContractTestFixture.Ownership(
                ownerId: "process-owner-3",
                ownershipEpoch: 3,
                acquiredAtUtc: authoritative.AcquiredAtUtc.AddMinutes(1)))));
    }

    [Fact]
    public void Heartbeat_and_failure_transitions_reject_owner_substitution_and_time_reversal()
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership();
        var otherOwner = GovernedLoopSleepContractTestFixture.Ownership(ownerId: "process-owner-2");
        var heartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(1, ownership);
        var substitutedHeartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            otherOwner,
            heartbeat.RecordedAtUtc.AddTicks(1),
            heartbeat.LeaseExpiresAtUtc.AddMinutes(1));
        var failure = GovernedLoopSleepContractTestFixture.Failure(1, ownership);
        var nextFailure = GovernedLoopSleepContractTestFixture.Failure(
            2,
            ownership,
            occurredAtUtc: failure.OccurredAtUtc.AddTicks(1));
        var gap = GovernedLoopSleepContractTestFixture.Failure(
            3,
            ownership,
            occurredAtUtc: failure.OccurredAtUtc.AddTicks(1));
        var substitutedFailure = GovernedLoopSleepContractTestFixture.Failure(
            2,
            otherOwner,
            occurredAtUtc: failure.OccurredAtUtc.AddTicks(1));
        var reversedFailure = GovernedLoopSleepContractTestFixture.Failure(
            2,
            ownership,
            occurredAtUtc: failure.OccurredAtUtc.AddTicks(-1));

        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(heartbeat, substitutedHeartbeat).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged);
        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(failure, nextFailure).IsValid);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(failure, gap).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(failure, substitutedFailure).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(failure, reversedFailure).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Malformed_nested_ownership_returns_bounded_errors_instead_of_throwing()
    {
        var hash = GovernedLoopSleepContractTestFixture.Hash('f');
        var lifecycle = new GovernedLoopCoordinatorLifecycle(
            1,
            1,
            null!,
            GovernedLoopCoordinatorStatus.Starting,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            null,
            hash);
        var heartbeat = new GovernedLoopCoordinatorHeartbeat(
            1,
            1,
            null!,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddMinutes(1),
            hash);
        var failure = new GovernedLoopCoordinatorFailure(
            1,
            1,
            null!,
            GovernedLoopCoordinatorFailureKind.CorruptState,
            "corrupt-state-1",
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            hash);

        Assert.Contains(GovernedLoopSleepContractValidator.Validate(lifecycle).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.Required);
        Assert.Contains(GovernedLoopSleepContractValidator.Validate(heartbeat).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.Required);
        Assert.Contains(GovernedLoopSleepContractValidator.Validate(failure).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.Required);
    }

    [Fact]
    public void Malformed_coordinator_shapes_bounds_timestamps_and_hashes_fail_closed()
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership();
        var invalidOwnership = GovernedLoopSleepContractHash.Apply(ownership with { OwnershipEpoch = GovernedLoopSleepContractLimits.MaxVersion + 1 });
        var terminalWithoutTime = GovernedLoopSleepContractHash.Apply(
            GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Stopped) with { TerminalAtUtc = null });
        var activeWithTerminal = GovernedLoopSleepContractHash.Apply(
            GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Running) with { TerminalAtUtc = GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddMinutes(1) });
        var unsupportedStatus = GovernedLoopSleepContractHash.Apply(
            GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Running) with { Status = (GovernedLoopCoordinatorStatus)99 });
        var expiredHeartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
            leaseExpiresAtUtc: GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddMinutes(1),
            recordedAtUtc: GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddMinutes(1));
        var preOwnershipFailure = GovernedLoopSleepContractTestFixture.Failure(
            occurredAtUtc: ownership.AcquiredAtUtc.AddTicks(-1));
        var unsupportedFailure = GovernedLoopSleepContractHash.Apply(
            GovernedLoopSleepContractTestFixture.Failure() with { Kind = (GovernedLoopCoordinatorFailureKind)99 });
        var tampered = ownership with { OwnerId = "process-owner-2" };

        Assert.Contains(GovernedLoopSleepContractValidator.Validate(invalidOwnership).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.LimitExceeded);
        Assert.False(GovernedLoopSleepContractValidator.Validate(terminalWithoutTime).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(activeWithTerminal).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(unsupportedStatus).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(expiredHeartbeat).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(preOwnershipFailure).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(unsupportedFailure).IsValid);
        Assert.Contains(GovernedLoopSleepContractValidator.Validate(tampered).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IntegrityMismatch);
    }

    [Fact]
    public void Coordinator_hash_APIs_reject_null_and_tampering()
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership();
        var lifecycle = GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Starting);
        var heartbeat = GovernedLoopSleepContractTestFixture.Heartbeat();
        var failure = GovernedLoopSleepContractTestFixture.Failure();

        Assert.False(GovernedLoopSleepContractHash.Matches(ownership with { ContentHash = GovernedLoopSleepContractTestFixture.Hash('0') }));
        Assert.False(GovernedLoopSleepContractHash.Matches(lifecycle with { ContentHash = GovernedLoopSleepContractTestFixture.Hash('0') }));
        Assert.False(GovernedLoopSleepContractHash.Matches(heartbeat with { ContentHash = GovernedLoopSleepContractTestFixture.Hash('0') }));
        Assert.False(GovernedLoopSleepContractHash.Matches(failure with { ContentHash = GovernedLoopSleepContractTestFixture.Hash('0') }));
        Assert.False(GovernedLoopSleepContractHash.Matches((GovernedLoopCoordinatorOwnership?)null));
        Assert.False(GovernedLoopSleepContractHash.Matches((GovernedLoopCoordinatorLifecycle?)null));
        Assert.False(GovernedLoopSleepContractHash.Matches((GovernedLoopCoordinatorHeartbeat?)null));
        Assert.False(GovernedLoopSleepContractHash.Matches((GovernedLoopCoordinatorFailure?)null));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.Apply((GovernedLoopCoordinatorOwnership)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.Apply((GovernedLoopCoordinatorLifecycle)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.Apply((GovernedLoopCoordinatorHeartbeat)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.Apply((GovernedLoopCoordinatorFailure)null!));
        Assert.False(GovernedLoopSleepContractValidator.Validate((GovernedLoopCoordinatorOwnership?)null).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate((GovernedLoopCoordinatorLifecycle?)null).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate((GovernedLoopCoordinatorHeartbeat?)null).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate((GovernedLoopCoordinatorFailure?)null).IsValid);
    }

    [Fact]
    public void Very_over_limit_coordinator_fields_are_rejected_before_integrity_recomputation()
    {
        var massiveValue = new string('a', 1_000_000);
        var ownership = new GovernedLoopCoordinatorOwnership(
            1,
            "background-coordinator",
            massiveValue,
            1,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            GovernedLoopSleepContractTestFixture.Hash('a'));
        var failure = new GovernedLoopCoordinatorFailure(
            1,
            1,
            GovernedLoopSleepContractTestFixture.Ownership(),
            GovernedLoopCoordinatorFailureKind.Unexpected,
            massiveValue,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddMinutes(1),
            GovernedLoopSleepContractTestFixture.Hash('b'));

        var ownershipValidation = GovernedLoopSleepContractValidator.Validate(ownership);
        var failureValidation = GovernedLoopSleepContractValidator.Validate(failure);
        var ownershipException = Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopSleepContractHash.Compute(ownership));
        var failureException = Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopSleepContractHash.Compute(failure));

        Assert.Contains(ownershipValidation.Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidIdentity);
        Assert.DoesNotContain(ownershipValidation.Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IntegrityMismatch);
        Assert.Contains(failureValidation.Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidIdentity);
        Assert.DoesNotContain(failureValidation.Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IntegrityMismatch);
        Assert.Equal("ownership", ownershipException.ParamName);
        Assert.Equal("failure", failureException.ParamName);
    }

    private static GovernedLoopSleepValidationErrorCodePath SingleError(GovernedLoopSleepValidationResult result)
    {
        var error = Assert.Single(result.Errors);
        return new GovernedLoopSleepValidationErrorCodePath(error.Code, error.Path);
    }

    private sealed record GovernedLoopSleepValidationErrorCodePath(
        GovernedLoopSleepValidationErrorCode Code,
        string Path);
}
