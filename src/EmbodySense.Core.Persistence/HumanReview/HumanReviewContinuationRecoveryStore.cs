using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.HumanReview;

/// <summary>Implements bounded Human Review continuation discovery, exact rereads, and fenced mutations over the canonical run store.</summary>
/// <remarks>This adapter owns no queue, index, timer, worker lifetime, or runtime action. It pages canonical run summaries, rereads each candidate exactly, and delegates all atomic continuation transitions to <see cref="HumanReviewContinuationRunStore"/>.</remarks>
public sealed class HumanReviewContinuationRecoveryStore : IHumanReviewContinuationRecoveryStore
{
    private readonly ICustomLoopRunStore _runs;
    private readonly IGovernedLoopGraphRevisionStore _graphs;
    private readonly HumanReviewContinuationRunStore _continuations;

    /// <summary>Initializes the recovery adapter over canonical run and immutable graph-revision stores.</summary>
    /// <param name="runs">The sole canonical run and review transaction boundary.</param>
    /// <param name="graphs">The immutable graph source used only for exact candidate rereads.</param>
    public HumanReviewContinuationRecoveryStore(ICustomLoopRunStore runs, IGovernedLoopGraphRevisionStore graphs)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        _continuations = new HumanReviewContinuationRunStore(_runs);
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationRecoveryPage> ListCandidatesAsync(int maximumCount, string? scanCursor, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > CustomLoopLimits.MaxRecentRunsPageSize || observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            return Page(HumanReviewContinuationRecoveryPageStatus.Invalid);
        }

        CustomLoopRunPage source;
        try
        {
            source = await _runs.ListPageAsync(new CustomLoopRunPageRequest(maximumCount, null, scanCursor), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return Page(HumanReviewContinuationRecoveryPageStatus.Invalid);
        }
        catch (FormatException)
        {
            return Page(HumanReviewContinuationRecoveryPageStatus.Invalid);
        }
        catch
        {
            return Page(HumanReviewContinuationRecoveryPageStatus.Unavailable);
        }

        if (source?.Items is null || source.Items.Count > maximumCount)
        {
            return Page(HumanReviewContinuationRecoveryPageStatus.Invalid);
        }

        var candidates = new List<HumanReviewContinuationRecoveryCandidate>(source.Items.Count);
        foreach (var summary in source.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (summary is null || string.IsNullOrWhiteSpace(summary.Id))
            {
                return Page(HumanReviewContinuationRecoveryPageStatus.Invalid);
            }

            CustomLoopRunRecord? run;
            try
            {
                run = await _runs.GetAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (FormatException)
            {
                return Page(HumanReviewContinuationRecoveryPageStatus.Invalid);
            }
            catch
            {
                return Page(HumanReviewContinuationRecoveryPageStatus.Unavailable);
            }

            if (run is not null && TryCreateEligibleCandidate(run, observedAtUtc, out var candidate))
            {
                candidates.Add(candidate!);
            }
        }

        var sourceTruncated = source.ContinuationCursor is not null;
        var nextScanCursor = sourceTruncated
            ? source.ContinuationCursor
            : source.Items.Count == 0
                ? null
                : EncodeTailProbeCursor(source.Items[^1]);
        return new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, candidates, nextScanCursor, sourceTruncated);
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationCandidateReadResult> ReadAsync(HumanReviewContinuationCandidateQuery query, CancellationToken cancellationToken = default)
    {
        if (query is null || string.IsNullOrWhiteSpace(query.RunId))
        {
            return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Corrupt);
        }

        CustomLoopRunRecord? run;
        try
        {
            run = await _runs.GetAsync(query.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Corrupt);
        }
        catch
        {
            return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Unavailable);
        }

