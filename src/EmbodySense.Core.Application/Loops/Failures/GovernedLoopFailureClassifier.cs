using EmbodySense.Core.Application.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Failures;

/// <summary>Applies the immutable schema-1 failure mapping and precedence lattice.</summary>
public sealed class GovernedLoopFailureClassifier : IGovernedLoopFailureClassifier
{
    /// <summary>Gets the maximum number of observations accepted by one classification.</summary>
    public const int MaxObservations = GovernedLoopFailureEvidenceContract.MaxCausalEvidenceReferences;

    /// <inheritdoc />
    public GovernedLoopFailureClassificationResult Classify(GovernedLoopFailureClassificationContext context, IReadOnlyList<GovernedLoopFailureObservation> observations, DateTimeOffset observedAtUtc)
    {
        if (!TryValidateContext(context, observedAtUtc))
        {
            return new GovernedLoopFailureClassificationResult(GovernedLoopFailureClassificationStatus.Invalid, null, "classification-context-invalid");
        }

        GovernedLoopFailureObservation[] snapshot;
        try
        {
            if (observations is null || observations.Count is < 1 or > MaxObservations)
            {
                return Integrity(context, observedAtUtc, "classification-observation-count-invalid");
            }
            snapshot = observations.Take(MaxObservations + 1).ToArray();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return Integrity(context, observedAtUtc, "classification-observation-read-failed");
        }

        if (snapshot.Length != observations.Count || snapshot.Any(ObservationIsMalformed))
        {
            return Integrity(context, observedAtUtc, "classification-observation-invalid");
        }

        var causalEvidence = snapshot
            .Select(item => item.CausalEvidence)
            .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceHash, StringComparer.Ordinal)
            .Distinct()
            .ToArray();
        if (causalEvidence.GroupBy(item => item.EvidenceId, StringComparer.Ordinal).Any(group => group.Select(item => item.EvidenceHash).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            return Integrity(context, observedAtUtc, "classification-evidence-conflict", causalEvidence);
        }

        var winner = snapshot
            .OrderByDescending(item => Precedence(item.Kind))
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Source)
            .ThenBy(item => item.ServerCode, StringComparer.Ordinal)
            .ThenBy(item => item.SafeDetail, StringComparer.Ordinal)
            .ThenBy(item => item.CausalEvidence.EvidenceId, StringComparer.Ordinal)
            .ThenBy(item => item.CausalEvidence.EvidenceHash, StringComparer.Ordinal)
            .First();
        var evidence = GovernedLoopFailureEvidenceContract.Create(
            context.FailureEvidenceId,
            context.WorkspaceId,
            context.RunId,
            context.Revision,
            context.ExecutionGeneration,
            context.ActivationOrdinal,
            context.VisitOrdinal,
            context.NodeId,
            context.Attempt,
            FailureClass(winner.Kind),
            winner.ServerCode,
            winner.Source,
            EffectCertainty(winner.Kind),
            AuthorityPosture(winner.Kind),
            HumanPosture(winner.Kind),
            RetrySafety(winner.Kind),
            Severity(winner.Kind),
            Precedence(winner.Kind),
            causalEvidence,
            winner.SafeDetail,
            observedAtUtc);
        var status = GovernedLoopFailureEvidenceContract.RequiresReview(evidence)
            ? GovernedLoopFailureClassificationStatus.ReviewBlocked
            : GovernedLoopFailureClassificationStatus.Classified;
        return new GovernedLoopFailureClassificationResult(status, evidence, "classification-complete");
    }

    private static bool TryValidateContext(GovernedLoopFailureClassificationContext? context, DateTimeOffset observedAtUtc)
    {
        if (context?.ClassificationBoundaryEvidence is null)
        {
            return false;
        }
        try
        {
            _ = GovernedLoopFailureEvidenceContract.Create(
                context.FailureEvidenceId,
                context.WorkspaceId,
                context.RunId,
                context.Revision,
                context.ExecutionGeneration,
                context.ActivationOrdinal,
                context.VisitOrdinal,
                context.NodeId,
                context.Attempt,
                GovernedLoopFailureClass.EvidenceIntegrityFailure,
                "classification-context-probe",
                GovernedLoopFailureSource.Evidence,
                GovernedLoopFailureEffectCertainty.Unknown,
                GovernedLoopFailureAuthorityPosture.Unknown,
                GovernedLoopFailureHumanPosture.Unknown,
                GovernedLoopFailureRetrySafety.Unknown,
                GovernedLoopFailureSeverity.ReviewBlocked,
                Precedence(GovernedLoopFailureObservationKind.EvidenceIntegrityFailure),
                [context.ClassificationBoundaryEvidence],
                null,
                observedAtUtc);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static GovernedLoopFailureClassificationResult Integrity(
        GovernedLoopFailureClassificationContext context,
        DateTimeOffset observedAtUtc,
        string serverCode,
        IEnumerable<GovernedLoopFailureEvidenceReference>? observedEvidence = null)
    {
        var references = (observedEvidence ?? [])
            .Append(context.ClassificationBoundaryEvidence)
            .Where(item => item is not null)
            .GroupBy(item => item.EvidenceId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.EvidenceHash, StringComparer.Ordinal).First())
            .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
            .Take(GovernedLoopFailureEvidenceContract.MaxCausalEvidenceReferences)
            .ToArray();
        try
        {
            var evidence = GovernedLoopFailureEvidenceContract.Create(
                context.FailureEvidenceId,
                context.WorkspaceId,
                context.RunId,
                context.Revision,
                context.ExecutionGeneration,
                context.ActivationOrdinal,
                context.VisitOrdinal,
                context.NodeId,
                context.Attempt,
                GovernedLoopFailureClass.EvidenceIntegrityFailure,
                serverCode,
                GovernedLoopFailureSource.Evidence,
                GovernedLoopFailureEffectCertainty.Unknown,
                GovernedLoopFailureAuthorityPosture.Unknown,
                GovernedLoopFailureHumanPosture.Unknown,
                GovernedLoopFailureRetrySafety.Unknown,
                GovernedLoopFailureSeverity.ReviewBlocked,
                Precedence(GovernedLoopFailureObservationKind.EvidenceIntegrityFailure),
                references,
                null,
                observedAtUtc);
            return new GovernedLoopFailureClassificationResult(GovernedLoopFailureClassificationStatus.ReviewBlocked, evidence, serverCode);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new GovernedLoopFailureClassificationResult(GovernedLoopFailureClassificationStatus.Invalid, null, "classification-integrity-evidence-invalid");
        }
    }

    private static bool ObservationIsMalformed(GovernedLoopFailureObservation? observation)
    {
        if (observation?.CausalEvidence is null
            || observation.Kind == GovernedLoopFailureObservationKind.Unknown
            || !Enum.IsDefined(observation.Kind)
            || observation.Source == GovernedLoopFailureSource.Unknown
            || !Enum.IsDefined(observation.Source))
        {
            return true;
        }
        try
        {
            _ = GovernedLoopFailureEvidenceContract.Create(
                "classification-observation-probe",
                $"workspace-sha256:{new string('a', 64)}",
                "classification-run",
                GovernedLoopRevisionReference.Create(1, "classification-graph", "classification-revision", new string('a', 64)),
                1,
                0,
                1,
                "classification-node",
                1,
                FailureClass(observation.Kind),
                observation.ServerCode,
                observation.Source,
                EffectCertainty(observation.Kind),
                AuthorityPosture(observation.Kind),
                HumanPosture(observation.Kind),
                RetrySafety(observation.Kind),
                Severity(observation.Kind),
                Precedence(observation.Kind),
                [observation.CausalEvidence],
                observation.SafeDetail,
                DateTimeOffset.UnixEpoch);
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }

    private static int Precedence(GovernedLoopFailureObservationKind kind)
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

    private static GovernedLoopFailureClass FailureClass(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.ValidationRejected => GovernedLoopFailureClass.ValidationConfiguration,
            GovernedLoopFailureObservationKind.AuthorityDenied or GovernedLoopFailureObservationKind.AuthorityRevoked => GovernedLoopFailureClass.AuthorityPermissionDenied,
            GovernedLoopFailureObservationKind.HumanReviewRejected => GovernedLoopFailureClass.ReviewRejected,
            GovernedLoopFailureObservationKind.DependencyUnavailable => GovernedLoopFailureClass.DependencyUnavailableBeforeDispatch,
            GovernedLoopFailureObservationKind.DispatchProvedNotStarted => GovernedLoopFailureClass.DispatchProvedNotStarted,
            GovernedLoopFailureObservationKind.RetryableNoEffect => GovernedLoopFailureClass.RetryableNoEffect,
            GovernedLoopFailureObservationKind.TerminalFailure => GovernedLoopFailureClass.TerminalFailure,
            GovernedLoopFailureObservationKind.TargetConflict => GovernedLoopFailureClass.TargetPreconditionConflict,
            GovernedLoopFailureObservationKind.TimeoutNoEffect or GovernedLoopFailureObservationKind.CancellationNoEffect => GovernedLoopFailureClass.TimeoutCancellationNoEffect,
            GovernedLoopFailureObservationKind.MalformedOutput or GovernedLoopFailureObservationKind.PolicyInvalidOutput => GovernedLoopFailureClass.MalformedPolicyInvalidOutput,
            GovernedLoopFailureObservationKind.QuotaExhausted or GovernedLoopFailureObservationKind.DeadlineExhausted or GovernedLoopFailureObservationKind.IterationExhausted or GovernedLoopFailureObservationKind.CostExhausted => GovernedLoopFailureClass.Exhaustion,
            GovernedLoopFailureObservationKind.UserPaused => GovernedLoopFailureClass.UserPaused,
            GovernedLoopFailureObservationKind.UserCancelled => GovernedLoopFailureClass.UserCancelled,
            GovernedLoopFailureObservationKind.UnsupportedSchema or GovernedLoopFailureObservationKind.UnsupportedCapability => GovernedLoopFailureClass.UnsupportedSchemaCapability,
            GovernedLoopFailureObservationKind.AmbiguousOutcome => GovernedLoopFailureClass.AmbiguousExternalOutcome,
            GovernedLoopFailureObservationKind.PersistenceIntegrityFailure or GovernedLoopFailureObservationKind.AuditIntegrityFailure or GovernedLoopFailureObservationKind.EvidenceIntegrityFailure => GovernedLoopFailureClass.EvidenceIntegrityFailure,
            GovernedLoopFailureObservationKind.AgentSelectedFailure => GovernedLoopFailureClass.AgentSelectedFailure,
            _ => GovernedLoopFailureClass.Unknown,
        };

    private static GovernedLoopFailureEffectCertainty EffectCertainty(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.AmbiguousOutcome => GovernedLoopFailureEffectCertainty.Ambiguous,
            GovernedLoopFailureObservationKind.PersistenceIntegrityFailure or GovernedLoopFailureObservationKind.AuditIntegrityFailure or GovernedLoopFailureObservationKind.EvidenceIntegrityFailure => GovernedLoopFailureEffectCertainty.Unknown,
            GovernedLoopFailureObservationKind.DependencyUnavailable or GovernedLoopFailureObservationKind.DispatchProvedNotStarted or GovernedLoopFailureObservationKind.AuthorityDenied or GovernedLoopFailureObservationKind.AuthorityRevoked or GovernedLoopFailureObservationKind.HumanReviewRejected or GovernedLoopFailureObservationKind.UnsupportedCapability => GovernedLoopFailureEffectCertainty.DispatchProvedNotStarted,
            GovernedLoopFailureObservationKind.RetryableNoEffect or GovernedLoopFailureObservationKind.TargetConflict or GovernedLoopFailureObservationKind.TimeoutNoEffect or GovernedLoopFailureObservationKind.CancellationNoEffect => GovernedLoopFailureEffectCertainty.EffectProvedAbsent,
            GovernedLoopFailureObservationKind.TerminalFailure or GovernedLoopFailureObservationKind.MalformedOutput or GovernedLoopFailureObservationKind.PolicyInvalidOutput => GovernedLoopFailureEffectCertainty.EffectProvedCommitted,
            _ => GovernedLoopFailureEffectCertainty.NotApplicable,
        };

    private static GovernedLoopFailureAuthorityPosture AuthorityPosture(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.AuthorityDenied => GovernedLoopFailureAuthorityPosture.Denied,
            GovernedLoopFailureObservationKind.AuthorityRevoked => GovernedLoopFailureAuthorityPosture.Revoked,
            GovernedLoopFailureObservationKind.PersistenceIntegrityFailure or GovernedLoopFailureObservationKind.AuditIntegrityFailure or GovernedLoopFailureObservationKind.EvidenceIntegrityFailure or GovernedLoopFailureObservationKind.AmbiguousOutcome => GovernedLoopFailureAuthorityPosture.Unknown,
            _ => GovernedLoopFailureAuthorityPosture.NotApplicable,
        };

    private static GovernedLoopFailureHumanPosture HumanPosture(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.HumanReviewRejected => GovernedLoopFailureHumanPosture.ReviewRejected,
            GovernedLoopFailureObservationKind.UserPaused => GovernedLoopFailureHumanPosture.Paused,
            GovernedLoopFailureObservationKind.UserCancelled => GovernedLoopFailureHumanPosture.Cancelled,
            GovernedLoopFailureObservationKind.PersistenceIntegrityFailure or GovernedLoopFailureObservationKind.AuditIntegrityFailure or GovernedLoopFailureObservationKind.EvidenceIntegrityFailure or GovernedLoopFailureObservationKind.AmbiguousOutcome => GovernedLoopFailureHumanPosture.Unknown,
            _ => GovernedLoopFailureHumanPosture.None,
        };

    private static GovernedLoopFailureRetrySafety RetrySafety(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.DependencyUnavailable or GovernedLoopFailureObservationKind.DispatchProvedNotStarted or GovernedLoopFailureObservationKind.RetryableNoEffect or GovernedLoopFailureObservationKind.TimeoutNoEffect or GovernedLoopFailureObservationKind.CancellationNoEffect => GovernedLoopFailureRetrySafety.RetryableWithExactIntent,
            GovernedLoopFailureObservationKind.AmbiguousOutcome or GovernedLoopFailureObservationKind.PersistenceIntegrityFailure or GovernedLoopFailureObservationKind.AuditIntegrityFailure or GovernedLoopFailureObservationKind.EvidenceIntegrityFailure => GovernedLoopFailureRetrySafety.Unknown,
            _ => GovernedLoopFailureRetrySafety.NotRetryable,
        };

    private static GovernedLoopFailureSeverity Severity(GovernedLoopFailureObservationKind kind)
        => kind switch
        {
            GovernedLoopFailureObservationKind.AmbiguousOutcome or GovernedLoopFailureObservationKind.PersistenceIntegrityFailure or GovernedLoopFailureObservationKind.AuditIntegrityFailure or GovernedLoopFailureObservationKind.EvidenceIntegrityFailure => GovernedLoopFailureSeverity.ReviewBlocked,
            GovernedLoopFailureObservationKind.AuthorityDenied or GovernedLoopFailureObservationKind.AuthorityRevoked or GovernedLoopFailureObservationKind.HumanReviewRejected or GovernedLoopFailureObservationKind.UserPaused or GovernedLoopFailureObservationKind.UserCancelled or GovernedLoopFailureObservationKind.AgentSelectedFailure or GovernedLoopFailureObservationKind.QuotaExhausted or GovernedLoopFailureObservationKind.DeadlineExhausted or GovernedLoopFailureObservationKind.IterationExhausted or GovernedLoopFailureObservationKind.CostExhausted => GovernedLoopFailureSeverity.Critical,
            _ => GovernedLoopFailureSeverity.Error,
        };
}
