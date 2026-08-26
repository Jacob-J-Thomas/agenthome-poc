using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Coordinates one bounded, host-neutral, exact-once Human Review continuation recovery pass.</summary>
/// <remarks>This type owns neither a timer nor a worker lifetime. A host invokes a bounded pass, persists its opaque cursor externally when it elects to continue scanning, and supplies the release port. The coordinator never converts unavailable or ambiguous release evidence into redispatch or terminal state.</remarks>
public sealed class HumanReviewContinuationRecoveryCoordinator
{
    private readonly IHumanReviewContinuationRecoveryStore _store;
    private readonly IHumanReviewContinuationConsumer _consumer;
    private readonly IHumanReviewContinuationReleasePort _release;
    private readonly IHumanReviewTrustedClock _clock;

    /// <summary>Initializes a host-neutral recovery coordinator over canonical ports.</summary>
    /// <param name="store">The canonical bounded discovery, reread, claim, completion, and retirement port.</param>
    /// <param name="consumer">The Application-only continuation evaluator.</param>
    /// <param name="release">The host-owned idempotent release boundary.</param>
    /// <param name="clock">The trusted UTC clock used for claim and terminal artifact timing.</param>
    public HumanReviewContinuationRecoveryCoordinator(
        IHumanReviewContinuationRecoveryStore store,
        IHumanReviewContinuationConsumer consumer,
        IHumanReviewContinuationReleasePort release,
        IHumanReviewTrustedClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Runs one bounded recovery pass without starting a timer, allocating a background worker, or composing a runtime.</summary>
    /// <param name="request">The bounded scan, opaque cursor, worker identity, and lease configuration.</param>
    /// <param name="cancellationToken">Cancels before a closed step completes; cancellation never becomes an inferred release result.</param>
    /// <returns>A closed page posture and one non-secret result for each discovered eligible candidate.</returns>
    public async Task<HumanReviewContinuationRecoveryResult> RecoverAsync(HumanReviewContinuationRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsValid(request) || !TryNow(out var observedAtUtc))
        {
            return new HumanReviewContinuationRecoveryResult(HumanReviewContinuationRecoveryStatus.Invalid, null, false, []);
        }

        HumanReviewContinuationRecoveryPage page;
        try
        {
            page = await _store.ListCandidatesAsync(request.MaximumCount, request.ScanCursor, observedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new HumanReviewContinuationRecoveryResult(HumanReviewContinuationRecoveryStatus.Unavailable, request.ScanCursor, false, []);
        }

        if (page is null || page.Status != HumanReviewContinuationRecoveryPageStatus.Current || page.Candidates is null)
        {
            return new HumanReviewContinuationRecoveryResult(Map(page?.Status), request.ScanCursor, false, []);
        }

        var items = new List<HumanReviewContinuationRecoveryItemResult>(page.Candidates.Count);
        foreach (var candidate in page.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await RecoverCandidateAsync(candidate, request, observedAtUtc, cancellationToken).ConfigureAwait(false));
        }

        return new HumanReviewContinuationRecoveryResult(HumanReviewContinuationRecoveryStatus.Current, page.NextScanCursor, page.SourceTruncated, items);
    }

    private async Task<HumanReviewContinuationRecoveryItemResult> RecoverCandidateAsync(
        HumanReviewContinuationRecoveryCandidate candidate,
        HumanReviewContinuationRecoveryRequest request,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!TryCreateClaim(candidate, request, observedAtUtc, out var claim) || claim is null)
        {
            return Item(candidate, observedAtUtc >= candidate.WakeExpiresAtUtc
                ? HumanReviewContinuationRecoveryItemStatus.ExpiredWakeRetained
                : HumanReviewContinuationRecoveryItemStatus.Invalid);
        }

