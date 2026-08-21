using System.Text;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Effects;

public sealed class GovernedLoopEffectAttemptContractTests
{
    [Fact]
    public void Attempt_binds_exact_execution_catalog_and_value_free_intent()
    {
        var attempt = Prepare();

        Assert.Null(GovernedLoopEffectAttemptContract.Validate(attempt));
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, attempt.Payload.Phase);
        Assert.Equal(attempt.NodeId, attempt.Payload.OriginNodeId);
        Assert.Equal(attempt.Capability, GovernedActuatorOperationContractTests.Create().Capability);
        Assert.Equal(64, attempt.Payload.IntentHash.Length);
        Assert.Null(attempt.DispatchAuthorityEvidenceHash);
        Assert.Null(attempt.PreviousContentHash);
    }

    [Fact]
    public void Legal_success_path_is_hash_chained_and_replay_identical()
    {
        var prepared = Prepare();
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), prepared.Payload.UpdatedAtUtc.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(
            authorized,
            GovernedLoopEffectPhase.DispatchBoundaryReached,
            GovernedLoopEffectOutcome.OutcomeUnknown,
            GovernedLoopEffectEvidenceStatus.Pending,
            null,
            null,
            prepared.Payload.UpdatedAtUtc.AddSeconds(2));
        var observed = GovernedLoopEffectAttemptContract.Advance(
            crossed,
            GovernedLoopEffectPhase.OutcomeObserved,
            GovernedLoopEffectOutcome.Succeeded,
            GovernedLoopEffectEvidenceStatus.Complete,
            "probe-outcome",
            "probe-after",
            prepared.Payload.UpdatedAtUtc.AddSeconds(3));
        var committed = GovernedLoopEffectAttemptContract.Advance(
            observed,
            GovernedLoopEffectPhase.Committed,
            GovernedLoopEffectOutcome.Succeeded,
            GovernedLoopEffectEvidenceStatus.Complete,
            "probe-outcome",
            "probe-after",
            prepared.Payload.UpdatedAtUtc.AddSeconds(4));

        Assert.Equal(prepared.ContentHash, authorized.PreviousContentHash);
        Assert.Equal(authorized.ContentHash, crossed.PreviousContentHash);
        Assert.Equal(crossed.ContentHash, observed.PreviousContentHash);
        Assert.Equal(observed.ContentHash, committed.PreviousContentHash);
        Assert.Null(GovernedLoopEffectAttemptContract.Validate(committed));
        Assert.True(GovernedLoopEffectAttemptContract.HasSameIntent(prepared, committed));
        Assert.Throws<InvalidOperationException>(() => GovernedLoopEffectAttemptContract.Advance(committed, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, committed.Payload.UpdatedAtUtc.AddSeconds(1)));
    }

    [Fact]
    public void Changed_authorized_identity_and_illegal_evidence_fail_closed()
    {
        var prepared = Prepare();
        var changed = Prepare(inputFingerprint: Hash('9'));

        Assert.False(GovernedLoopEffectAttemptContract.HasSameIntent(prepared, changed));
        Assert.Equal("effect-attempt-intent-hash-mismatch", GovernedLoopEffectAttemptContract.Validate(prepared with { TargetFingerprint = Hash('0') }));
        Assert.Equal("effect-attempt-content-hash-mismatch", GovernedLoopEffectAttemptContract.Validate(prepared with { ContentHash = Hash('0') }));
        Assert.Throws<InvalidOperationException>(() => GovernedLoopEffectAttemptContract.Advance(prepared, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, prepared.Payload.UpdatedAtUtc));
    }

    [Fact]
    public void Proved_pre_boundary_stop_needs_no_direct_authority_and_cannot_redispatch()
    {
        var prepared = Prepare();
        var stopped = GovernedLoopEffectAttemptContract.Advance(
            prepared,
            GovernedLoopEffectPhase.DispatchNotStarted,
            GovernedLoopEffectOutcome.None,
            GovernedLoopEffectEvidenceStatus.Complete,
            null,
            null,
            prepared.Payload.UpdatedAtUtc.AddSeconds(1));

        Assert.Null(stopped.DispatchAuthorityEvidenceHash);
        Assert.Null(GovernedLoopEffectAttemptContract.Validate(stopped));
        Assert.True(GovernedLoopEffectAttemptContract.IsDirectSuccessor(prepared, stopped));
        Assert.False(GovernedLoopExecutionStateMatrix.IsEffectDispatchEligible(stopped.Payload));
    }

    [Fact]
    public void Direct_successor_validation_rejects_skipped_phases_and_changed_evidence()
    {
        var prepared = Prepare();
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), prepared.Payload.UpdatedAtUtc.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, prepared.Payload.UpdatedAtUtc.AddSeconds(2));
        var observed = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "probe-outcome", "probe-after", prepared.Payload.UpdatedAtUtc.AddSeconds(3));

        Assert.False(GovernedLoopEffectAttemptContract.IsDirectSuccessor(authorized, observed with { PreviousContentHash = authorized.ContentHash, ContentHash = GovernedLoopEffectAttemptContract.Compute(observed with { PreviousContentHash = authorized.ContentHash, ContentHash = string.Empty }) }));
        Assert.False(GovernedLoopEffectAttemptContract.IsDirectSuccessor(crossed, observed with { BeforeEvidenceId = "changed-before", PreviousContentHash = crossed.ContentHash }));
    }

    [Fact]
    public void Standalone_terminal_and_phase_incompatible_after_evidence_are_invalid_shapes()
    {
        var prepared = Prepare();
        var stopped = GovernedLoopEffectAttemptContract.Advance(prepared, GovernedLoopEffectPhase.DispatchNotStarted, GovernedLoopEffectOutcome.None, GovernedLoopEffectEvidenceStatus.Complete, null, null, prepared.Payload.UpdatedAtUtc.AddSeconds(1));

        Assert.Equal("effect-attempt-authority-phase-invalid", GovernedLoopEffectAttemptContract.Validate(stopped with { PreviousContentHash = null, ContentHash = stopped.ContentHash }));
        Assert.Equal("effect-attempt-authority-phase-invalid", GovernedLoopEffectAttemptContract.Validate(prepared with { AfterEvidenceId = "too-early" }));
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), prepared.Payload.UpdatedAtUtc.AddSeconds(1));
        Assert.Equal("effect-attempt-authority-phase-invalid", GovernedLoopEffectAttemptContract.Validate(authorized with { AfterEvidenceId = "too-early" }));
    }

    [Fact]
    public void Persistence_codec_round_trips_only_canonical_value_free_evidence()
    {
        var attempt = Prepare();
        var encoded = GovernedLoopEffectAttemptRecordCodec.Encode(attempt);

        Assert.True(GovernedLoopEffectAttemptRecordCodec.TryDecode(encoded, out var decoded, out var reason), reason);
        Assert.Equal(attempt, decoded);
        Assert.DoesNotContain("private-value", Encoding.UTF8.GetString(encoded), StringComparison.Ordinal);

        var whitespace = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(encoded));
        Assert.False(GovernedLoopEffectAttemptRecordCodec.TryDecode(whitespace, out _, out _));
        var unknown = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(encoded)[..^1] + ",\"rawSecret\":\"private-value\"}");
        Assert.False(GovernedLoopEffectAttemptRecordCodec.TryDecode(unknown, out _, out _));
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(encoded).Replace(Hash('2'), Hash('9'), StringComparison.Ordinal));
        Assert.False(GovernedLoopEffectAttemptRecordCodec.TryDecode(tampered, out _, out _));
    }

    internal static GovernedLoopEffectAttempt Prepare(string? inputFingerprint = null)
    {
        var operation = GovernedActuatorOperationContractTests.Create();
        return GovernedLoopEffectAttemptContract.Prepare(
            GovernedLoopExecutionTestFixture.Binding(),
            "infer",
            1,
            operation.Capability,
            operation.Implementation,
            operation.OperationId,
            operation.ContentHash,
            "effect-1",
            "effect-operation-1",
            1,
            inputFingerprint ?? Hash('2'),
            Hash('3'),
            Hash('4'),
            Hash('5'),
            "probe-before",
            GovernedLoopExecutionTestFixture.UpdatedAtUtc);
    }

    internal static string Hash(char value) => new(value, 64);
}
