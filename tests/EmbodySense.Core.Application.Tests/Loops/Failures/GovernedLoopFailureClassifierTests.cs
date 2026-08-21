using EmbodySense.Core.Application.Loops.Failures;
using EmbodySense.Core.Application.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Loops.Failures;

public sealed class GovernedLoopFailureClassifierTests
{
    private readonly GovernedLoopFailureClassifier _classifier = new();

    public static TheoryData<GovernedLoopFailureObservationKind, GovernedLoopFailureClass, GovernedLoopFailureClassificationStatus> Mappings
        => new()
        {
            { GovernedLoopFailureObservationKind.ValidationRejected, GovernedLoopFailureClass.ValidationConfiguration, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.AuthorityDenied, GovernedLoopFailureClass.AuthorityPermissionDenied, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.AuthorityRevoked, GovernedLoopFailureClass.AuthorityPermissionDenied, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.HumanReviewRejected, GovernedLoopFailureClass.ReviewRejected, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.DependencyUnavailable, GovernedLoopFailureClass.DependencyUnavailableBeforeDispatch, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.DispatchProvedNotStarted, GovernedLoopFailureClass.DispatchProvedNotStarted, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.RetryableNoEffect, GovernedLoopFailureClass.RetryableNoEffect, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.TerminalFailure, GovernedLoopFailureClass.TerminalFailure, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.TargetConflict, GovernedLoopFailureClass.TargetPreconditionConflict, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.TimeoutNoEffect, GovernedLoopFailureClass.TimeoutCancellationNoEffect, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.CancellationNoEffect, GovernedLoopFailureClass.TimeoutCancellationNoEffect, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.MalformedOutput, GovernedLoopFailureClass.MalformedPolicyInvalidOutput, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.PolicyInvalidOutput, GovernedLoopFailureClass.MalformedPolicyInvalidOutput, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.QuotaExhausted, GovernedLoopFailureClass.Exhaustion, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.DeadlineExhausted, GovernedLoopFailureClass.Exhaustion, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.IterationExhausted, GovernedLoopFailureClass.Exhaustion, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.CostExhausted, GovernedLoopFailureClass.Exhaustion, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.UserPaused, GovernedLoopFailureClass.UserPaused, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.UserCancelled, GovernedLoopFailureClass.UserCancelled, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.UnsupportedSchema, GovernedLoopFailureClass.UnsupportedSchemaCapability, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.UnsupportedCapability, GovernedLoopFailureClass.UnsupportedSchemaCapability, GovernedLoopFailureClassificationStatus.Classified },
            { GovernedLoopFailureObservationKind.AmbiguousOutcome, GovernedLoopFailureClass.AmbiguousExternalOutcome, GovernedLoopFailureClassificationStatus.ReviewBlocked },
            { GovernedLoopFailureObservationKind.PersistenceIntegrityFailure, GovernedLoopFailureClass.EvidenceIntegrityFailure, GovernedLoopFailureClassificationStatus.ReviewBlocked },
            { GovernedLoopFailureObservationKind.AuditIntegrityFailure, GovernedLoopFailureClass.EvidenceIntegrityFailure, GovernedLoopFailureClassificationStatus.ReviewBlocked },
            { GovernedLoopFailureObservationKind.EvidenceIntegrityFailure, GovernedLoopFailureClass.EvidenceIntegrityFailure, GovernedLoopFailureClassificationStatus.ReviewBlocked },
            { GovernedLoopFailureObservationKind.AgentSelectedFailure, GovernedLoopFailureClass.AgentSelectedFailure, GovernedLoopFailureClassificationStatus.Classified },
        };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Classifier_maps_every_closed_observation_kind(GovernedLoopFailureObservationKind kind, GovernedLoopFailureClass expectedClass, GovernedLoopFailureClassificationStatus expectedStatus)
    {
        var result = _classifier.Classify(Context(), [Observation(kind, "mapping-observation", 'b')], DateTimeOffset.UnixEpoch);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedClass, result.Evidence?.FailureClass);
        Assert.Equal(ExpectedSource(kind), result.Evidence?.Source);
        Assert.Equal(GovernedLoopFailureEvidence.CurrentMappingVersion, result.Evidence?.MappingVersion);
        Assert.Equal(ExpectedEffectCertainty(kind), result.Evidence?.EffectCertainty);
        Assert.Equal(ExpectedRetrySafety(kind), result.Evidence?.RetrySafety);
        Assert.Equal(ExpectedPrecedence(kind), result.Evidence?.Precedence);
        Assert.True(GovernedLoopFailureEvidenceContract.IsValid(result.Evidence));
    }

    [Fact]
    public void Classifier_applies_integrity_human_authority_exhaustion_then_specific_precedence()
    {
        var observations = new[]
        {
            Observation(GovernedLoopFailureObservationKind.DependencyUnavailable, "dependency-unavailable", 'a'),
            Observation(GovernedLoopFailureObservationKind.TerminalFailure, "terminal-failure", 'b'),
            Observation(GovernedLoopFailureObservationKind.QuotaExhausted, "quota-exhausted", 'c'),
            Observation(GovernedLoopFailureObservationKind.AuthorityDenied, "authority-denied", 'd'),
            Observation(GovernedLoopFailureObservationKind.UserPaused, "user-paused", 'e'),
            Observation(GovernedLoopFailureObservationKind.AmbiguousOutcome, "ambiguous-outcome", 'f'),
            Observation(GovernedLoopFailureObservationKind.EvidenceIntegrityFailure, "evidence-integrity", '0'),
        };

        var result = _classifier.Classify(Context(), observations, DateTimeOffset.UnixEpoch);

        Assert.Equal(GovernedLoopFailureClassificationStatus.ReviewBlocked, result.Status);
        Assert.Equal(GovernedLoopFailureClass.EvidenceIntegrityFailure, result.Evidence?.FailureClass);
        Assert.Equal("evidence-integrity", result.Evidence?.ServerCode);
        Assert.Equal(observations.Length, result.Evidence?.CausalEvidence.Count);
    }

    [Fact]
    public void Classifier_tie_breaks_deterministically_without_observation_order_authority()
    {
        var first = Observation(GovernedLoopFailureObservationKind.ValidationRejected, "z-validation", 'b');
        var second = Observation(GovernedLoopFailureObservationKind.ValidationRejected, "a-validation", 'a');

        var forward = _classifier.Classify(Context(), [first, second], DateTimeOffset.UnixEpoch);
        var reverse = _classifier.Classify(Context(), [second, first], DateTimeOffset.UnixEpoch);

        Assert.Equal("a-validation", forward.Evidence?.ServerCode);
        Assert.Equal(forward.Evidence?.ContentHash, reverse.Evidence?.ContentHash);
    }

    [Theory]
    [InlineData(GovernedLoopFailureObservationKind.TerminalFailure, GovernedLoopFailureSource.Provider)]
    [InlineData(GovernedLoopFailureObservationKind.TerminalFailure, GovernedLoopFailureSource.Actuator)]
    [InlineData(GovernedLoopFailureObservationKind.DispatchProvedNotStarted, GovernedLoopFailureSource.Workspace)]
    [InlineData(GovernedLoopFailureObservationKind.DeadlineExhausted, GovernedLoopFailureSource.Wait)]
    [InlineData(GovernedLoopFailureObservationKind.AmbiguousOutcome, GovernedLoopFailureSource.Provider)]
    public void Classifier_preserves_the_exact_observing_subsystem_for_generic_kinds(GovernedLoopFailureObservationKind kind, GovernedLoopFailureSource source)
    {
        var observation = new GovernedLoopFailureObservation(kind, source, "subsystem-observation", Reference("subsystem-evidence", '9'));

        var result = _classifier.Classify(Context(), [observation], DateTimeOffset.UnixEpoch);

        Assert.Equal(source, result.Evidence?.Source);
        Assert.True(GovernedLoopFailureEvidenceContract.IsValid(result.Evidence));
    }

    [Fact]
    public void Classifier_tie_breaks_equal_generic_observations_by_exact_source()
    {
        var provider = new GovernedLoopFailureObservation(GovernedLoopFailureObservationKind.TerminalFailure, GovernedLoopFailureSource.Provider, "same-code", Reference("provider-evidence", '7'));
        var actuator = new GovernedLoopFailureObservation(GovernedLoopFailureObservationKind.TerminalFailure, GovernedLoopFailureSource.Actuator, "same-code", Reference("actuator-evidence", '8'));

        var forward = _classifier.Classify(Context(), [provider, actuator], DateTimeOffset.UnixEpoch);
        var reverse = _classifier.Classify(Context(), [actuator, provider], DateTimeOffset.UnixEpoch);

        Assert.Equal(forward.Evidence?.ContentHash, reverse.Evidence?.ContentHash);
        Assert.Equal(GovernedLoopFailureSource.Provider, forward.Evidence?.Source);
    }

    [Fact]
    public void Classifier_is_order_independent_idempotent_and_associative_for_compatible_observations()
    {
        var authority = Observation(GovernedLoopFailureObservationKind.AuthorityDenied, "authority-denied", 'a');
        var dependency = Observation(GovernedLoopFailureObservationKind.DependencyUnavailable, "dependency-unavailable", 'b');
        var retryable = Observation(GovernedLoopFailureObservationKind.RetryableNoEffect, "retryable-no-effect", 'c');
        var expected = _classifier.Classify(Context(), [authority, dependency, retryable], DateTimeOffset.UnixEpoch);
        var equivalentAssociations = new[]
        {
            new[] { authority }.Concat([dependency, retryable]).ToArray(),
            new[] { authority, dependency }.Concat([retryable]).ToArray(),
            new[] { retryable, authority, dependency },
            new[] { dependency, retryable, authority },
        };

        Assert.All(equivalentAssociations, observations => Assert.Equal(expected.Evidence?.ContentHash, _classifier.Classify(Context(), observations, DateTimeOffset.UnixEpoch).Evidence?.ContentHash));
        var single = _classifier.Classify(Context(), [authority], DateTimeOffset.UnixEpoch);
        var duplicate = _classifier.Classify(Context(), [authority, authority], DateTimeOffset.UnixEpoch);
        Assert.Equal(single.Evidence?.ContentHash, duplicate.Evidence?.ContentHash);
        Assert.Single(duplicate.Evidence!.CausalEvidence);
    }

    [Fact]
    public void Classifier_fails_closed_for_conflicting_or_malformed_evidence()
    {
        var conflicting = new[]
        {
            new GovernedLoopFailureObservation(GovernedLoopFailureObservationKind.ValidationRejected, GovernedLoopFailureSource.Validation, "validation-one", Reference("shared", 'a')),
            new GovernedLoopFailureObservation(GovernedLoopFailureObservationKind.TerminalFailure, GovernedLoopFailureSource.Provider, "terminal-two", Reference("shared", 'b')),
        };
        var conflictResult = _classifier.Classify(Context(), conflicting, DateTimeOffset.UnixEpoch);
        var malformedResult = _classifier.Classify(Context(), [Observation(GovernedLoopFailureObservationKind.Unknown, "unknown", 'a')], DateTimeOffset.UnixEpoch);

        Assert.Equal(GovernedLoopFailureClassificationStatus.ReviewBlocked, conflictResult.Status);
        Assert.Equal(GovernedLoopFailureClass.EvidenceIntegrityFailure, conflictResult.Evidence?.FailureClass);
        Assert.Equal(GovernedLoopFailureClassificationStatus.ReviewBlocked, malformedResult.Status);
        Assert.Equal(GovernedLoopFailureClass.EvidenceIntegrityFailure, malformedResult.Evidence?.FailureClass);
    }

    [Fact]
    public void Classifier_returns_invalid_when_exact_context_cannot_be_authenticated()
    {
        var result = _classifier.Classify(Context() with { WorkspaceId = "not-a-workspace-id" }, [Observation(GovernedLoopFailureObservationKind.ValidationRejected, "validation", 'a')], DateTimeOffset.UnixEpoch);

        Assert.Equal(GovernedLoopFailureClassificationStatus.Invalid, result.Status);
        Assert.Null(result.Evidence);
    }

    internal static GovernedLoopFailureClassificationContext Context()
        => new(
            "failure-evidence",
            $"workspace-sha256:{new string('e', 64)}",
            "run-1",
            GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", new string('f', 64)),
            1,
            0,
            1,
            "node-1",
            1,
            Reference("classification-boundary", '0'));

    internal static GovernedLoopFailureObservation Observation(GovernedLoopFailureObservationKind kind, string code, char hashCharacter)
        => new(kind, ExpectedSource(kind), code, Reference($"evidence-{hashCharacter}", hashCharacter));

    private static GovernedLoopFailureSource ExpectedSource(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.ValidationRejected or GovernedLoopFailureObservationKind.UnsupportedSchema => GovernedLoopFailureSource.Validation,
            GovernedLoopFailureObservationKind.AuthorityDenied or GovernedLoopFailureObservationKind.AuthorityRevoked => GovernedLoopFailureSource.Authority,
            GovernedLoopFailureObservationKind.HumanReviewRejected => GovernedLoopFailureSource.HumanReview,
            GovernedLoopFailureObservationKind.DependencyUnavailable or GovernedLoopFailureObservationKind.UnsupportedCapability => GovernedLoopFailureSource.Dependency,
            GovernedLoopFailureObservationKind.TargetConflict => GovernedLoopFailureSource.Workspace,
            GovernedLoopFailureObservationKind.MalformedOutput or GovernedLoopFailureObservationKind.PolicyInvalidOutput or GovernedLoopFailureObservationKind.TerminalFailure => GovernedLoopFailureSource.Provider,
            GovernedLoopFailureObservationKind.UserPaused or GovernedLoopFailureObservationKind.UserCancelled => GovernedLoopFailureSource.User,
            GovernedLoopFailureObservationKind.PersistenceIntegrityFailure => GovernedLoopFailureSource.Persistence,
            GovernedLoopFailureObservationKind.AuditIntegrityFailure => GovernedLoopFailureSource.Audit,
            GovernedLoopFailureObservationKind.EvidenceIntegrityFailure => GovernedLoopFailureSource.Evidence,
            GovernedLoopFailureObservationKind.AgentSelectedFailure => GovernedLoopFailureSource.Agent,
            _ => GovernedLoopFailureSource.Runtime,
        };

    private static GovernedLoopFailureEvidenceReference Reference(string id, char hashCharacter)
        => new(id, new string(hashCharacter, 64));

    private static GovernedLoopFailureEffectCertainty ExpectedEffectCertainty(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.AmbiguousOutcome => GovernedLoopFailureEffectCertainty.Ambiguous,
            GovernedLoopFailureObservationKind.PersistenceIntegrityFailure or GovernedLoopFailureObservationKind.AuditIntegrityFailure or GovernedLoopFailureObservationKind.EvidenceIntegrityFailure => GovernedLoopFailureEffectCertainty.Unknown,
            GovernedLoopFailureObservationKind.DependencyUnavailable or GovernedLoopFailureObservationKind.DispatchProvedNotStarted or GovernedLoopFailureObservationKind.AuthorityDenied or GovernedLoopFailureObservationKind.AuthorityRevoked or GovernedLoopFailureObservationKind.HumanReviewRejected or GovernedLoopFailureObservationKind.UnsupportedCapability => GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted,
            GovernedLoopFailureObservationKind.RetryableNoEffect or GovernedLoopFailureObservationKind.TargetConflict or GovernedLoopFailureObservationKind.TimeoutNoEffect or GovernedLoopFailureObservationKind.CancellationNoEffect => GovernedLoopFailureEffectCertainty.EffectProvedAbsent,
            GovernedLoopFailureObservationKind.TerminalFailure or GovernedLoopFailureObservationKind.MalformedOutput or GovernedLoopFailureObservationKind.PolicyInvalidOutput => GovernedLoopFailureEffectCertainty.EffectProvedCommitted,
            _ => GovernedLoopFailureEffectCertainty.NotApplicable,
        };

    private static GovernedLoopFailureRetrySafety ExpectedRetrySafety(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.DependencyUnavailable or GovernedLoopFailureObservationKind.DispatchProvedNotStarted or GovernedLoopFailureObservationKind.RetryableNoEffect or GovernedLoopFailureObservationKind.TimeoutNoEffect or GovernedLoopFailureObservationKind.CancellationNoEffect => GovernedLoopFailureRetrySafety.RetryableWithExactIntent,
            GovernedLoopFailureObservationKind.AmbiguousOutcome or GovernedLoopFailureObservationKind.PersistenceIntegrityFailure or GovernedLoopFailureObservationKind.AuditIntegrityFailure or GovernedLoopFailureObservationKind.EvidenceIntegrityFailure => GovernedLoopFailureRetrySafety.Unknown,
            _ => GovernedLoopFailureRetrySafety.NotRetryable,
        };

    private static int ExpectedPrecedence(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.EvidenceIntegrityFailure => 1_000,
            GovernedLoopFailureObservationKind.AuditIntegrityFailure => 999,
            GovernedLoopFailureObservationKind.PersistenceIntegrityFailure => 998,
            GovernedLoopFailureObservationKind.AmbiguousOutcome => 990,
            GovernedLoopFailureObservationKind.UserCancelled => 950,
            GovernedLoopFailureObservationKind.UserPaused => 940,
            GovernedLoopFailureObservationKind.HumanReviewRejected => 930,
            GovernedLoopFailureObservationKind.AuthorityRevoked => 920,
            GovernedLoopFailureObservationKind.AuthorityDenied => 910,
            GovernedLoopFailureObservationKind.CostExhausted => 850,
            GovernedLoopFailureObservationKind.DeadlineExhausted => 849,
            GovernedLoopFailureObservationKind.IterationExhausted => 848,
            GovernedLoopFailureObservationKind.QuotaExhausted => 847,
            GovernedLoopFailureObservationKind.TerminalFailure => 800,
            GovernedLoopFailureObservationKind.AgentSelectedFailure => 790,
            GovernedLoopFailureObservationKind.UnsupportedSchema => 750,
            GovernedLoopFailureObservationKind.UnsupportedCapability => 749,
            GovernedLoopFailureObservationKind.ValidationRejected => 700,
            GovernedLoopFailureObservationKind.TargetConflict => 690,
            GovernedLoopFailureObservationKind.PolicyInvalidOutput => 680,
            GovernedLoopFailureObservationKind.MalformedOutput => 679,
            GovernedLoopFailureObservationKind.CancellationNoEffect => 650,
            GovernedLoopFailureObservationKind.TimeoutNoEffect => 649,
            GovernedLoopFailureObservationKind.RetryableNoEffect => 640,
            GovernedLoopFailureObservationKind.DispatchProvedNotStarted => 630,
            GovernedLoopFailureObservationKind.DependencyUnavailable => 620,
            _ => 0,
        };
}