        if (run is null)
        {
            return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Missing);
        }
        if (!TryMatchQuery(run, query, out var continuation, out var claim))
        {
            return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Stale);
        }
        if (run.SequentialAdapterBinding is not { } binding)
        {
            return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Corrupt);
        }

        try
        {
            var artifactRead = await _graphs.ReadArtifactAsync(binding.ExecutionBinding.Revision, cancellationToken).ConfigureAwait(false);
            if (artifactRead is null || artifactRead.Status is GovernedLoopRevisionStoreReadStatus.Unavailable or GovernedLoopRevisionStoreReadStatus.Ambiguous)
            {
                return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Unavailable);
            }
            if (artifactRead.Status != GovernedLoopRevisionStoreReadStatus.Ready || artifactRead.Artifact is null)
            {
                return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Missing);
            }
            if (!string.Equals(artifactRead.Artifact.ArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
                || !string.Equals(artifactRead.Artifact.LayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal))
            {
                return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Stale);
            }

            return new HumanReviewContinuationCandidateReadResult(
                HumanReviewContinuationCandidateReadStatus.Current,
                new HumanReviewContinuationCandidate(run, artifactRead.Artifact, continuation, claim));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationStoreMutationResult> ClaimAsync(HumanReviewContinuationClaimIntent intent, CancellationToken cancellationToken = default)
    {
        if (!MatchesClaimIntent(intent))
        {
            return new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Invalid);
        }

        return await MutateAsync(() => _continuations.ClaimAsync(intent.Candidate.RunId, intent.Candidate.ExpectedLifecycleVersion, intent.Claim, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationStoreMutationResult> CompleteAsync(HumanReviewContinuationCompletionIntent intent, HumanReviewContinuationCompletion completion, CancellationToken cancellationToken = default)
    {
        if (!MatchesCompletionIntent(intent, completion))
        {
            return new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Invalid);
        }

        return await MutateAsync(() => _continuations.CompleteAsync(intent.RunId, intent.ExpectedLifecycleVersion, completion, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationStoreMutationResult> RetireAsync(HumanReviewContinuationRetirementIntent intent, HumanReviewContinuationRetirement retirement, CancellationToken cancellationToken = default)
    {
        if (!MatchesRetirementIntent(intent, retirement))
        {
            return new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Invalid);
        }

        return await MutateAsync(() => _continuations.RetireAsync(intent.RunId, intent.ExpectedLifecycleVersion, intent.Claim, retirement, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static bool TryCreateEligibleCandidate(CustomLoopRunRecord run, DateTimeOffset observedAtUtc, out HumanReviewContinuationRecoveryCandidate? candidate)
    {
        candidate = null;
        try
        {
            if (!CustomLoopRunValidator.Validate(run).IsValid
                || run.IsTerminal
                || run.Status != CustomLoopRunStatus.Paused
                || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked
                || run.HumanReview is not { } review
                || review.AcceptedTerminalDecision is not { } decision
                || decision.Kind != HumanReviewDecisionKind.Approve
                || review.ContinuationReservation is not { } reservation
                || review.Continuation is not { } continuation
                || continuation.Completion is not null
                || continuation.Retirement is not null
                || observedAtUtc >= continuation.Wake.ExpiresAtUtc)
            {
                return false;
            }

            var active = continuation.Claims.IsDefaultOrEmpty ? null : continuation.Claims[^1];
            if (active is not null && observedAtUtc <= active.LeaseExpiresAtUtc)
            {
                return false;
            }

            candidate = new HumanReviewContinuationRecoveryCandidate(
                run.Id,
                run.LifecycleVersion,
                new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash),
                new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash),
                new HumanReviewContinuationWakeReference(continuation.Wake.WakeId, continuation.Wake.WakeHash),
                continuation.Wake.ExpectedGeneration,
                continuation.Wake.ExpiresAtUtc,
                new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
                active is null ? null : new HumanReviewContinuationClaimReference(active.ClaimId, active.ClaimHash));
            return true;
        }
        catch
        {
            candidate = null;
            return false;
        }
    }

    private static bool TryMatchQuery(
        CustomLoopRunRecord run,
        HumanReviewContinuationCandidateQuery query,
        out HumanReviewContinuationState? continuation,
        out HumanReviewContinuationClaim? claim)
    {
        continuation = null;
        claim = null;
        try
        {
            if (!CustomLoopRunValidator.Validate(run).IsValid
                || run.IsTerminal
                || run.Status != CustomLoopRunStatus.Paused
                || run.Frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked
                || !string.Equals(run.Id, query.RunId, StringComparison.Ordinal)
                || run.HumanReview is not { } review
                || review.AcceptedTerminalDecision is not { } decision
                || !Equals(query.Request, new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash))
                || !Equals(query.Decision, new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash)))
            {
                return false;
            }

            if (query.Reservation is not null && !Equals(query.Reservation, review.ContinuationReservation is null ? null : new HumanReviewContinuationReservationReference(review.ContinuationReservation.ReservationId, review.ContinuationReservation.ReservationHash)))
            {
                return false;
            }
            if (query.Wake is not null && !Equals(query.Wake, review.Continuation is null ? null : new HumanReviewContinuationWakeReference(review.Continuation.Wake.WakeId, review.Continuation.Wake.WakeHash)))
            {
                return false;
            }

            var current = review.Continuation;
            var currentClaim = current?.Claims.IsDefaultOrEmpty == false ? current.Claims[^1] : null;
            if (query.Claim is not null && !Equals(query.Claim, currentClaim is null ? null : new HumanReviewContinuationClaimReference(currentClaim.ClaimId, currentClaim.ClaimHash)))
            {
                return false;
            }
            if (query.ExpectedGeneration is not null && current?.Wake.ExpectedGeneration != query.ExpectedGeneration)
            {
                return false;
            }

            continuation = current;
            claim = currentClaim;
            return true;
        }
        catch
        {
            continuation = null;
            claim = null;
            return false;
        }
    }

    private static bool MatchesClaimIntent(HumanReviewContinuationClaimIntent? intent)
        => intent is not null
            && intent.Candidate is not null
            && intent.Claim is not null
            && Equals(intent.Candidate.Wake, intent.Claim.Wake)
            && Equals(intent.Candidate.Reservation, intent.Claim.Reservation)
            && intent.Candidate.ExpectedGeneration == intent.Claim.ExpectedGeneration;

    private static bool MatchesCompletionIntent(HumanReviewContinuationCompletionIntent? intent, HumanReviewContinuationCompletion? completion)
        => intent is not null
            && completion is not null
            && completion.ReleaseReceipt is not null
            && intent.ReleaseReceipt is not null
            && Equals(intent.Request, intent.ReleaseReceipt.Request)
            && Equals(intent.Wake, completion.Wake)
            && Equals(intent.Claim, completion.Claim)
            && Equals(intent.Reservation, completion.Reservation)
            && intent.ExpectedGeneration == completion.ExpectedGeneration
            && string.Equals(intent.ReleaseReceipt.ReleaseOperationId, completion.ReleaseReceipt.ReleaseOperationId, StringComparison.Ordinal)
            && Equals(intent.Wake, intent.ReleaseReceipt.Wake)
            && Equals(intent.Claim, intent.ReleaseReceipt.Claim)
            && Equals(intent.Reservation, intent.ReleaseReceipt.Reservation)
            && Equals(intent.ReleaseReceipt.Wake, completion.ReleaseReceipt.Wake)
            && Equals(intent.ReleaseReceipt.Claim, completion.ReleaseReceipt.Claim)
            && Equals(intent.ReleaseReceipt.Reservation, completion.ReleaseReceipt.Reservation)
            && intent.ReleaseReceipt.ExpectedGeneration == completion.ReleaseReceipt.ExpectedGeneration
            && intent.ReleaseReceipt.Kind == completion.ReleaseReceipt.Kind
            && string.Equals(intent.ReleaseReceipt.EffectReceiptHash, completion.ReleaseReceipt.EffectReceiptHash, StringComparison.Ordinal);

    private static bool MatchesRetirementIntent(HumanReviewContinuationRetirementIntent? intent, HumanReviewContinuationRetirement? retirement)
        => intent is not null
            && retirement is not null
            && Equals(intent.Wake, retirement.Wake)
            && Equals(intent.Reservation, retirement.Reservation)
            && intent.ExpectedGeneration == retirement.ExpectedGeneration
            && intent.Outcome == retirement.Outcome;

    private static string EncodeTailProbeCursor(CustomLoopRunSummary summary)
        => CustomLoopRunPageCursorCodec.Encode(new CustomLoopRunPageCursor(summary.CreatedAtUtc, summary.Id, null));

    private static HumanReviewContinuationRecoveryPage Page(HumanReviewContinuationRecoveryPageStatus status)
        => new(status, [], null, false);

    private static async Task<HumanReviewContinuationStoreMutationResult> MutateAsync(
        Func<Task<HumanReviewContinuationMutationResult>> mutate,
        CancellationToken cancellationToken)
    {
        try
        {
            return Mutation((await mutate().ConfigureAwait(false)).Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Invalid);
        }
        catch (FormatException)
        {
            return new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Invalid);
        }
        catch
        {
            return new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Unavailable);
        }
    }

    private static HumanReviewContinuationStoreMutationResult Mutation(HumanReviewContinuationMutationStatus status)
        => new(status switch
        {
            HumanReviewContinuationMutationStatus.Committed => HumanReviewContinuationStoreMutationStatus.Committed,
            HumanReviewContinuationMutationStatus.Replayed => HumanReviewContinuationStoreMutationStatus.Replayed,
            HumanReviewContinuationMutationStatus.Conflict => HumanReviewContinuationStoreMutationStatus.Conflict,
            HumanReviewContinuationMutationStatus.NotFound => HumanReviewContinuationStoreMutationStatus.NotFound,
            HumanReviewContinuationMutationStatus.Invalid => HumanReviewContinuationStoreMutationStatus.Invalid,
            HumanReviewContinuationMutationStatus.Unavailable => HumanReviewContinuationStoreMutationStatus.Unavailable,
            HumanReviewContinuationMutationStatus.LimitExceeded => HumanReviewContinuationStoreMutationStatus.LimitExceeded,
            _ => HumanReviewContinuationStoreMutationStatus.Unknown,
        });
}
