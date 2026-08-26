using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Consumes detached canonical Human Review candidates into fail-closed declared paths without persisting, leasing, resuming, or dispatching work.</summary>
/// <remarks>Approval remains consent only. Every release intent requires exact current run, frontier, graph, authority, nondecreasing trusted UTC observations, and—when applicable—effect-certainty evidence. The later durable worker owns compare-exchange, callback execution, and completion or retirement artifact construction.</remarks>
public sealed class HumanReviewContinuationConsumer : IHumanReviewContinuationConsumer
{
    private readonly IHumanReviewContinuationAuthoritySource _authority;
    private readonly IHumanReviewCurrentEffectAttemptEvidenceSource _effectEvidence;
    private readonly IGovernedLoopEffectCertaintySnapshotSource _effectCertainty;
    private readonly IHumanReviewTrustedClock _clock;

    /// <summary>Initializes the Application-only continuation consumer.</summary>
    /// <param name="authority">The independently revalidated current non-effect authority source.</param>
    /// <param name="effectEvidence">The read-only canonical current-effect identity and preparation source.</param>
    /// <param name="effectCertainty">The #570 read-only current effect-certainty source.</param>
    /// <param name="clock">The trusted UTC clock used for review, wake, and lease expiry evaluation.</param>
    /// <exception cref="ArgumentNullException">Thrown when any dependency is <see langword="null"/>.</exception>
    public HumanReviewContinuationConsumer(
        IHumanReviewContinuationAuthoritySource authority,
        IHumanReviewCurrentEffectAttemptEvidenceSource effectEvidence,
        IGovernedLoopEffectCertaintySnapshotSource effectCertainty,
        IHumanReviewTrustedClock clock)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _effectEvidence = effectEvidence ?? throw new ArgumentNullException(nameof(effectEvidence));
        _effectCertainty = effectCertainty ?? throw new ArgumentNullException(nameof(effectCertainty));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationConsumptionResult> ConsumeAsync(HumanReviewContinuationCandidate candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidate);
        if (!TryCaptureContext(candidate, out var context))
        {
            return Invalid();
        }

