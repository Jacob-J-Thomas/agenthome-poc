using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Wait;

public sealed class GovernedLoopWaitEvidenceContractTests
{
    [Fact]
    public void Activation_evidence_is_bounded_hash_bound_and_append_phase_composed()
    {
        var park = GovernedLoopWaitContractTestFixture.TimestampPark();
        var binding = park.Checkpoint.Binding;
        var parked = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitExecutionEvidence(
            1,
            binding.ActivationOrdinal,
            binding.NodeId,
            binding.NodeVisitOrdinal,
            binding.CycleId,
            binding.CycleIteration,
            binding.WaitAttempt,
            binding.WaitOperationId,
            park.Condition,
            park.ParkedAtUtc,
            binding.FrontierVersion,
            binding.FrontierHash,
            park,
            null,
            string.Empty));
        var continuation = GovernedLoopWaitContractTestFixture.Continuation(park);
        var resumed = GovernedLoopWaitContractHash.Apply(parked with { ContinuationEvidence = continuation });

        Assert.True(GovernedLoopWaitContractValidator.Validate(parked).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.Validate(resumed).IsValid);
        Assert.True(GovernedLoopWaitContractHash.Matches(resumed));
        Assert.NotEqual(parked.ContentHash, resumed.ContentHash);
        Assert.NotSame(park.Condition, parked.Condition);
        Assert.NotSame(park, parked.ParkEvidence);
        Assert.NotSame(continuation, resumed.ContinuationEvidence);

