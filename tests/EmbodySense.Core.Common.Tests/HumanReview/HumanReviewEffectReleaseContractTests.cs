using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Tests.Loops.Execution.Effects;

namespace EmbodySense.Core.Common.Tests.HumanReview;

public sealed class HumanReviewEffectReleaseContractTests
{
    [Fact]
    public void Exact_prepared_attempt_creates_restart_stable_value_free_snapshot()
    {
        var attempt = GovernedLoopEffectAttemptContractTests.Prepare();
        var snapshot = CreateSnapshot(attempt);

        Assert.Null(HumanReviewEffectReleaseContract.Validate(snapshot));
        Assert.Equal(HumanReviewEffectCertainty.NotStarted, snapshot.Certainty);
        Assert.Equal(attempt.Payload.EffectId, snapshot.Identity.EffectId);
        Assert.Equal(attempt.Payload.OperationId, snapshot.Identity.OperationId);
        Assert.Equal(attempt.Payload.EffectGeneration, snapshot.Identity.EffectGeneration);
        Assert.Equal(attempt.Binding.ExecutionGeneration, snapshot.Identity.ExecutionGeneration);
        Assert.Equal(attempt.InputFingerprint, snapshot.Preparation.InputFingerprint);
        Assert.True(HumanReviewEffectReleaseContract.TryCapture(snapshot, out var captured, out var reason), reason);
        Assert.Equal(snapshot, captured);
        Assert.NotSame(snapshot.Identity, captured!.Identity);
        Assert.NotSame(snapshot.Preparation, captured.Preparation);
    }

    [Fact]
    public void Preparation_identity_and_authority_drift_are_distinguishable_and_fail_closed()
    {
        var prepared = CreateSnapshot(GovernedLoopEffectAttemptContractTests.Prepare());
        var authorizedAttempt = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(GovernedLoopEffectAttemptContractTests.Prepare(), Hash('8'), HumanReviewTestData.CreatedAtUtc.AddMinutes(1));
        var authorized = CreateSnapshot(authorizedAttempt);
        var changedPreparation = prepared with { Preparation = prepared.Preparation with { ReviewPayloadHash = Hash('9'), PreparationHash = string.Empty } };
        changedPreparation = changedPreparation with { Preparation = changedPreparation.Preparation with { PreparationHash = HumanReviewEffectReleaseContract.ComputePreparation(changedPreparation.Preparation) } };
        changedPreparation = changedPreparation with { SnapshotHash = HumanReviewEffectReleaseContract.ComputeSnapshot(changedPreparation) };

        Assert.Equal(HumanReviewEffectSnapshotReplayDisposition.DivergentReuse, HumanReviewEffectReleaseContract.ClassifyReplay(prepared, changedPreparation));
        Assert.Null(HumanReviewEffectReleaseContract.Validate(changedPreparation));
        Assert.Equal(HumanReviewEffectSnapshotReplayDisposition.DivergentReuse, HumanReviewEffectReleaseContract.ClassifyReplay(prepared, authorized));
        Assert.NotEqual(prepared.DispatchAuthorityEvidenceHash, authorized.DispatchAuthorityEvidenceHash);
    }