        return context!.Decision.Kind switch
        {
            HumanReviewDecisionKind.Reject => PrepareDecisionPath(context, HumanReviewContinuationAction.FailRejected, cancellationToken),
            HumanReviewDecisionKind.Cancel => PrepareDecisionPath(context, HumanReviewContinuationAction.Cancel, cancellationToken),
            HumanReviewDecisionKind.RequestInformation => PrepareDecisionPath(context, HumanReviewContinuationAction.ParkForInformation, cancellationToken),
            HumanReviewDecisionKind.Approve => await ConsumeApprovalAsync(context, candidate, cancellationToken).ConfigureAwait(false),
            _ => Invalid(),
        };
    }

    private async Task<HumanReviewContinuationConsumptionResult> ConsumeApprovalAsync(CanonicalContext context, HumanReviewContinuationCandidate candidate, CancellationToken cancellationToken)
    {
        if (!TryCaptureApprovedContinuation(context, candidate, out var approved))
        {
            return Invalid();
        }
        var current = approved!;

        var initialTiming = ObserveApprovalEmission(context, current, null, cancellationToken, out var initialObservedAtUtc);
        if (initialTiming is not null)
        {
            return initialTiming;
        }

        var firstAuthority = await ReadAuthorityAsync(context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (firstAuthority == HumanReviewContinuationAuthorityReadStatus.Unavailable)
        {
            return Unavailable();
        }
        if (firstAuthority != HumanReviewContinuationAuthorityReadStatus.Current)
        {
            return BlockedOrObserved(context, current, initialObservedAtUtc, cancellationToken);
        }

        if (context.Request.Binding.EffectAttempt is null)
        {
            var finalAuthority = await ReadAuthorityAsync(context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return finalAuthority switch
            {
                HumanReviewContinuationAuthorityReadStatus.Current => ReleaseOrObserved(context, current, initialObservedAtUtc, HumanReviewContinuationAction.ReleaseContinuation, null, cancellationToken),
                HumanReviewContinuationAuthorityReadStatus.Unavailable => Unavailable(),
                _ => BlockedOrObserved(context, current, initialObservedAtUtc, cancellationToken),
            };
        }

        var firstEffect = await ReadEffectAsync(context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (firstEffect.Status == HumanReviewEffectReleaseReadStatus.Unavailable)
        {
            return Unavailable();
        }
        if (firstEffect.Status != HumanReviewEffectReleaseReadStatus.ExactNotStarted)
        {
            return BlockedOrObserved(context, current, initialObservedAtUtc, cancellationToken);
        }

        var finalAuthorityForEffect = await ReadAuthorityAsync(context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (finalAuthorityForEffect == HumanReviewContinuationAuthorityReadStatus.Unavailable)
        {
            return Unavailable();
        }
        if (finalAuthorityForEffect != HumanReviewContinuationAuthorityReadStatus.Current)
        {
            return BlockedOrObserved(context, current, initialObservedAtUtc, cancellationToken);
        }

        var finalEffect = await ReadEffectAsync(context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (finalEffect.Status == HumanReviewEffectReleaseReadStatus.Unavailable)
        {
            return Unavailable();
        }
        if (finalEffect.Status != HumanReviewEffectReleaseReadStatus.ExactNotStarted || finalEffect.Query is null)
        {
            return BlockedOrObserved(context, current, initialObservedAtUtc, cancellationToken);
        }

        // This query is the same one whose paired certainty read just proved ExactNotStarted. No later, unproven
        // effect-evidence reread may replace it before the later effect boundary performs its own revalidation.
        return ReleaseOrObserved(context, current, initialObservedAtUtc, HumanReviewContinuationAction.ReleaseEffect, finalEffect.Query, cancellationToken);
    }

    private async Task<EffectRead> ReadEffectAsync(CanonicalContext context, CancellationToken cancellationToken)
    {
        var query = await ReadEffectQueryAsync(context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Status == HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable)
        {
            return new EffectRead(HumanReviewEffectReleaseReadStatus.Unavailable, null);
        }
        if (query.Status != HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current || query.Query is null)
        {
            return new EffectRead(HumanReviewEffectReleaseReadStatus.Invalid, null);
        }

        GovernedLoopEffectCertaintySnapshotResult? result;
        try
        {
            result = await _effectCertainty.ReadAsync(query.Query, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new EffectRead(HumanReviewEffectReleaseReadStatus.Unavailable, null);
        }

        return new EffectRead(HumanReviewEffectReleaseReadStatusProjection.Project(query.Query, result), query.Query);
    }

    private async Task<EffectQueryRead> ReadEffectQueryAsync(CanonicalContext context, CancellationToken cancellationToken)
    {
        var reviewed = context.Request.Binding.EffectAttempt;
        if (reviewed is null)
        {
            return new EffectQueryRead(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unknown, null);
        }

        HumanReviewCurrentEffectAttemptEvidenceReadResult? read;
        try
        {
            read = await _effectEvidence.ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(context.Request.Binding, reviewed), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new EffectQueryRead(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable, null);
        }

        if (read is null || !Enum.IsDefined(read.Status))
        {
            return new EffectQueryRead(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unknown, null);
        }
        if (read.Status != HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current || read.Evidence is not { } evidence)
        {
            return new EffectQueryRead(read.Status, null);
        }
        if (!MatchesReviewedEffect(context.Request.Binding, reviewed, evidence)
            || !HumanReviewEffectReleaseContract.TryCaptureExpectation(evidence.Identity, evidence.Preparation, out var identity, out var preparation, out _)
            || identity is null
            || preparation is null)
        {
            return new EffectQueryRead(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Stale, null);
        }

        return new EffectQueryRead(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation));
    }

    private async Task<HumanReviewContinuationAuthorityReadStatus> ReadAuthorityAsync(CanonicalContext context, CancellationToken cancellationToken)
    {
        HumanReviewContinuationAuthorityReadResult? result;
        try
        {
            result = await _authority.ReadAsync(new HumanReviewContinuationAuthorityQuery(context.Request.Binding, context.AdapterBinding, context.GraphArtifact), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            return HumanReviewContinuationAuthorityReadStatus.Unavailable;
        }

        return result is not null && Enum.IsDefined(result.Status)
            ? result.Status
            : HumanReviewContinuationAuthorityReadStatus.Invalid;
    }

    private static bool TryCaptureContext(HumanReviewContinuationCandidate candidate, out CanonicalContext? context)
    {
        context = null;
        try
        {
            var run = candidate.Run;
            if (run is null || !CustomLoopRunValidator.Validate(run).IsValid || run.HumanReview is not { } review
                || run.Status != CustomLoopRunStatus.Paused || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked
                || run.SequentialAdapterBinding is not { } adapterBinding || !GovernedLoopSequentialContractValidator.Validate(adapterBinding).IsValid
                || candidate.GraphArtifact is not { } graphArtifact || !MatchesGraph(adapterBinding, graphArtifact)
                || !HumanReviewContractSnapshot.TryCaptureRequest(review.Request, out var request, out _) || request is null
                || !TryGetAcceptedDecision(review, request, out var decision) || decision is null
                || !MatchesBinding(run, request, graphArtifact, adapterBinding))
            {
                return false;
            }

            context = new CanonicalContext(run.Id, run.LifecycleVersion, run.UpdatedAtUtc, request, decision, review.Lifecycle.Status, TryCaptureReservation(request, review.ContinuationReservation), adapterBinding, graphArtifact);
            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }

    private static bool TryCaptureApprovedContinuation(CanonicalContext context, HumanReviewContinuationCandidate candidate, out ApprovedContinuation? approved)
    {
        approved = null;
        try
        {
            var reservation = context.Reservation;
            if (context.Decision.Kind != HumanReviewDecisionKind.Approve || context.ReviewLifecycleStatus != HumanReviewLifecycleStatus.Approved
                || reservation is null || !HumanReviewContractValidator.ValidateContinuationReservation(context.Request, reservation).IsValid
                || !Equals(reservation.Decision, Reference(context.Decision))
                || !HumanReviewContinuationContractSnapshot.TryCaptureState(context.Request, reservation, candidate.Continuation, out var continuation, out _)
                || continuation is null || continuation.Completion is not null || continuation.Retirement is not null
                || candidate.Claim is null || continuation.Claims.IsDefaultOrEmpty || !Equals(continuation.Claims[^1], candidate.Claim)
                || !HumanReviewContinuationContractValidator.ValidateClaim(continuation.Wake, reservation, continuation.Claims[^1]).IsValid
                || continuation.Wake.Decision.Kind != HumanReviewDecisionKind.Approve
                || !Equals(continuation.Wake.Decision, Reference(context.Decision))
                || !Equals(continuation.Wake.Reservation, Reference(reservation))
                || !Equals(continuation.Claims[^1].Wake, Reference(continuation.Wake))
                || !Equals(continuation.Claims[^1].Reservation, Reference(reservation))
                || continuation.Claims[^1].ExpectedGeneration != continuation.Wake.ExpectedGeneration)
            {
                return false;
            }

            approved = new ApprovedContinuation(continuation.Wake, reservation, continuation.Claims[^1]);
            return true;
        }
        catch
        {
            approved = null;
            return false;
        }
    }

    private static bool TryGetAcceptedDecision(HumanReviewRunState review, HumanReviewRequest request, out HumanReviewDecision? decision)
    {
        decision = null;
        var candidate = review.AcceptedTerminalDecision;
        if (candidate is null && review.Lifecycle.Status == HumanReviewLifecycleStatus.AwaitingInformation)
        {
            candidate = review.AcceptedDecisions.LastOrDefault(value => value?.Kind == HumanReviewDecisionKind.RequestInformation);
        }

        return candidate is not null
            && HumanReviewContractValidator.ValidateDecision(request, candidate).IsValid
            && Equals(candidate.Request, new HumanReviewRequestReference(request.RequestId, request.RequestHash))
            && HumanReviewContractSnapshot.TryCaptureDecision(request, candidate, out decision, out _)
            && decision is not null;
    }

    private static HumanReviewContinuationReservation? TryCaptureReservation(HumanReviewRequest request, HumanReviewContinuationReservation? reservation)
    {
        try
        {
            if (reservation is null)
            {
                return null;
            }

            var snapshot = reservation with
            {
                Request = reservation.Request is null ? null! : reservation.Request with { },
                Decision = reservation.Decision is null ? null! : reservation.Decision with { },
                Provenance = reservation.Provenance is null ? null! : reservation.Provenance with { },
            };
            return HumanReviewContractValidator.ValidateContinuationReservation(request, snapshot).IsValid ? snapshot : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            return null;
        }
    }

    private static bool MatchesBinding(CustomLoopRunRecord run, HumanReviewRequest request, GovernedLoopGraphRevisionArtifact artifact, GovernedLoopSequentialAdapterBinding adapterBinding)
    {
        var frontier = run.Frontier;
        var matches = frontier?.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked).Take(2).ToArray();
        var blocked = matches is { Length: 1 } ? matches[0] : null;
        return blocked is not null
            && string.Equals(run.Id, request.Binding.RunId, StringComparison.Ordinal)
            && string.Equals(frontier!.WorkspaceId, request.Binding.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.RunId, run.Id, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.GraphId, request.Binding.GraphId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.RevisionId, request.Binding.RevisionId, StringComparison.Ordinal)
            && string.Equals(frontier.Binding.Revision.ExecutableHash, request.Binding.RevisionHash, StringComparison.Ordinal)
            && frontier.Payload.FrontierVersion == request.Binding.FrontierVersion
            && string.Equals(frontier.Payload.ContentHash, request.Binding.FrontierHash, StringComparison.Ordinal)
            && string.Equals(blocked.NodeId, request.Binding.NodeId, StringComparison.Ordinal)
            && blocked.Attempt == request.Binding.Attempt
            && (request.Binding.ActivationOrdinal is null || blocked.ActivationOrdinal == request.Binding.ActivationOrdinal)
            && (request.Binding.VisitOrdinal is null || blocked.VisitOrdinal == request.Binding.VisitOrdinal)
            && string.Equals(adapterBinding.WorkspaceId, request.Binding.WorkspaceId, StringComparison.Ordinal)
            && Equals(adapterBinding.ExecutionBinding, frontier.Binding)
            && string.Equals(adapterBinding.AdmissionReceiptHash, frontier.AdmissionReceiptHash, StringComparison.Ordinal)
            && string.Equals(adapterBinding.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
            && string.Equals(adapterBinding.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal);
    }

    private static bool MatchesGraph(GovernedLoopSequentialAdapterBinding binding, GovernedLoopGraphRevisionArtifact artifact)
        => artifact.SchemaVersion == GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion
            && string.Equals(binding.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
            && string.Equals(binding.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
            && string.Equals(binding.ExecutionBinding.Revision.GraphId, artifact.RevisionArtifact.Revision.GraphId, StringComparison.Ordinal)
            && string.Equals(binding.ExecutionBinding.Revision.RevisionId, artifact.RevisionArtifact.Revision.RevisionId, StringComparison.Ordinal)
            && string.Equals(binding.ExecutionBinding.Revision.ExecutableHash, artifact.RevisionArtifact.Revision.ExecutableHash, StringComparison.Ordinal);

    private static bool MatchesReviewedEffect(HumanReviewBinding binding, HumanReviewEffectAttemptBinding reviewed, HumanReviewCurrentEffectAttemptEvidence evidence)
    {
        try
        {
            return string.Equals(evidence.Identity.RunId, binding.RunId, StringComparison.Ordinal)
                && string.Equals(evidence.Identity.GraphId, binding.GraphId, StringComparison.Ordinal)
                && string.Equals(evidence.Identity.RevisionId, binding.RevisionId, StringComparison.Ordinal)
                && string.Equals(evidence.Identity.RevisionHash, binding.RevisionHash, StringComparison.Ordinal)
                && string.Equals(evidence.Identity.FrontierId, binding.FrontierId, StringComparison.Ordinal)
                && evidence.Identity.FrontierVersion == binding.FrontierVersion
                && string.Equals(evidence.Identity.FrontierHash, binding.FrontierHash, StringComparison.Ordinal)
                && string.Equals(evidence.Identity.NodeId, binding.NodeId, StringComparison.Ordinal)
                && evidence.Identity.ActivationOrdinal == binding.ActivationOrdinal
                && evidence.Identity.VisitOrdinal == binding.VisitOrdinal
                && evidence.Identity.NodeAttempt == binding.Attempt
                && string.Equals(evidence.Identity.EffectId, reviewed.EffectAttemptId, StringComparison.Ordinal)
                && string.Equals(evidence.Identity.OperationId, reviewed.OperationId, StringComparison.Ordinal)
                && evidence.Identity.EffectGeneration == reviewed.EffectGeneration
                && string.Equals(evidence.Identity.IntentHash, reviewed.IntentHash, StringComparison.Ordinal)
                && string.Equals(evidence.Preparation.IntentHash, reviewed.IntentHash, StringComparison.Ordinal)
                && string.Equals(evidence.Preparation.PreparationHash, reviewed.PreparationHash, StringComparison.Ordinal)
                && string.Equals(evidence.Preparation.ReviewTargetHash, binding.TargetHash, StringComparison.Ordinal)
                && string.Equals(evidence.Preparation.ReviewPreconditionHash, binding.PreconditionHash, StringComparison.Ordinal)
                && string.Equals(evidence.Preparation.ReviewPayloadHash, binding.PayloadHash, StringComparison.Ordinal)
                && string.Equals(evidence.Preparation.AdmissionAuthorityEvidenceHash, binding.AuthorityGrantHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private HumanReviewContinuationConsumptionResult PrepareDecisionPath(CanonicalContext context, HumanReviewContinuationAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTrustedNow(context, null, out _))
        {
            return Unavailable();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return DecisionPath(context, action);
    }

    private HumanReviewContinuationConsumptionResult BlockedOrObserved(CanonicalContext context, ApprovedContinuation approved, DateTimeOffset initialObservedAtUtc, CancellationToken cancellationToken)
    {
        var timing = ObserveApprovalEmission(context, approved, initialObservedAtUtc, cancellationToken, out _);
        return timing ?? Retire(context, approved, HumanReviewContinuationOutcome.Blocked, HumanReviewContinuationRetirementReason.Blocked);
    }

    private HumanReviewContinuationConsumptionResult ReleaseOrObserved(
        CanonicalContext context,
        ApprovedContinuation approved,
        DateTimeOffset initialObservedAtUtc,
        HumanReviewContinuationAction action,
        GovernedLoopEffectCertaintySnapshotQuery? effectQuery,
        CancellationToken cancellationToken)
    {
        var timing = ObserveApprovalEmission(context, approved, initialObservedAtUtc, cancellationToken, out _);
        return timing ?? Release(context, approved, action, effectQuery);
    }

    private HumanReviewContinuationConsumptionResult? ObserveApprovalEmission(CanonicalContext context, ApprovedContinuation approved, DateTimeOffset? initialObservedAtUtc, CancellationToken cancellationToken, out DateTimeOffset observedAtUtc)
    {
        observedAtUtc = default;
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTrustedNow(context, approved, out var now))
        {
            return Unavailable();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (initialObservedAtUtc is not null && now < initialObservedAtUtc.Value)
        {
            return Unavailable();
        }

        observedAtUtc = now;
        if (now >= approved.Wake.ExpiresAtUtc)
        {
            return Retire(context, approved, HumanReviewContinuationOutcome.Expired, HumanReviewContinuationRetirementReason.Expired);
        }
        if (now >= approved.Claim.LeaseExpiresAtUtc)
        {
            return StaleClaim();
        }

        return null;
    }

    private bool TryGetTrustedNow(CanonicalContext context, ApprovedContinuation? approved, out DateTimeOffset now)
    {
        try
        {
            now = _clock.UtcNow;
            return now != default
                && now.Offset == TimeSpan.Zero
                && now >= context.RunUpdatedAtUtc
                && (approved is null || now >= approved.Reservation.ReservedAtUtc)
                && (approved is null || now >= approved.Wake.PublishedAtUtc)
                && (approved is null || now >= approved.Claim.ClaimedAtUtc);
        }
        catch
        {
            now = default;
            return false;
        }
    }

    private static HumanReviewContinuationConsumptionResult DecisionPath(CanonicalContext context, HumanReviewContinuationAction action)
        => new(HumanReviewContinuationConsumptionStatus.DecisionPathPrepared, DecisionIntent(context, action));

    private static HumanReviewContinuationConsumptionResult Release(CanonicalContext context, ApprovedContinuation approved, HumanReviewContinuationAction action, GovernedLoopEffectCertaintySnapshotQuery? effectQuery)
    {
        var intent = new HumanReviewContinuationActionIntent(
            action,
            context.RunId,
            context.ExpectedLifecycleVersion,
            new HumanReviewRequestReference(context.Request.RequestId, context.Request.RequestHash),
            Reference(context.Decision),
            Reference(approved.Wake),
            Reference(approved.Claim),
            Reference(approved.Reservation),
            approved.Wake.ExpectedGeneration,
            effectQuery);
        var completion = new HumanReviewContinuationCompletionIntent(context.RunId, context.ExpectedLifecycleVersion, Reference(approved.Wake), Reference(approved.Claim), Reference(approved.Reservation), approved.Wake.ExpectedGeneration);
        return new(action == HumanReviewContinuationAction.ReleaseEffect ? HumanReviewContinuationConsumptionStatus.EffectReleasePrepared : HumanReviewContinuationConsumptionStatus.ContinuationReleasePrepared, intent, completion);
    }

    private static HumanReviewContinuationConsumptionResult Retire(CanonicalContext context, ApprovedContinuation approved, HumanReviewContinuationOutcome outcome, HumanReviewContinuationRetirementReason reason)
        => new(HumanReviewContinuationConsumptionStatus.RetirementRequired, Retirement: new HumanReviewContinuationRetirementIntent(context.RunId, context.ExpectedLifecycleVersion, Reference(approved.Wake), Reference(approved.Claim), Reference(approved.Reservation), approved.Wake.ExpectedGeneration, outcome, reason));

    private static HumanReviewContinuationConsumptionResult Unavailable() => new(HumanReviewContinuationConsumptionStatus.Unavailable);

    private static HumanReviewContinuationConsumptionResult StaleClaim() => new(HumanReviewContinuationConsumptionStatus.StaleClaim);

    private static HumanReviewContinuationConsumptionResult Invalid() => new(HumanReviewContinuationConsumptionStatus.Invalid);

    private static HumanReviewContinuationActionIntent DecisionIntent(CanonicalContext context, HumanReviewContinuationAction action)
        => new(action, context.RunId, context.ExpectedLifecycleVersion, new HumanReviewRequestReference(context.Request.RequestId, context.Request.RequestHash), Reference(context.Decision), null, null, null, null, null);

    private static HumanReviewDecisionReference Reference(HumanReviewDecision value) => new(value.DecisionId, value.DecisionOperationId, value.Kind, value.DecisionHash);

    private static HumanReviewContinuationReservationReference Reference(HumanReviewContinuationReservation value) => new(value.ReservationId, value.ReservationHash);

    private static HumanReviewContinuationWakeReference Reference(HumanReviewContinuationWake value) => new(value.WakeId, value.WakeHash);

    private static HumanReviewContinuationClaimReference Reference(HumanReviewContinuationClaim value) => new(value.ClaimId, value.ClaimHash);

    private sealed record CanonicalContext(
        string RunId,
        int ExpectedLifecycleVersion,
        DateTimeOffset RunUpdatedAtUtc,
        HumanReviewRequest Request,
        HumanReviewDecision Decision,
        HumanReviewLifecycleStatus ReviewLifecycleStatus,
        HumanReviewContinuationReservation? Reservation,
        GovernedLoopSequentialAdapterBinding AdapterBinding,
        GovernedLoopGraphRevisionArtifact GraphArtifact);

    private sealed record ApprovedContinuation(
        HumanReviewContinuationWake Wake,
        HumanReviewContinuationReservation Reservation,
        HumanReviewContinuationClaim Claim);

    private sealed record EffectRead(HumanReviewEffectReleaseReadStatus Status, GovernedLoopEffectCertaintySnapshotQuery? Query);

    private sealed record EffectQueryRead(HumanReviewCurrentEffectAttemptEvidenceReadStatus Status, GovernedLoopEffectCertaintySnapshotQuery? Query);
}
