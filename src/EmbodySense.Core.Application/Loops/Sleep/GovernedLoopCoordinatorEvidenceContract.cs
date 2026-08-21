using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Validates bounded coordinator-port requests and result shapes before durable use.</summary>
public static class GovernedLoopCoordinatorEvidenceContract
{
    /// <summary>Gets whether one coordinator read identity is a bounded canonical token.</summary>
    public static bool IsValidCoordinatorId(string? coordinatorId)
        => CustomLoopArtifactIdentifier.IsValid(coordinatorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters);

    /// <summary>Gets whether one current evidence snapshot is complete, bounded, and consistently bound.</summary>
    public static bool IsValid(GovernedLoopCoordinatorSnapshot? snapshot)
    {
        if (snapshot is null
            || !GovernedLoopSleepContractValidator.Validate(snapshot.Ownership).IsValid
            || !GovernedLoopSleepContractValidator.ValidateComposition(snapshot.Ownership, snapshot.LatestLifecycle).IsValid
            || !GovernedLoopSleepContractValidator.ValidateComposition(snapshot.Ownership, snapshot.LatestHeartbeat).IsValid)
        {
            return false;
        }

        return snapshot.LatestFailureSequence == 0
            ? snapshot.LatestFailureHash is null
            : snapshot.LatestFailureSequence <= GovernedLoopSleepContractLimits.MaxVersion
                && IsCanonicalHash(snapshot.LatestFailureHash);
    }