        var substitutedNode = GovernedLoopWaitContractHash.Apply(resumed with { NodeId = "other-node" });
        var missingPark = GovernedLoopWaitContractHash.Apply(resumed with { ParkEvidence = null });
        var invalidOrdinal = GovernedLoopWaitContractHash.Apply(resumed with { ActivationOrdinal = GovernedLoopExecutionLimits.MaxFrontierNodes });
        Assert.False(GovernedLoopWaitContractValidator.Validate(substitutedNode).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate(missingPark).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate(invalidOrdinal).IsValid);
        Assert.False(GovernedLoopWaitContractHash.Matches(resumed with { ContentHash = GovernedLoopWaitContractTestFixture.Hash('d') }));
        Assert.False(GovernedLoopWaitContractHash.Matches((GovernedLoopWaitExecutionEvidence?)null));
    }

    [Fact]
    public void Conditions_are_valid_hash_bound_and_mode_exclusive()
    {
        var timestamp = GovernedLoopWaitContractTestFixture.TimestampCondition();
        var eventCondition = GovernedLoopWaitContractTestFixture.EventCondition();
        var invalid = new[]
        {
            GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitCondition(timestamp.SchemaVersion, GovernedLoopWaitContractTestFixture.EventDescriptor(), timestamp.ParameterKind, timestamp.WakeDeadlineUtc, timestamp.AuthenticatedEventReference, string.Empty)),
            GovernedLoopWaitContractHash.Apply(timestamp with { ParameterKind = GovernedLoopWaitParameterKind.AuthenticatedEventReference }),
            GovernedLoopWaitContractHash.Apply(timestamp with { AuthenticatedEventReference = "event-1" }),
            GovernedLoopWaitContractHash.Apply(timestamp with { WakeDeadlineUtc = new DateTimeOffset(timestamp.WakeDeadlineUtc!.Value.DateTime, TimeSpan.FromHours(1)) }),
            GovernedLoopWaitContractHash.Apply(eventCondition with { WakeDeadlineUtc = GovernedLoopWaitContractTestFixture.DeadlineUtc }),
            GovernedLoopWaitContractHash.Apply(eventCondition with { AuthenticatedEventReference = null }),
            GovernedLoopWaitContractHash.Apply(eventCondition with { ParameterKind = (GovernedLoopWaitParameterKind)99 })
        };

        Assert.True(GovernedLoopWaitContractValidator.Validate(timestamp).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.Validate(eventCondition).IsValid);
        Assert.True(GovernedLoopWaitContractHash.Matches(timestamp));
        Assert.True(GovernedLoopWaitContractHash.Matches(eventCondition));
        Assert.All(invalid, condition => Assert.False(GovernedLoopWaitContractValidator.Validate(condition).IsValid));
        Assert.False(GovernedLoopWaitContractHash.Matches(timestamp with { ContentHash = GovernedLoopWaitContractTestFixture.Hash('f') }));
        Assert.False(GovernedLoopWaitContractHash.Matches((GovernedLoopWaitCondition?)null));
    }

    [Fact]
    public void Condition_hash_changes_for_every_exact_descriptor_or_parameter_coordinate()
    {
        var timestamp = GovernedLoopWaitContractTestFixture.TimestampCondition();
        var same = GovernedLoopWaitContractTestFixture.TimestampCondition();
        var eventCondition = GovernedLoopWaitContractTestFixture.EventCondition();
        var variants = new[]
        {
            GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitCondition(1, new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, GovernedLoopWaitVocabulary.Timestamp, 2), timestamp.ParameterKind, timestamp.WakeDeadlineUtc, null, string.Empty)),
            GovernedLoopWaitContractHash.Apply(timestamp with { ParameterKind = GovernedLoopWaitParameterKind.AuthenticatedEventReference }),
            GovernedLoopWaitContractHash.Apply(timestamp with { WakeDeadlineUtc = timestamp.WakeDeadlineUtc!.Value.AddTicks(1) }),
            GovernedLoopWaitContractTestFixture.EventCondition("governed-event-2")
        };

        Assert.Equal(timestamp.ContentHash, same.ContentHash);
        Assert.NotEqual(timestamp.ContentHash, eventCondition.ContentHash);
        Assert.All(variants, variant => Assert.NotEqual(timestamp.ContentHash, variant.ContentHash));
    }

    [Fact]
    public void Park_evidence_binds_exact_condition_and_sleep_checkpoint()
    {
        var timestamp = GovernedLoopWaitContractTestFixture.TimestampPark();
        var eventPark = GovernedLoopWaitContractTestFixture.EventPark();
        var substitutedDeadline = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(timestamp.SchemaVersion, timestamp.Condition, GovernedLoopSleepContractTestFixture.TimestampCheckpoint(deadlineUtc: timestamp.Condition.WakeDeadlineUtc!.Value.AddTicks(1)), timestamp.ParkedAtUtc, string.Empty));
        var substitutedEvent = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(eventPark.SchemaVersion, eventPark.Condition, GovernedLoopSleepContractTestFixture.EventCheckpoint("other-event"), eventPark.ParkedAtUtc, string.Empty));
        var publishedBeforePark = GovernedLoopWaitContractHash.Apply(timestamp with
        {
            ParkedAtUtc = timestamp.Checkpoint.PublishedAtUtc.AddTicks(1)
        });

        Assert.True(GovernedLoopWaitContractValidator.Validate(timestamp).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.Validate(eventPark).IsValid);
        Assert.True(GovernedLoopWaitContractHash.Matches(timestamp));
        Assert.False(GovernedLoopWaitContractValidator.Validate(substitutedDeadline).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate(substitutedEvent).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate(publishedBeforePark).IsValid);
    }

    [Fact]
    public void Park_hash_changes_for_condition_checkpoint_and_park_time()
    {
        var park = GovernedLoopWaitContractTestFixture.TimestampPark();
        var variants = new[]
        {
            GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(1, GovernedLoopWaitContractTestFixture.TimestampCondition(park.Condition.WakeDeadlineUtc!.Value.AddTicks(1)), park.Checkpoint, park.ParkedAtUtc, string.Empty)),
            GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(1, park.Condition, GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(frontierVersion: 8), park.Condition.WakeDeadlineUtc), park.ParkedAtUtc, string.Empty)),
            GovernedLoopWaitContractHash.Apply(park with { ParkedAtUtc = park.ParkedAtUtc.AddTicks(-1) })
        };

        Assert.All(variants, variant => Assert.NotEqual(park.ContentHash, variant.ContentHash));
    }

    [Fact]
    public void Continuation_binds_prepared_wake_and_exact_pre_resume_successor()
    {
        var park = GovernedLoopWaitContractTestFixture.TimestampPark(frontierVersion: 7);
        var continuation = GovernedLoopWaitContractTestFixture.Continuation(park, preResumeFrontierVersion: 11);
        var advancedBySibling = GovernedLoopWaitContractTestFixture.Continuation(park, preResumeFrontierVersion: 14);
        var committedWake = GovernedLoopWaitContractTestFixture.CommittedWake(continuation);
        var advancedCommittedWake = GovernedLoopWaitContractTestFixture.CommittedWake(advancedBySibling);

        Assert.True(GovernedLoopWaitContractValidator.Validate(continuation).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, committedWake).IsValid);
        Assert.Equal(continuation.ContentHash, committedWake.ContinuationEvidenceHash);
        Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, advancedBySibling).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, advancedBySibling, advancedCommittedWake).IsValid);
        Assert.Equal(15, advancedBySibling.ResumedFrontierVersion);
        Assert.NotEqual(park.Checkpoint.Binding.FrontierVersion + 1, advancedBySibling.ResumedFrontierVersion);

        var skippedVersion = GovernedLoopWaitContractHash.Apply(continuation with { ResumedFrontierVersion = continuation.PreResumeFrontierVersion + 2 });
        var regressedCurrent = GovernedLoopWaitContractTestFixture.Continuation(park, preResumeFrontierVersion: park.Checkpoint.Binding.FrontierVersion - 1);
        var substitutedCurrent = GovernedLoopWaitContractTestFixture.Continuation(park, preResumeFrontierVersion: park.Checkpoint.Binding.FrontierVersion, preResumeFrontierHash: GovernedLoopWaitContractTestFixture.Hash('d'));
        var stalePark = GovernedLoopWaitContractHash.Apply(continuation with { ParkEvidenceHash = GovernedLoopWaitContractTestFixture.Hash('d') });
        var substitutedWake = GovernedLoopWaitContractTestFixture.Continuation(GovernedLoopWaitContractTestFixture.EventPark());

        Assert.Contains(
            GovernedLoopWaitContractValidator.Validate(skippedVersion).Errors,
            error => error.Code == GovernedLoopWaitValidationErrorCode.InvalidSuccessorVersion);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, regressedCurrent).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, substitutedCurrent).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, stalePark).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, substitutedWake).IsValid);
    }

    [Fact]
    public void Continuation_requires_preparation_before_resume_and_commit_at_or_after_resume()
    {
        var park = GovernedLoopWaitContractTestFixture.TimestampPark();
        var continuation = GovernedLoopWaitContractTestFixture.Continuation(park);
        var committedAsPreparation = GovernedLoopWaitContractTestFixture.CommittedWake(continuation);
        var nonPrepared = GovernedLoopWaitContractTestFixture.WithPreparedWake(continuation, committedAsPreparation);
        var nonUtc = GovernedLoopWaitContractHash.Apply(continuation with
        {
            ResumedAtUtc = new DateTimeOffset(continuation.ResumedAtUtc.DateTime, TimeSpan.FromHours(1))
        });
        var beforePreparation = GovernedLoopWaitContractHash.Apply(continuation with
        {
            ResumedAtUtc = continuation.PreparedWakeEvidence.RecordedAtUtc.AddTicks(-1)
        });
        var atPreparation = GovernedLoopWaitContractHash.Apply(continuation with
        {
            ResumedAtUtc = continuation.PreparedWakeEvidence.RecordedAtUtc
        });
        var committedAtResume = GovernedLoopWaitContractTestFixture.CommittedWake(continuation, recordedAtUtc: continuation.ResumedAtUtc);
        var committedAfterResume = GovernedLoopWaitContractTestFixture.CommittedWake(continuation, recordedAtUtc: continuation.ResumedAtUtc.AddTicks(1));
        var committedBeforeResume = GovernedLoopWaitContractTestFixture.CommittedWake(continuation, recordedAtUtc: continuation.ResumedAtUtc.AddTicks(-1));

        Assert.False(GovernedLoopWaitContractValidator.Validate(nonPrepared).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate(nonUtc).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate(beforePreparation).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.Validate(atPreparation).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, committedAtResume).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, committedAfterResume).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, committedBeforeResume).IsValid);
    }

    [Fact]
    public void Committed_wake_must_be_the_exact_direct_or_single_ambiguous_completion_of_the_continuation()
    {
        var park = GovernedLoopWaitContractTestFixture.TimestampPark();
        var continuation = GovernedLoopWaitContractTestFixture.Continuation(park);
        var committed = GovernedLoopWaitContractTestFixture.CommittedWake(continuation);
        var otherPark = GovernedLoopWaitContractTestFixture.EventPark();
        var substitutedIdentity = GovernedLoopWaitContractTestFixture.CommittedWake(
            continuation,
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(otherPark.Checkpoint));
        var substitutedOperation = GovernedLoopWaitContractTestFixture.CommittedWake(
            continuation,
            continuationOperationId: "other-continuation-operation");
        var substitutedPointer = GovernedLoopSleepContractHash.Apply(committed with
        {
            ContinuationEvidenceHash = GovernedLoopWaitContractTestFixture.Hash('d')
        });
        var afterAmbiguousAttempt = GovernedLoopWaitContractTestFixture.CommittedWake(
            continuation,
            evidenceVersion: continuation.PreparedWakeEvidence.EvidenceVersion + 2);
        var skippedEvidenceVersion = GovernedLoopWaitContractTestFixture.CommittedWake(
            continuation,
            evidenceVersion: continuation.PreparedWakeEvidence.EvidenceVersion + 3);
        var substitutedPreparation = GovernedLoopSleepContractHash.Apply(continuation.PreparedWakeEvidence with
        {
            ContinuationOperationId = "other-preparation-operation"
        });
        var substitutedContinuation = GovernedLoopWaitContractTestFixture.WithPreparedWake(continuation, substitutedPreparation);

        Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, committed).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, afterAmbiguousAttempt).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, substitutedIdentity).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, substitutedOperation).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, substitutedPointer).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, skippedEvidenceVersion).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, substitutedContinuation, committed).IsValid);
    }

    [Fact]
    public void Continuation_hash_changes_for_every_current_and_successor_coordinate()
    {
        var continuation = GovernedLoopWaitContractTestFixture.Continuation();
        var otherPreparation = GovernedLoopSleepContractHash.Apply(continuation.PreparedWakeEvidence with
        {
            ContinuationOperationId = "other-continuation-operation"
        });
        var variants = new[]
        {
            GovernedLoopWaitContractHash.Apply(continuation with { ParkEvidenceHash = GovernedLoopWaitContractTestFixture.Hash('d') }),
            GovernedLoopWaitContractTestFixture.WithPreparedWake(continuation, otherPreparation),
            GovernedLoopWaitContractHash.Apply(continuation with { PreResumeFrontierVersion = continuation.PreResumeFrontierVersion + 1 }),
            GovernedLoopWaitContractHash.Apply(continuation with { PreResumeFrontierHash = GovernedLoopWaitContractTestFixture.Hash('d') }),
            GovernedLoopWaitContractHash.Apply(continuation with { ResumedFrontierVersion = continuation.ResumedFrontierVersion + 1 }),
            GovernedLoopWaitContractHash.Apply(continuation with { ResumedFrontierHash = GovernedLoopWaitContractTestFixture.Hash('d') }),
            GovernedLoopWaitContractHash.Apply(continuation with { ResumedAtUtc = continuation.ResumedAtUtc.AddTicks(1) })
        };

        Assert.All(variants, variant => Assert.NotEqual(continuation.ContentHash, variant.ContentHash));
        Assert.True(GovernedLoopWaitContractHash.Matches(continuation));
        Assert.False(GovernedLoopWaitContractHash.Matches(continuation with { ContentHash = GovernedLoopWaitContractTestFixture.Hash('d') }));
        Assert.False(GovernedLoopWaitContractHash.Matches((GovernedLoopWaitContinuationEvidence?)null));
    }

    [Fact]
    public void Exact_bounds_are_accepted_and_limit_plus_one_fails_before_hashing()
    {
        var exactEvent = GovernedLoopWaitContractTestFixture.EventParameters(new string('a', GovernedLoopWaitContractLimits.MaxEventReferenceCharacters));
        var tooLargeEvent = GovernedLoopWaitContractTestFixture.EventParameters(new string('a', GovernedLoopWaitContractLimits.MaxEventReferenceCharacters + 1));
        var continuation = GovernedLoopWaitContractTestFixture.Continuation(preResumeFrontierVersion: GovernedLoopWaitContractLimits.MaxVersion - 1);
        var tooLarge = GovernedLoopWaitContractHash.Apply(continuation with
        {
            PreResumeFrontierVersion = GovernedLoopWaitContractLimits.MaxVersion - 2,
            ResumedFrontierVersion = GovernedLoopWaitContractLimits.MaxVersion
        });
        var exhausted = GovernedLoopWaitContractHash.Apply(continuation with
        {
            PreResumeFrontierVersion = GovernedLoopWaitContractLimits.MaxVersion,
            ResumedFrontierVersion = GovernedLoopWaitContractLimits.MaxVersion
        });
        var massive = new GovernedLoopWaitCondition(
            1,
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, new string('a', 1_000_000), 1),
            GovernedLoopWaitParameterKind.AuthenticatedEventReference,
            null,
            new string('a', 1_000_000),
            GovernedLoopWaitContractTestFixture.Hash('a'));

        Assert.True(GovernedLoopWaitContractValidator.ValidateDescriptor(GovernedLoopWaitContractTestFixture.EventDescriptor(), exactEvent).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateDescriptor(GovernedLoopWaitContractTestFixture.EventDescriptor(), tooLargeEvent).IsValid);
        Assert.True(GovernedLoopWaitContractValidator.Validate(continuation).IsValid);
        Assert.Contains(GovernedLoopWaitContractValidator.Validate(tooLarge).Errors, error => error.Code == GovernedLoopWaitValidationErrorCode.InvalidSuccessorVersion);
        Assert.Contains(GovernedLoopWaitContractValidator.Validate(exhausted).Errors, error => error.Code == GovernedLoopWaitValidationErrorCode.InvalidSuccessorVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopWaitContractHash.Compute(massive));
        Assert.False(GovernedLoopWaitContractHash.Matches(massive));
    }

    [Fact]
    public void Records_defensively_copy_all_nested_public_evidence()
    {
        var condition = GovernedLoopWaitContractTestFixture.TimestampCondition();
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(deadlineUtc: condition.WakeDeadlineUtc);
        var park = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitParkEvidence(
            1,
            condition,
            checkpoint,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddTicks(-1),
            string.Empty));
        var preparedWake = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Prepared,
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint),
            recordedAtUtc: checkpoint.WakeDeadlineUtc);
        var continuation = GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitContinuationEvidence(
            1,
            park.ContentHash,
            preparedWake,
            9,
            GovernedLoopWaitContractTestFixture.Hash('e'),
            10,
            GovernedLoopWaitContractTestFixture.Hash('f'),
            preparedWake.RecordedAtUtc,
            string.Empty));

        Assert.NotSame(condition, park.Condition);
        Assert.NotSame(condition.Descriptor, park.Condition.Descriptor);
        Assert.NotSame(checkpoint, park.Checkpoint);
        Assert.NotSame(checkpoint.Binding, park.Checkpoint.Binding);
        Assert.NotSame(preparedWake, continuation.PreparedWakeEvidence);
        Assert.NotSame(preparedWake.Identity, continuation.PreparedWakeEvidence.Identity);
        Assert.Equal(condition, park.Condition);
        Assert.Equal(checkpoint, park.Checkpoint);
        Assert.Equal(preparedWake, continuation.PreparedWakeEvidence);
    }

    [Fact]
    public void Malformed_nested_shapes_and_unsupported_schema_fail_closed()
    {
        var condition = GovernedLoopWaitContractTestFixture.TimestampCondition();
        var park = GovernedLoopWaitContractTestFixture.TimestampPark();
        var continuation = GovernedLoopWaitContractTestFixture.Continuation(park);
        var invalidCondition = new GovernedLoopWaitCondition(2, null!, (GovernedLoopWaitParameterKind)99, null, null, "bad-hash");
        var invalidPark = new GovernedLoopWaitParkEvidence(2, null!, null!, default, "bad-hash");
        var invalidContinuation = new GovernedLoopWaitContinuationEvidence(2, "bad-hash", null!, 0, "bad-hash", 0, "bad-hash", default, "bad-hash");

        Assert.False(GovernedLoopWaitContractValidator.Validate(invalidCondition).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate(invalidPark).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate(invalidContinuation).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(null, continuation).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, null).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.ValidateComposition(park, continuation, null).IsValid);
        Assert.False(GovernedLoopWaitContractHash.Matches(condition with { ContentHash = "BAD" }));
        Assert.False(GovernedLoopWaitContractHash.Matches(park with { ContentHash = "BAD" }));
        Assert.False(GovernedLoopWaitContractHash.Matches(continuation with { ContentHash = "BAD" }));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopWaitContractHash.Compute((GovernedLoopWaitCondition)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopWaitContractHash.Compute((GovernedLoopWaitParkEvidence)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopWaitContractHash.Compute((GovernedLoopWaitContinuationEvidence)null!));
    }

    [Fact]
    public void Validation_errors_are_bounded_value_free_and_immutable()
    {
        var invalid = GovernedLoopWaitContractTestFixture.TimestampCondition() with { ContentHash = "SECRET-value" };
        var first = Assert.Single(
            GovernedLoopWaitContractValidator.Validate(invalid).Errors,
            error => error.Code == GovernedLoopWaitValidationErrorCode.InvalidHash);
        var second = Assert.Single(
            GovernedLoopWaitContractValidator.Validate(invalid).Errors,
            error => error.Code == GovernedLoopWaitValidationErrorCode.InvalidHash);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.False(first.Equals(null));
        Assert.False(first.Equals(new object()));
        Assert.Equal("InvalidHash at $.contentHash", first.ToString());
        Assert.DoesNotContain("SECRET", first.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopWaitValidationError>)GovernedLoopWaitContractValidator.Validate(invalid).Errors).Clear());
        Assert.False(GovernedLoopWaitContractValidator.Validate((GovernedLoopWaitCondition?)null).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate((GovernedLoopWaitParkEvidence?)null).IsValid);
        Assert.False(GovernedLoopWaitContractValidator.Validate((GovernedLoopWaitContinuationEvidence?)null).IsValid);
    }
}