        HumanReviewContinuationStoreMutationResult claimResult;
        try
        {
            claimResult = await _store.ClaimAsync(new HumanReviewContinuationClaimIntent(candidate, claim), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        if (claimResult is null)
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        if (claimResult.Status == HumanReviewContinuationStoreMutationStatus.Replayed)
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.ClaimReplayed);
        }
        if (claimResult.Status is not HumanReviewContinuationStoreMutationStatus.Committed)
        {
            return Item(candidate, claimResult.Status is HumanReviewContinuationStoreMutationStatus.Conflict or HumanReviewContinuationStoreMutationStatus.NotFound
                ? HumanReviewContinuationRecoveryItemStatus.ClaimConflict
                : claimResult.Status == HumanReviewContinuationStoreMutationStatus.Invalid
                    ? HumanReviewContinuationRecoveryItemStatus.Invalid
                    : HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        var reread = await ReadClaimedCandidateAsync(candidate, claim, cancellationToken).ConfigureAwait(false);
        if (reread is null)
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
        }
        var rereadCandidate = reread.Candidate;
        if (reread.Status == HumanReviewContinuationCandidateReadStatus.Current && rereadCandidate is null)
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Invalid);
        }
        if (reread.Status != HumanReviewContinuationCandidateReadStatus.Current)
        {
            return Item(candidate, reread.Status == HumanReviewContinuationCandidateReadStatus.Corrupt
                ? HumanReviewContinuationRecoveryItemStatus.Invalid
                : reread.Status == HumanReviewContinuationCandidateReadStatus.Unavailable
                    ? HumanReviewContinuationRecoveryItemStatus.Parked
                : HumanReviewContinuationRecoveryItemStatus.StaleAfterClaim);
        }

        HumanReviewContinuationConsumptionResult consumption;
        try
        {
            consumption = await _consumer.ConsumeAsync(rereadCandidate!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        if (consumption is null)
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Invalid);
        }

        if (consumption.Status == HumanReviewContinuationConsumptionStatus.RetirementRequired && consumption.Retirement is not null)
        {
            return await RetireAsync(candidate, consumption.Retirement, request.CoordinatorSourceId, cancellationToken).ConfigureAwait(false);
        }

        if (consumption.Status is not (HumanReviewContinuationConsumptionStatus.ContinuationReleasePrepared or HumanReviewContinuationConsumptionStatus.EffectReleasePrepared)
            || consumption.Action is null || consumption.Completion is null)
        {
            return Item(candidate, consumption.Status == HumanReviewContinuationConsumptionStatus.Invalid
                ? HumanReviewContinuationRecoveryItemStatus.Invalid
                : HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        HumanReviewContinuationReleaseResult release;
        try
        {
            release = await _release.ReleaseAsync(consumption.Action, consumption.Completion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        if (release is null || release.Status is not (HumanReviewContinuationReleaseStatus.Completed or HumanReviewContinuationReleaseStatus.Invalid))
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        if (release.Status == HumanReviewContinuationReleaseStatus.Completed)
        {
            if (release.Completion is null)
            {
                return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
            }

            var completed = await CompleteAsync(consumption.Completion, release.Completion, cancellationToken).ConfigureAwait(false);
            return Item(candidate, completed is HumanReviewContinuationStoreMutationStatus.Committed or HumanReviewContinuationStoreMutationStatus.Replayed
                ? HumanReviewContinuationRecoveryItemStatus.Completed
                : completed == HumanReviewContinuationStoreMutationStatus.Invalid
                    ? HumanReviewContinuationRecoveryItemStatus.Invalid
                    : HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        var blocked = new HumanReviewContinuationRetirementIntent(
            consumption.Action.RunId,
            consumption.Action.ExpectedLifecycleVersion,
            consumption.Action.Wake!,
            consumption.Action.Claim!,
            consumption.Action.Reservation!,
            consumption.Action.ExpectedGeneration!.Value,
            HumanReviewContinuationOutcome.Blocked,
            HumanReviewContinuationRetirementReason.Blocked);
        return await RetireAsync(candidate, blocked, request.CoordinatorSourceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanReviewContinuationCandidateReadResult?> ReadClaimedCandidateAsync(HumanReviewContinuationRecoveryCandidate candidate, HumanReviewContinuationClaim claim, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ReadAsync(new HumanReviewContinuationCandidateQuery(
                candidate.RunId,
                candidate.Request,
                candidate.Decision,
                candidate.Wake,
                new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
                candidate.Reservation,
                claim.ExpectedGeneration), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<HumanReviewContinuationRecoveryItemResult> RetireAsync(HumanReviewContinuationRecoveryCandidate candidate, HumanReviewContinuationRetirementIntent intent, string sourceId, CancellationToken cancellationToken)
    {
        if (!TryNow(out var retiredAtUtc) || !TryCreateRetirement(intent, sourceId, retiredAtUtc, out var retirement) || retirement is null)
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        HumanReviewContinuationStoreMutationResult result;
        try
        {
            result = await _store.RetireAsync(intent, retirement, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Item(candidate, HumanReviewContinuationRecoveryItemStatus.Parked);
        }

        return Item(candidate, result?.Status is HumanReviewContinuationStoreMutationStatus.Committed or HumanReviewContinuationStoreMutationStatus.Replayed
            ? HumanReviewContinuationRecoveryItemStatus.Retired
            : result?.Status == HumanReviewContinuationStoreMutationStatus.Invalid
                ? HumanReviewContinuationRecoveryItemStatus.Invalid
                : HumanReviewContinuationRecoveryItemStatus.Parked);
    }

    private async Task<HumanReviewContinuationStoreMutationStatus> CompleteAsync(HumanReviewContinuationCompletionIntent intent, HumanReviewContinuationCompletion completion, CancellationToken cancellationToken)
    {
        try
        {
            return (await _store.CompleteAsync(intent, completion, cancellationToken).ConfigureAwait(false))?.Status ?? HumanReviewContinuationStoreMutationStatus.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return HumanReviewContinuationStoreMutationStatus.Unavailable;
        }
    }

    private static bool TryCreateClaim(HumanReviewContinuationRecoveryCandidate candidate, HumanReviewContinuationRecoveryRequest request, DateTimeOffset observedAtUtc, out HumanReviewContinuationClaim? claim)
    {
        claim = null;
        try
        {
            if (candidate is null
                || !IsValidCandidate(candidate)
                || observedAtUtc.Offset != TimeSpan.Zero
                || observedAtUtc >= candidate.WakeExpiresAtUtc
                || candidate.WakeExpiresAtUtc.Offset != TimeSpan.Zero
                || !HumanReviewIdentifier.IsValid(request.WorkerId)
                || !HumanReviewIdentifier.IsValid(request.CoordinatorSourceId))
            {
                return false;
            }

            var leaseExpiresAtUtc = observedAtUtc + request.ClaimLeaseDuration;
            if (leaseExpiresAtUtc > candidate.WakeExpiresAtUtc)
            {
                leaseExpiresAtUtc = candidate.WakeExpiresAtUtc;
            }
            if (leaseExpiresAtUtc <= observedAtUtc)
            {
                return false;
            }

            var claimId = "claim-" + Guid.NewGuid().ToString("N");
            var provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, request.CoordinatorSourceId, claimId, observedAtUtc, string.Empty));
            claim = HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
                HumanReviewContinuationClaim.CurrentSchemaVersion,
                claimId,
                candidate.Wake,
                candidate.Reservation,
                candidate.ExpectedGeneration,
                request.WorkerId,
                observedAtUtc,
                leaseExpiresAtUtc,
                provenance,
                string.Empty));
            return true;
        }
        catch
        {
            claim = null;
            return false;
        }
    }

    private static bool TryCreateRetirement(HumanReviewContinuationRetirementIntent intent, string sourceId, DateTimeOffset retiredAtUtc, out HumanReviewContinuationRetirement? retirement)
    {
        retirement = null;
        try
        {
            if (intent is null || retiredAtUtc.Offset != TimeSpan.Zero || !HumanReviewIdentifier.IsValid(sourceId))
            {
                return false;
            }

            var retirementId = "retirement-" + Guid.NewGuid().ToString("N");
            var provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, sourceId, retirementId, retiredAtUtc, string.Empty));
            retirement = HumanReviewContinuationContractHash.ApplyRetirement(new HumanReviewContinuationRetirement(
                HumanReviewContinuationRetirement.CurrentSchemaVersion,
                retirementId,
                intent.Wake,
                intent.Reservation,
                intent.ExpectedGeneration,
                intent.Outcome,
                retiredAtUtc,
                ImmutableArray<HumanReviewRedactedPreview>.Empty,
                provenance,
                string.Empty));
            return true;
        }
        catch
        {
            retirement = null;
            return false;
        }
    }

    private static HumanReviewContinuationRecoveryItemResult Item(HumanReviewContinuationRecoveryCandidate candidate, HumanReviewContinuationRecoveryItemStatus status)
        => new(candidate?.RunId ?? string.Empty, status);

    private static bool IsValidCandidate(HumanReviewContinuationRecoveryCandidate? candidate)
        => candidate is not null
            && CustomLoopArtifactIdentifier.IsValid(candidate.RunId)
            && candidate.ExpectedLifecycleVersion >= 1
            && candidate.ExpectedGeneration >= 1
            && candidate.Request is { } request
            && HumanReviewIdentifier.IsValid(request.RequestId)
            && HumanReviewContractHash.IsSha256(request.RequestHash)
            && candidate.Decision is { } decision
            && HumanReviewIdentifier.IsValid(decision.DecisionId)
            && HumanReviewIdentifier.IsValid(decision.DecisionOperationId)
            && decision.Kind == HumanReviewDecisionKind.Approve
            && HumanReviewContractHash.IsSha256(decision.DecisionHash)
            && candidate.Wake is { } wake
            && HumanReviewIdentifier.IsValid(wake.WakeId)
            && HumanReviewContractHash.IsSha256(wake.WakeHash)
            && candidate.Reservation is { } reservation
            && HumanReviewIdentifier.IsValid(reservation.ReservationId)
            && HumanReviewContractHash.IsSha256(reservation.ReservationHash)
            && (candidate.PriorClaim is null
                || HumanReviewIdentifier.IsValid(candidate.PriorClaim.ClaimId)
                && HumanReviewContractHash.IsSha256(candidate.PriorClaim.ClaimHash));

    private static HumanReviewContinuationRecoveryStatus Map(HumanReviewContinuationRecoveryPageStatus? status)
        => status == HumanReviewContinuationRecoveryPageStatus.Invalid
            ? HumanReviewContinuationRecoveryStatus.Invalid
            : HumanReviewContinuationRecoveryStatus.Unavailable;

    private static bool IsValid(HumanReviewContinuationRecoveryRequest? request)
        => request is not null
            && request.MaximumCount is >= 1 and <= CustomLoopLimits.MaxRecentRunsPageSize
            && HumanReviewIdentifier.IsValid(request.WorkerId)
            && HumanReviewIdentifier.IsValid(request.CoordinatorSourceId)
            && request.ClaimLeaseDuration > TimeSpan.Zero
            && request.ClaimLeaseDuration <= HumanReviewContractLimits.MaxContinuationClaimLease;

    private bool TryNow(out DateTimeOffset now)
    {
        try
        {
            now = _clock.UtcNow;
            return now != default && now.Offset == TimeSpan.Zero;
        }
        catch
        {
            now = default;
            return false;
        }
    }
}