    /// <summary>Gets whether one read result has an exact closed status and payload shape.</summary>
    public static bool IsValid(GovernedLoopCoordinatorReadResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && (result.Status == GovernedLoopCoordinatorReadStatus.Found
                ? IsValid(result.Snapshot)
                : result.Snapshot is null);

    /// <summary>Gets whether one acquisition request carries exact atomic initial evidence and prior-evidence expectations.</summary>
    public static bool IsValid(GovernedLoopCoordinatorAcquisitionRequest? request)
    {
        if (request is null
            || !Enum.IsDefined(request.PriorEvidenceExpectation)
            || !GovernedLoopSleepContractValidator.Validate(request.ProposedOwnership).IsValid
            || !GovernedLoopSleepContractValidator.ValidateComposition(request.ProposedOwnership, request.StartingLifecycle).IsValid
            || !GovernedLoopSleepContractValidator.ValidateComposition(request.ProposedOwnership, request.InitialHeartbeat).IsValid
            || request.StartingLifecycle.Status != GovernedLoopCoordinatorStatus.Starting
            || request.StartingLifecycle.LifecycleVersion != 1
            || request.InitialHeartbeat.HeartbeatSequence != 1)
        {
            return false;
        }

        return request.PriorEvidenceExpectation switch
        {
            GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound => request.ExpectedOwnershipHash is null
                && request.ExpectedHeartbeatHash is null
                && request.ProposedOwnership.OwnershipEpoch == 1,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing => IsCanonicalHash(request.ExpectedOwnershipHash)
                && IsCanonicalHash(request.ExpectedHeartbeatHash)
                && request.ProposedOwnership.OwnershipEpoch > 1,
            _ => false
        };
    }

    /// <summary>Gets whether one acquisition result has an exact closed status and payload shape.</summary>
    public static bool IsValid(GovernedLoopCoordinatorAcquisitionResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && IsMutationSnapshotShapeValid(result.Status is not GovernedLoopCoordinatorAcquisitionStatus.Corrupt
                and not GovernedLoopCoordinatorAcquisitionStatus.Unavailable, result.Snapshot);

    /// <summary>Gets whether one heartbeat request is an exact fenced contiguous successor.</summary>
    public static bool IsValid(GovernedLoopCoordinatorHeartbeatMutationRequest? request)
        => request is not null
            && IsExpectedOwnershipValid(request.ExpectedOwnership, request.ExpectedOwnershipHash)
            && request.ExpectedHeartbeatSequence is > 0 and < GovernedLoopSleepContractLimits.MaxVersion
            && IsCanonicalHash(request.ExpectedHeartbeatHash)
            && GovernedLoopSleepContractValidator.ValidateComposition(request.ExpectedOwnership, request.ProposedHeartbeat).IsValid
            && request.ProposedHeartbeat.HeartbeatSequence == request.ExpectedHeartbeatSequence + 1;

    /// <summary>Gets whether one heartbeat result has an exact closed status and payload shape.</summary>
    public static bool IsValid(GovernedLoopCoordinatorHeartbeatMutationResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && IsMutationSnapshotShapeValid(result.Status is not GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt
                and not GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable, result.Snapshot);

    /// <summary>Gets whether one lifecycle request is an exact fenced contiguous successor.</summary>
    public static bool IsValid(GovernedLoopCoordinatorLifecycleMutationRequest? request)
        => request is not null
            && IsExpectedOwnershipValid(request.ExpectedOwnership, request.ExpectedOwnershipHash)
            && request.ExpectedLifecycleVersion is > 0 and < GovernedLoopSleepContractLimits.MaxVersion
            && IsCanonicalHash(request.ExpectedLifecycleHash)
            && GovernedLoopSleepContractValidator.ValidateComposition(request.ExpectedOwnership, request.ProposedLifecycle).IsValid
            && request.ProposedLifecycle.LifecycleVersion == request.ExpectedLifecycleVersion + 1;

    /// <summary>Gets whether one lifecycle result has an exact closed status and payload shape.</summary>
    public static bool IsValid(GovernedLoopCoordinatorLifecycleMutationResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && IsMutationSnapshotShapeValid(result.Status is not GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt
                and not GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable, result.Snapshot);

    /// <summary>Gets whether one failure request is an exact fenced contiguous successor.</summary>
    public static bool IsValid(GovernedLoopCoordinatorFailureMutationRequest? request)
    {
        if (request is null
            || !Enum.IsDefined(request.PriorFailureExpectation)
            || !IsExpectedOwnershipValid(request.ExpectedOwnership, request.ExpectedOwnershipHash)
            || !GovernedLoopSleepContractValidator.ValidateComposition(request.ExpectedOwnership, request.ProposedFailure).IsValid)
        {
            return false;
        }

        return request.PriorFailureExpectation switch
        {
            GovernedLoopCoordinatorPriorFailureExpectation.None => request.ExpectedFailureSequence == 0
                && request.ExpectedFailureHash is null
                && request.ProposedFailure.FailureSequence == 1,
            GovernedLoopCoordinatorPriorFailureExpectation.Existing => request.ExpectedFailureSequence is > 0 and < GovernedLoopSleepContractLimits.MaxVersion
                && IsCanonicalHash(request.ExpectedFailureHash)
                && request.ProposedFailure.FailureSequence == request.ExpectedFailureSequence + 1,
            _ => false
        };
    }

    /// <summary>Gets whether one failure result has an exact closed status and payload shape.</summary>
    public static bool IsValid(GovernedLoopCoordinatorFailureMutationResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && IsMutationSnapshotShapeValid(result.Status is not GovernedLoopCoordinatorFailureMutationStatus.Corrupt
                and not GovernedLoopCoordinatorFailureMutationStatus.Unavailable, result.Snapshot);

    private static bool IsExpectedOwnershipValid(GovernedLoopCoordinatorOwnership ownership, string? expectedHash)
        => GovernedLoopSleepContractValidator.Validate(ownership).IsValid
            && IsCanonicalHash(expectedHash)
            && string.Equals(ownership.ContentHash, expectedHash, StringComparison.Ordinal);

    private static bool IsMutationSnapshotShapeValid(bool requiresSnapshot, GovernedLoopCoordinatorSnapshot? snapshot)
        => requiresSnapshot ? IsValid(snapshot) : snapshot is null;

    private static bool IsCanonicalHash(string? value)
        => value is { Length: GovernedLoopSleepContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