    [Fact]
    public void Dispatch_conclusive_ambiguous_and_terminal_postures_never_project_as_not_started()
    {
        var prepared = GovernedLoopEffectAttemptContractTests.Prepare();
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), prepared.Payload.UpdatedAtUtc.AddSeconds(1));
        var dispatched = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, prepared.Payload.UpdatedAtUtc.AddSeconds(2));
        var conclusive = GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-one", "after-one", prepared.Payload.UpdatedAtUtc.AddSeconds(3));
        var terminal = GovernedLoopEffectAttemptContract.Advance(conclusive, GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-one", "after-one", prepared.Payload.UpdatedAtUtc.AddSeconds(4));
        var ambiguous = GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Conflicting, null, null, prepared.Payload.UpdatedAtUtc.AddSeconds(3));

        Assert.Equal(HumanReviewEffectCertainty.Dispatched, CreateSnapshot(dispatched).Certainty);
        Assert.Equal(HumanReviewEffectCertainty.Conclusive, CreateSnapshot(conclusive).Certainty);
        Assert.Equal(HumanReviewEffectCertainty.Terminal, CreateSnapshot(terminal).Certainty);
        Assert.Equal(HumanReviewEffectCertainty.Ambiguous, CreateSnapshot(ambiguous).Certainty);
    }

    [Fact]
    public void Forward_version_unknown_posture_and_invalid_coordinate_fail_closed()
    {
        var snapshot = CreateSnapshot(GovernedLoopEffectAttemptContractTests.Prepare());
        var forward = snapshot with { SchemaVersion = 2 };
        forward = forward with { SnapshotHash = HumanReviewEffectReleaseContract.ComputeSnapshot(forward) };
        var unknown = snapshot with { Certainty = HumanReviewEffectCertainty.Unknown };
        unknown = unknown with { SnapshotHash = HumanReviewEffectReleaseContract.ComputeSnapshot(unknown) };
        var invalidCoordinate = snapshot with { Identity = snapshot.Identity with { ActivationOrdinal = null, VisitOrdinal = null } };
        invalidCoordinate = invalidCoordinate with { SnapshotHash = HumanReviewEffectReleaseContract.ComputeSnapshot(invalidCoordinate) };
        var invalidVisitIdentity = snapshot.Identity with { ActivationOrdinal = null, VisitOrdinal = 0, IdentityHash = string.Empty };
        invalidVisitIdentity = invalidVisitIdentity with { IdentityHash = HumanReviewEffectReleaseContract.ComputeIdentity(invalidVisitIdentity) };
        var invalidVisit = snapshot with { Identity = invalidVisitIdentity, SnapshotHash = string.Empty };
        invalidVisit = invalidVisit with { SnapshotHash = HumanReviewEffectReleaseContract.ComputeSnapshot(invalidVisit) };

        Assert.Equal("effect-certainty-snapshot-invalid", HumanReviewEffectReleaseContract.Validate(forward));
        Assert.Equal("effect-certainty-snapshot-posture-invalid", HumanReviewEffectReleaseContract.Validate(unknown));
        Assert.Equal("effect-certainty-snapshot-binding-invalid", HumanReviewEffectReleaseContract.Validate(invalidCoordinate));
        Assert.Equal("effect-certainty-snapshot-binding-invalid", HumanReviewEffectReleaseContract.Validate(invalidVisit));
        Assert.False(HumanReviewEffectReleaseContract.TryCapture(invalidVisit, out _, out var invalidVisitReason));
        Assert.Equal("effect-certainty-snapshot-binding-invalid", invalidVisitReason);
    }

    [Fact]
    public void Replay_classification_rejects_null_and_malformed_snapshots_before_exact_replay()
    {
        var snapshot = CreateSnapshot(GovernedLoopEffectAttemptContractTests.Prepare());
        var forward = snapshot with { SchemaVersion = 2 };
        forward = forward with { SnapshotHash = HumanReviewEffectReleaseContract.ComputeSnapshot(forward) };
        var badNestedHash = snapshot with { Identity = snapshot.Identity with { IdentityHash = Hash('f') } };
        badNestedHash = badNestedHash with { SnapshotHash = HumanReviewEffectReleaseContract.ComputeSnapshot(badNestedHash) };
        var badSnapshotHash = snapshot with { SnapshotHash = Hash('f') };

        Assert.Equal(HumanReviewEffectSnapshotReplayDisposition.ExactReplay, HumanReviewEffectReleaseContract.ClassifyReplay(snapshot, snapshot));
        Assert.Equal(HumanReviewEffectSnapshotReplayDisposition.Invalid, HumanReviewEffectReleaseContract.ClassifyReplay(null, snapshot));
        Assert.Equal(HumanReviewEffectSnapshotReplayDisposition.Invalid, HumanReviewEffectReleaseContract.ClassifyReplay(snapshot, null));
        Assert.Equal(HumanReviewEffectSnapshotReplayDisposition.Invalid, HumanReviewEffectReleaseContract.ClassifyReplay(snapshot, forward));
        Assert.Equal(HumanReviewEffectSnapshotReplayDisposition.Invalid, HumanReviewEffectReleaseContract.ClassifyReplay(snapshot, badNestedHash));
        Assert.Equal(HumanReviewEffectSnapshotReplayDisposition.Invalid, HumanReviewEffectReleaseContract.ClassifyReplay(snapshot, badSnapshotHash));
    }

    [Fact]
    public void Reviewed_binding_must_be_hashed_and_conclusively_pre_dispatch()
    {
        var attempt = GovernedLoopEffectAttemptContractTests.Prepare();
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(Binding(attempt, null), attempt);
        var unsafeEffect = HumanReviewContractHash.ApplyEffectAttempt(new HumanReviewEffectAttemptBinding(
            attempt.Payload.EffectId,
            attempt.Payload.OperationId,
            attempt.Payload.EffectGeneration,
            attempt.Payload.IntentHash,
            preparation.PreparationHash,
            (HumanReviewEffectDispatchCertainty)99,
            string.Empty));
        var binding = HumanReviewContractHash.ApplyBinding(Binding(attempt, unsafeEffect));

        Assert.Throws<ArgumentException>(() => HumanReviewEffectReleaseContract.Create(binding, attempt, attempt.Payload.UpdatedAtUtc.AddMinutes(1)));
    }

    private static HumanReviewEffectCertaintySnapshot CreateSnapshot(GovernedLoopEffectAttempt attempt)
    {
        var unbound = Binding(attempt, null);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(unbound, attempt);
        var reviewEffect = HumanReviewContractHash.ApplyEffectAttempt(new HumanReviewEffectAttemptBinding(
            attempt.Payload.EffectId,
            attempt.Payload.OperationId,
            attempt.Payload.EffectGeneration,
            attempt.Payload.IntentHash,
            preparation.PreparationHash,
            HumanReviewEffectDispatchCertainty.NotDispatched,
            string.Empty));
        return HumanReviewEffectReleaseContract.Create(Binding(attempt, reviewEffect), attempt, attempt.Payload.UpdatedAtUtc.AddMinutes(1));
    }

    private static HumanReviewBinding Binding(GovernedLoopEffectAttempt attempt, HumanReviewEffectAttemptBinding? effect)
        => HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(
            1,
            HumanReviewTestData.WorkspaceId,
            attempt.Binding.RunId,
            attempt.Binding.Revision.GraphId,
            attempt.Binding.Revision.RevisionId,
            attempt.Binding.Revision.ExecutableHash,
            attempt.NodeId,
            0,
            null,
            attempt.NodeAttempt,
            "frontier-one",
            1,
            Hash('d'),
            Hash('e'),
            Hash('f'),
            Hash('1'),
            Hash('2'),
            Hash('3'),
            Hash('4'),
            Hash('5'),
            effect,
            string.Empty));

    private static string Hash(char value) => new(value, 64);
}
