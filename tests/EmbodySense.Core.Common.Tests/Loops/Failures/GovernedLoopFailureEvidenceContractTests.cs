using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.Failures;

public sealed class GovernedLoopFailureEvidenceContractTests
{
    [Fact]
    public void Contract_hashes_copies_and_defensively_snapshots_exact_failure_evidence()
    {
        var source = new[]
        {
            new GovernedLoopFailureEvidenceReference("evidence-a", new string('a', 64)),
            new GovernedLoopFailureEvidenceReference("evidence-b", new string('b', 64)),
        };

        var evidence = Create(causalEvidence: source);
        source[0] = new GovernedLoopFailureEvidenceReference("changed", new string('c', 64));
        var copy = GovernedLoopFailureEvidenceContract.Copy(evidence);

        Assert.True(GovernedLoopFailureEvidenceContract.IsValid(evidence));
        Assert.Equal(64, evidence.ContentHash.Length);
        Assert.Equal("evidence-a", evidence.CausalEvidence[0].EvidenceId);
        Assert.Equal(evidence.ContentHash, copy.ContentHash);
        Assert.Equal(evidence.CausalEvidence, copy.CausalEvidence);
        Assert.NotSame(evidence.CausalEvidence, copy.CausalEvidence);
    }

    [Fact]
    public void Contract_rejects_unknown_taxonomy_malformed_bounds_and_secret_bearing_detail()
    {
        var valid = Create();
        var candidates = new[]
        {
            valid with { FailureClass = (GovernedLoopFailureClass)int.MaxValue },
            valid with { Source = GovernedLoopFailureSource.Unknown },
            valid with { EffectCertainty = (GovernedLoopFailureEffectCertainty)int.MaxValue },
            valid with { AuthorityPosture = (GovernedLoopFailureAuthorityPosture)int.MaxValue },
            valid with { HumanPosture = (GovernedLoopFailureHumanPosture)int.MaxValue },
            valid with { RetrySafety = (GovernedLoopFailureRetrySafety)int.MaxValue },
            valid with { Severity = GovernedLoopFailureSeverity.Unknown },
            valid with { EvidenceId = "" },
            valid with { WorkspaceId = "workspace-invalid" },
            valid with { RunId = "" },
            valid with { NodeId = "" },
            valid with { ServerCode = new string('a', GovernedLoopFailureEvidenceContract.MaxServerCodeCharacters + 1) },
            valid with { ServerCode = "Uppercase" },
            valid with { SafeDetail = new string('a', GovernedLoopFailureEvidenceContract.MaxSafeDetailCharacters + 1) },
            valid with { SafeDetail = "token=private-value" },
            valid with { SafeDetail = "private/path" },
            valid with { Precedence = GovernedLoopFailureEvidenceContract.MinPrecedence - 1 },
            valid with { Precedence = GovernedLoopFailureEvidenceContract.MaxPrecedence + 1 },
            valid with { ObservedAtUtc = default },
            valid with { ObservedAtUtc = valid.ObservedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
            valid with { ContentHash = new string('0', 64) },
        };

        Assert.All(candidates, candidate => Assert.False(GovernedLoopFailureEvidenceContract.IsValid(candidate)));
    }

    [Fact]
    public void Contract_accepts_every_exact_bound_and_rejects_each_bound_plus_one()
    {
        var exactReferences = Enumerable.Range(0, GovernedLoopFailureEvidenceContract.MaxCausalEvidenceReferences)
            .Select(index => new GovernedLoopFailureEvidenceReference($"evidence-{index:D2}", new string('a', 64)))
            .ToArray();
        var exact = GovernedLoopFailureEvidenceContract.Create(
            "failure-evidence",
            $"workspace-sha256:{new string('e', 64)}",
            "run-1",
            GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", new string('f', 64)),
            1,
            0,
            1,
            "node-1",
            1,
            GovernedLoopFailureClass.ValidationConfiguration,
            new string('a', GovernedLoopFailureEvidenceContract.MaxServerCodeCharacters),
            GovernedLoopFailureSource.Validation,
            GovernedLoopFailureEffectCertainty.NotApplicable,
            GovernedLoopFailureAuthorityPosture.NotApplicable,
            GovernedLoopFailureHumanPosture.None,
            GovernedLoopFailureRetrySafety.NotRetryable,
            GovernedLoopFailureSeverity.Error,
            GovernedLoopFailureEvidenceContract.MaxPrecedence,
            exactReferences,
            new string('b', GovernedLoopFailureEvidenceContract.MaxSafeDetailCharacters),
            DateTimeOffset.UnixEpoch);

        Assert.True(GovernedLoopFailureEvidenceContract.IsValid(exact));
        Assert.Throws<ArgumentException>(() => Create(causalEvidence: [.. exactReferences, new GovernedLoopFailureEvidenceReference("evidence-extra", new string('b', 64))]));
        Assert.Throws<ArgumentException>(() => GovernedLoopFailureEvidenceContract.Create(
            exact.EvidenceId,
            exact.WorkspaceId,
            exact.RunId,
            exact.Revision,
            exact.ExecutionGeneration,
            exact.ActivationOrdinal,
            exact.VisitOrdinal,
            exact.NodeId,
            exact.Attempt,
            exact.FailureClass,
            new string('a', GovernedLoopFailureEvidenceContract.MaxServerCodeCharacters + 1),
            exact.Source,
            exact.EffectCertainty,
            exact.AuthorityPosture,
            exact.HumanPosture,
            exact.RetrySafety,
            exact.Severity,
            exact.Precedence,
            exact.CausalEvidence,
            exact.SafeDetail,
            exact.ObservedAtUtc));
        Assert.False(GovernedLoopFailureEvidenceContract.IsValid(exact with { SafeDetail = new string('b', GovernedLoopFailureEvidenceContract.MaxSafeDetailCharacters + 1) }));
    }

    [Fact]
    public void Contract_rejects_duplicate_unordered_conflicting_or_overbound_causal_references()
    {
        var duplicate = new[]
        {
            new GovernedLoopFailureEvidenceReference("evidence-a", new string('a', 64)),
            new GovernedLoopFailureEvidenceReference("evidence-a", new string('a', 64)),
        };
        var unordered = new[]
        {
            new GovernedLoopFailureEvidenceReference("evidence-b", new string('b', 64)),
            new GovernedLoopFailureEvidenceReference("evidence-a", new string('a', 64)),
        };
        var overbound = Enumerable.Range(0, GovernedLoopFailureEvidenceContract.MaxCausalEvidenceReferences + 1)
            .Select(index => new GovernedLoopFailureEvidenceReference($"evidence-{index:D2}", new string('a', 64)))
            .ToArray();

        Assert.Throws<ArgumentException>(() => Create(causalEvidence: duplicate));
        Assert.Throws<ArgumentException>(() => Create(causalEvidence: unordered));
        Assert.Throws<ArgumentException>(() => Create(causalEvidence: overbound));
    }

    [Fact]
    public void Contract_enforces_uncertainty_authority_human_and_retry_certainty_combinations()
    {
        var valid = Create();
        var candidates = new[]
        {
            valid with { FailureClass = GovernedLoopFailureClass.AmbiguousExternalOutcome },
            valid with { FailureClass = GovernedLoopFailureClass.EvidenceIntegrityFailure },
            valid with { FailureClass = GovernedLoopFailureClass.AuthorityPermissionDenied },
            valid with { FailureClass = GovernedLoopFailureClass.ReviewRejected },
            valid with { FailureClass = GovernedLoopFailureClass.UserPaused },
            valid with { FailureClass = GovernedLoopFailureClass.UserCancelled },
            valid with { RetrySafety = GovernedLoopFailureRetrySafety.RetryableWithExactIntent },
        };

        Assert.All(candidates, candidate => Assert.False(GovernedLoopFailureEvidenceContract.IsValid(candidate)));
        Assert.True(GovernedLoopFailureEvidenceContract.RequiresReview(Create(
            failureClass: GovernedLoopFailureClass.AmbiguousExternalOutcome,
            effectCertainty: GovernedLoopFailureEffectCertainty.Ambiguous,
            retrySafety: GovernedLoopFailureRetrySafety.Unknown,
            severity: GovernedLoopFailureSeverity.ReviewBlocked)));
        Assert.True(GovernedLoopFailureEvidenceContract.RequiresReview(Create(
            failureClass: GovernedLoopFailureClass.EvidenceIntegrityFailure,
            effectCertainty: GovernedLoopFailureEffectCertainty.Unknown,
            retrySafety: GovernedLoopFailureRetrySafety.Unknown,
            severity: GovernedLoopFailureSeverity.ReviewBlocked)));
    }

    private static GovernedLoopFailureEvidence Create(
        GovernedLoopFailureClass failureClass = GovernedLoopFailureClass.ValidationConfiguration,
        GovernedLoopFailureEffectCertainty effectCertainty = GovernedLoopFailureEffectCertainty.NotApplicable,
        GovernedLoopFailureRetrySafety retrySafety = GovernedLoopFailureRetrySafety.NotRetryable,
        GovernedLoopFailureSeverity severity = GovernedLoopFailureSeverity.Error,
        int? precedence = null,
        IReadOnlyList<GovernedLoopFailureEvidenceReference>? causalEvidence = null)
        => GovernedLoopFailureEvidenceContract.Create(
            "failure-evidence",
            $"workspace-sha256:{new string('e', 64)}",
            "run-1",
            GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", new string('f', 64)),
            1,
            0,
            1,
            "node-1",
            1,
            failureClass,
            "validation-rejected",
            GovernedLoopFailureSource.Validation,
            effectCertainty,
            GovernedLoopFailureAuthorityPosture.NotApplicable,
            GovernedLoopFailureHumanPosture.None,
            retrySafety,
            severity,
            precedence ?? (failureClass is GovernedLoopFailureClass.AmbiguousExternalOutcome or GovernedLoopFailureClass.EvidenceIntegrityFailure ? 1_000 : 700),
            causalEvidence ?? [new GovernedLoopFailureEvidenceReference("evidence-a", new string('a', 64))],
            "safe-detail",
            DateTimeOffset.UnixEpoch);
}
