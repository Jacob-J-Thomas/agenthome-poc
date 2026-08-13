using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopSleepCheckpointContractTests
{
    [Fact]
    public void Timestamp_checkpoint_is_valid_deterministic_and_bound_to_every_exact_coordinate()
    {
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var same = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var variants = new[]
        {
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(runId: "run-2")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(executionGeneration: 2)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(publicationOperationId: "publication-operation-2")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(publicationEvidenceHash: GovernedLoopSleepContractTestFixture.Hash('9'))),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(frontierVersion: 8)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(frontierHash: GovernedLoopSleepContractTestFixture.Hash('8'))),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(activationOrdinal: 4)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(cycleId: "cycle-1", cycleIteration: 1)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(nodeId: "wait-node-2")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(nodeVisitOrdinal: 2)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(waitAttempt: 2)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(waitOperationId: "wait-operation-2")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(deadlineUtc: GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(2))
        };

        Assert.True(GovernedLoopSleepContractValidator.Validate(checkpoint).IsValid);
        Assert.True(GovernedLoopSleepContractHash.Matches(checkpoint));
        Assert.Equal(checkpoint.CheckpointId, same.CheckpointId);
        Assert.Equal(checkpoint.ContentHash, same.ContentHash);
        Assert.Equal(GovernedLoopSleepContractLimits.Sha256HexCharacters, checkpoint.CheckpointId.Length);
        Assert.All(checkpoint.CheckpointId, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.All(variants, variant => Assert.NotEqual(checkpoint.CheckpointId, variant.CheckpointId));
        Assert.All(variants, variant => Assert.True(GovernedLoopSleepContractValidator.Validate(variant).IsValid));
    }

    [Fact]
    public void Authenticated_event_wake_identity_is_deterministic_and_requires_exact_authentication_evidence()
    {
        var checkpoint = GovernedLoopSleepContractTestFixture.EventCheckpoint();
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var same = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var changedEvidence = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint, GovernedLoopSleepContractTestFixture.Hash('f'));

        Assert.True(GovernedLoopSleepContractValidator.Validate(checkpoint).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.Validate(identity).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, identity).IsValid);
        Assert.True(GovernedLoopSleepContractHash.Matches(identity));
        Assert.Equal(identity.WakeId, same.WakeId);
        Assert.NotEqual(identity.WakeId, changedEvidence.WakeId);

        var missingAuthentication = GovernedLoopSleepContractHash.Apply(identity with { AuthenticationEvidenceHash = null });
        Assert.Contains(
            GovernedLoopSleepContractValidator.Validate(missingAuthentication).Errors,
            error => error.Code is GovernedLoopSleepValidationErrorCode.InvalidHash or GovernedLoopSleepValidationErrorCode.InvalidComposition);
    }

    [Fact]
    public void Timestamp_and_event_shapes_fail_closed_without_cross_mode_coordinates()
    {
        var timestamp = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var timestampWithEvent = GovernedLoopSleepContractHash.Apply(timestamp with { AuthenticatedEventReference = "authenticated-event-1" });
        var eventCheckpoint = GovernedLoopSleepContractTestFixture.EventCheckpoint();
        var eventWithDeadline = GovernedLoopSleepContractHash.Apply(eventCheckpoint with { WakeDeadlineUtc = GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(1) });
        var alreadyDuePublication = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            deadlineUtc: GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            publishedAtUtc: GovernedLoopSleepContractTestFixture.PublishedAtUtc);
        var invalidMode = GovernedLoopSleepContractHash.Apply(timestamp with { WakeMode = (GovernedLoopWakeMode)99 });
        var timestampIdentity = GovernedLoopSleepContractTestFixture.WakeIdentity(timestamp);
        var timestampIdentityWithAuthentication = GovernedLoopSleepContractHash.Apply(
            timestampIdentity with
            {
                AuthenticatedEventReference = "authenticated-event-1",
                AuthenticationEvidenceHash = GovernedLoopSleepContractTestFixture.Hash('d')
            });

        Assert.False(GovernedLoopSleepContractValidator.Validate(timestampWithEvent).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(eventWithDeadline).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.Validate(alreadyDuePublication).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(invalidMode).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(timestampIdentityWithAuthentication).IsValid);
    }

    [Fact]
    public void Exact_coordinate_bounds_are_accepted_and_limit_plus_one_is_rejected()
    {
        var exact = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(
                frontierVersion: GovernedLoopSleepContractLimits.MaxVersion,
                activationOrdinal: GovernedLoopExecutionLimits.MaxFrontierNodes - 1,
                cycleId: "cycle-1",
                cycleIteration: GovernedLoopExecutionLimits.MaxCycleIterations,
                nodeVisitOrdinal: GovernedLoopExecutionLimits.MaxNodeVisits,
                waitAttempt: GovernedLoopSleepContractLimits.MaxWaitAttempt));
        var tooLarge = new[]
        {
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(frontierVersion: GovernedLoopSleepContractLimits.MaxVersion + 1)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(activationOrdinal: GovernedLoopExecutionLimits.MaxFrontierNodes)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(cycleId: "cycle-1", cycleIteration: GovernedLoopExecutionLimits.MaxCycleIterations + 1)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(nodeVisitOrdinal: GovernedLoopExecutionLimits.MaxNodeVisits + 1)),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(GovernedLoopSleepContractTestFixture.Binding(waitAttempt: GovernedLoopSleepContractLimits.MaxWaitAttempt + 1))
        };
        var missingCycleIteration = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(cycleId: "cycle-1"));
        var missingCycleIdentity = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(cycleIteration: 1));
        var overlongEventReference = new GovernedLoopSleepCheckpoint(
            1,
            GovernedLoopSleepContractTestFixture.Hash('d'),
            GovernedLoopSleepContractTestFixture.Binding(),
            GovernedLoopWakeMode.AuthenticatedEvent,
            null,
            new string('a', GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters + 1),
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            GovernedLoopSleepContractTestFixture.Hash('e'));

        Assert.True(GovernedLoopSleepContractValidator.Validate(exact).IsValid);
        Assert.All(tooLarge, checkpoint => Assert.Contains(
            GovernedLoopSleepContractValidator.Validate(checkpoint).Errors,
            error => error.Code == GovernedLoopSleepValidationErrorCode.LimitExceeded));
        Assert.False(GovernedLoopSleepContractValidator.Validate(missingCycleIteration).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(missingCycleIdentity).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate(overlongEventReference).IsValid);
    }

    [Fact]
    public void Very_over_limit_hash_input_is_rejected_before_integrity_recomputation()
    {
        var massiveNodeId = new string('a', 1_000_000);
        var checkpoint = new GovernedLoopSleepCheckpoint(
            1,
            GovernedLoopSleepContractTestFixture.Hash('d'),
            GovernedLoopSleepContractTestFixture.Binding(nodeId: massiveNodeId),
            GovernedLoopWakeMode.Timestamp,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(1),
            null,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            GovernedLoopSleepContractTestFixture.Hash('e'));
        var identity = new GovernedLoopWakeIdentity(
            1,
            GovernedLoopSleepContractTestFixture.Hash('a'),
            GovernedLoopSleepContractTestFixture.Hash('b'),
            GovernedLoopSleepContractTestFixture.Hash('c'),
            GovernedLoopWakeMode.AuthenticatedEvent,
            massiveNodeId,
            GovernedLoopSleepContractTestFixture.Hash('d'),
            GovernedLoopSleepContractTestFixture.Hash('e'));

        var validation = GovernedLoopSleepContractValidator.Validate(checkpoint);
        var identityValidation = GovernedLoopSleepContractValidator.Validate(identity);
        var checkpointException = Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopSleepContractHash.ComputeCheckpointId(checkpoint));
        var identityException = Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopSleepContractHash.ComputeWakeId(identity));

        Assert.Contains(validation.Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidIdentity);
        Assert.DoesNotContain(validation.Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IntegrityMismatch);
        Assert.Contains(identityValidation.Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidIdentity);
        Assert.DoesNotContain(identityValidation.Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.IntegrityMismatch);
        Assert.Equal("checkpoint", checkpointException.ParamName);
        Assert.Equal("identity", identityException.ParamName);
        Assert.False(GovernedLoopSleepContractHash.Matches(checkpoint));
        Assert.False(GovernedLoopSleepContractHash.Matches(identity));
    }

    [Fact]
    public void Wake_composition_rejects_checkpoint_generation_visit_and_mode_substitution()
    {
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var generation = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(executionGeneration: 2));
        var visit = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(nodeVisitOrdinal: 2));
        var eventCheckpoint = GovernedLoopSleepContractTestFixture.EventCheckpoint();

        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, identity).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.ValidateComposition(generation, identity).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.ValidateComposition(visit, identity).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.ValidateComposition(eventCheckpoint, identity).IsValid);
    }

    [Fact]
    public void Hashes_reject_tampering_null_and_noncanonical_content()
    {
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);

        Assert.False(GovernedLoopSleepContractHash.Matches(checkpoint with { ContentHash = GovernedLoopSleepContractTestFixture.Hash('f') }));
        Assert.False(GovernedLoopSleepContractHash.Matches(checkpoint with { CheckpointId = GovernedLoopSleepContractTestFixture.Hash('f') }));
        Assert.False(GovernedLoopSleepContractHash.Matches(identity with { WakeId = GovernedLoopSleepContractTestFixture.Hash('f') }));
        Assert.False(GovernedLoopSleepContractHash.Matches((GovernedLoopSleepCheckpoint?)null));
        Assert.False(GovernedLoopSleepContractHash.Matches((GovernedLoopWakeIdentity?)null));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.Compute((GovernedLoopSleepCheckpoint)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.Apply((GovernedLoopSleepCheckpoint)null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.ComputeCheckpointId(null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopSleepContractHash.ComputeWakeId(null!));
    }

    [Fact]
    public void Public_records_defensively_copy_exact_nested_bindings()
    {
        var binding = GovernedLoopSleepContractTestFixture.Binding();
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(binding);
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var evidence = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);

        Assert.NotSame(binding, checkpoint.Binding);
        Assert.NotSame(binding.Execution, checkpoint.Binding.Execution);
        Assert.NotSame(binding.Publication, checkpoint.Binding.Publication);
        Assert.NotSame(identity, evidence.Identity);
        Assert.Equal(binding, checkpoint.Binding);
        Assert.Equal(identity, evidence.Identity);
    }

    [Fact]
    public void Validation_errors_are_bounded_value_free_and_have_value_semantics()
    {
        var invalid = GovernedLoopSleepContractTestFixture.TimestampCheckpoint() with { ContentHash = "SECRET-value" };
        var first = Assert.Single(
            GovernedLoopSleepContractValidator.Validate(invalid).Errors,
            error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidHash);
        var second = Assert.Single(
            GovernedLoopSleepContractValidator.Validate(invalid).Errors,
            error => error.Code == GovernedLoopSleepValidationErrorCode.InvalidHash);

        Assert.Equal(first, second);
        Assert.True(first.Equals(second));
        Assert.False(first.Equals(null));
        Assert.False(first.Equals(new object()));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal("InvalidHash at $.contentHash", first.ToString());
        Assert.DoesNotContain("SECRET", first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", first.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopSleepValidationError>)GovernedLoopSleepContractValidator.Validate(invalid).Errors).Clear());
        Assert.False(GovernedLoopSleepContractValidator.Validate((GovernedLoopSleepCheckpoint?)null).IsValid);
        Assert.False(GovernedLoopSleepContractValidator.Validate((GovernedLoopWakeIdentity?)null).IsValid);
    }

    [Fact]
    public void Null_nested_binding_identity_and_publication_revision_return_structured_errors()
    {
        var hashes = GovernedLoopSleepContractTestFixture.Hash('f');
        var nullBinding = new GovernedLoopSleepCheckpoint(
            1,
            hashes,
            null!,
            GovernedLoopWakeMode.Timestamp,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(1),
            null,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            hashes);
        var validBinding = GovernedLoopSleepContractTestFixture.Binding();
        var malformedPublication = new GovernedLoopRevisionPublicationPin(
            1,
            null!,
            validBinding.Publication.PublicationOperationId,
            validBinding.Publication.ValidationEvidenceHash);
        var malformedBinding = new GovernedLoopSleepBinding(
            validBinding.Execution,
            malformedPublication,
            validBinding.FrontierVersion,
            validBinding.FrontierHash,
            validBinding.ActivationOrdinal,
            validBinding.CycleId,
            validBinding.CycleIteration,
            validBinding.NodeId,
            validBinding.NodeVisitOrdinal,
            validBinding.WaitAttempt,
            validBinding.WaitOperationId);
        var nullRevision = new GovernedLoopSleepCheckpoint(
            1,
            hashes,
            malformedBinding,
            GovernedLoopWakeMode.Timestamp,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddHours(1),
            null,
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            hashes);
        var nullIdentity = new GovernedLoopWakeEvidence(
            1,
            1,
            null!,
            GovernedLoopWakeDisposition.Stale,
            null,
            null,
            "stale-evidence-1",
            GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            hashes);

        Assert.Contains(GovernedLoopSleepContractValidator.Validate(nullBinding).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.Required);
        Assert.Contains(GovernedLoopSleepContractValidator.Validate(nullRevision).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.BindingMismatch);
        Assert.Contains(GovernedLoopSleepContractValidator.Validate(nullIdentity).Errors, error => error.Code == GovernedLoopSleepValidationErrorCode.Required);
    }
}
