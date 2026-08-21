using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopWakeEvidenceContractTests
{
    [Fact]
    public void Restart_can_publish_and_continue_an_already_due_timestamp_checkpoint()
    {
        var deadlineUtc = GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(1);
        var restartPublishedAtUtc = deadlineUtc.AddMinutes(5);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            deadlineUtc: deadlineUtc,
            publishedAtUtc: restartPublishedAtUtc);
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: identity,
            recordedAtUtc: restartPublishedAtUtc);

        Assert.True(GovernedLoopSleepContractValidator.Validate(checkpoint).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, prepared).IsValid);
    }

    [Fact]
    public void Timestamp_continuation_waits_for_eligibility_but_proactive_terminal_posture_remains_explicit()
    {
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var earlyUtc = checkpoint.PublishedAtUtc.AddMinutes(30);
        var earlyAttempts = new[]
        {
            GovernedLoopSleepContractTestFixture.WakeEvidence(GovernedLoopWakeDisposition.Prepared, identity: identity, recordedAtUtc: earlyUtc),
            GovernedLoopSleepContractTestFixture.WakeEvidence(GovernedLoopWakeDisposition.Committed, identity: identity, recordedAtUtc: earlyUtc),
            GovernedLoopSleepContractTestFixture.WakeEvidence(GovernedLoopWakeDisposition.AmbiguousAttempt, identity: identity, recordedAtUtc: earlyUtc),
            GovernedLoopSleepContractTestFixture.WakeEvidence(
                GovernedLoopWakeDisposition.Failed,
                identity: identity,
                continuationOperationId: "continuation-operation-1",
                recordedAtUtc: earlyUtc)
        };
        var earlyCancelled = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Cancelled,
            identity: identity,
            recordedAtUtc: earlyUtc);
        var earlyFailureWithoutAttempt = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Failed,
            identity: identity,
            recordedAtUtc: earlyUtc);
        var beforePublication = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Cancelled,
            identity: identity,
            recordedAtUtc: checkpoint.PublishedAtUtc.AddTicks(-1));
        var atDeadline = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Prepared,
            identity: identity,
            recordedAtUtc: checkpoint.WakeDeadlineUtc);

        Assert.All(earlyAttempts, evidence => Assert.Contains(
            GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, evidence).Errors,
            error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidTimestamp));
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, earlyCancelled).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, earlyFailureWithoutAttempt).IsValid);
        Assert.Contains(
            GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, beforePublication).Errors,
            error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidTimestamp);
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, atDeadline).IsValid);
    }

    [Fact]
    public void Timestamp_late_disposition_is_rejected_before_and_accepted_at_the_exact_deadline()
    {
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var beforeDeadline = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Late,
            identity: identity,
            recordedAtUtc: checkpoint.WakeDeadlineUtc!.Value.AddTicks(-1));
        var atDeadline = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Late,
            identity: identity,
            recordedAtUtc: checkpoint.WakeDeadlineUtc);

        Assert.Contains(
            GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, beforeDeadline).Errors,
            error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidTimestamp
                && error.Path == "$.evidence.recordedAtUtc");
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, atDeadline).IsValid);
    }

    [Fact]
    public void Wake_evidence_chronology_requires_the_exact_checkpoint_identity()
    {
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var substitutedCheckpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(nodeVisitOrdinal: 2));
        var evidence = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint),
            recordedAtUtc: checkpoint.WakeDeadlineUtc);

        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, evidence).IsValid);
        Assert.Contains(
            GovernedLoopSleepContractValidator.ValidateComposition(substitutedCheckpoint, evidence).Errors,
            error => error.Code == GovernedLoopSleepValidationErrorCode.BindingMismatch);
    }

    [Fact]
    public void Every_closed_wake_disposition_has_one_valid_bounded_evidence_shape()
    {
        foreach (var disposition in Enum.GetValues<GovernedLoopWakeDisposition>())
        {
            var evidence = GovernedLoopSleepContractTestFixture.WakeEvidence(disposition);

            Assert.True(GovernedLoopSleepContractValidator.Validate(evidence).IsValid, disposition.ToString());
            Assert.True(GovernedLoopSleepContractHash.Matches(evidence));
        }
    }

    [Fact]
    public void Prepared_intent_advances_only_to_exact_commit_ambiguity_or_failure()
    {
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(GovernedLoopSleepContractTestFixture.TimestampCheckpoint());
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(1));
        var ambiguous = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.AmbiguousAttempt,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(1));
        var failed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Failed,
            2,
            identity,
            continuationOperationId: "continuation-operation-1",
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(1));

        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(prepared, committed).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(prepared, ambiguous).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(prepared, failed).IsValid);
        Assert.True(GovernedLoopSleepStateMatrix.IsWakeTransitionAllowed(GovernedLoopWakeDisposition.Prepared, GovernedLoopWakeDisposition.Committed));
        Assert.False(GovernedLoopSleepStateMatrix.IsWakeTransitionAllowed(GovernedLoopWakeDisposition.Committed, GovernedLoopWakeDisposition.Prepared));
    }

    [Fact]
    public void Ambiguous_intent_can_be_reconciled_but_terminal_evidence_cannot_reopen()
    {
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(GovernedLoopSleepContractTestFixture.TimestampCheckpoint());
        var ambiguous = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.AmbiguousAttempt,
            identity: identity);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: ambiguous.RecordedAtUtc.AddTicks(1));
        var stale = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Stale,
            identity: identity);
        var staleSuccessor = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Prepared,
            2,
            identity,
            recordedAtUtc: stale.RecordedAtUtc.AddTicks(1));

        Assert.True(GovernedLoopSleepContractValidator.ValidateTransition(ambiguous, committed).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.ValidateTransition(stale, staleSuccessor).IsValid);
    }

    [Fact]
    public void Transition_rejects_version_gap_identity_substitution_operation_change_and_time_reversal()
    {
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(GovernedLoopSleepContractTestFixture.TimestampCheckpoint());
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var wrongIdentity = GovernedLoopSleepContractTestFixture.WakeIdentity(
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(nodeVisitOrdinal: 2)));
        var versionGap = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            3,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(1));
        var substituted = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            wrongIdentity,
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(1));
        var changedOperation = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            continuationOperationId: "continuation-operation-2",
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(1));
        var reversedTime = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(-1));

        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(prepared, versionGap).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(prepared, substituted).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(prepared, changedOperation).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged);
        Assert.Contains(GovernedLoopSleepContractValidator.ValidateTransition(prepared, reversedTime).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IllegalTransition);
    }

    [Fact]
    public void Malformed_disposition_shapes_hashes_versions_and_enumerations_fail_closed()
    {
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence();
        var committedWithoutEvidence = GovernedLoopSleepContractHash.Apply(
            prepared with
            {
                Disposition = GovernedLoopWakeDisposition.Committed,
                ContinuationEvidenceHash = null
            });
        var rejectedWithOperation = GovernedLoopSleepContractHash.Apply(
            GovernedLoopSleepContractTestFixture.WakeEvidence(GovernedLoopWakeDisposition.Stale) with
            {
                ContinuationOperationId = "continuation-operation-1"
            });
        var unsupported = GovernedLoopSleepContractHash.Apply(prepared with { Disposition = (GovernedLoopWakeDisposition)99 });
        var tooLarge = GovernedLoopSleepContractHash.Apply(prepared with { EvidenceVersion = GovernedLoopSleepContractLimits.MaxVersion + 1 });
        var tampered = prepared with { RecordedAtUtc = prepared.RecordedAtUtc.AddTicks(1) };

        Assert.False(GovernedLoopSleepContractValidator.Validate(committedWithoutEvidence).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(rejectedWithOperation).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(unsupported).IsValid);
        Assert.Contains(GovernedLoopSleepContractValidator.Validate(tooLarge).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.LimitExceeded);
        Assert.Contains(GovernedLoopSleepContractValidator.Validate(tampered).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IntegrityMismatch);
        Assert.False(GovernedLoopSleepContractHash.Matches((GovernedLoopWakeEvidence?)null));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.Compute((GovernedLoopWakeEvidence)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.Apply((GovernedLoopWakeEvidence)null!));
        Assert.False(GovernedLoopSleepContractValidator.Validate((GovernedLoopWakeEvidence?)null).IsValid);
    }
}
