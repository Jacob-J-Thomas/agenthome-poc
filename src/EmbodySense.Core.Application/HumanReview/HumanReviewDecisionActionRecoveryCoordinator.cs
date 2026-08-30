using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Coordinates one bounded, host-neutral, exact-once recovery pass for accepted non-approval Human Review actions.</summary>
/// <remarks>Unavailable and response-unknown action results remain pending for a later canonical reread. A second exact reread fences the release intent after consumption. This coordinator never converts non-approval into approval, effect consent, or a release receipt.</remarks>
public sealed class HumanReviewDecisionActionRecoveryCoordinator
{
    private readonly IHumanReviewDecisionActionRecoveryStore _store;
    private readonly IHumanReviewDecisionActionConsumer _consumer;
    private readonly IHumanReviewDecisionActionReleasePort _release;
    private readonly IHumanReviewTrustedClock _clock;

    /// <summary>Initializes the coordinator over canonical action recovery ports.</summary>
    public HumanReviewDecisionActionRecoveryCoordinator(IHumanReviewDecisionActionRecoveryStore store, IHumanReviewDecisionActionConsumer consumer, IHumanReviewDecisionActionReleasePort release, IHumanReviewTrustedClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Runs one bounded recovery pass without composing a worker, timer, queue, graph re-entry, or runtime facade.</summary>
    public async Task<HumanReviewDecisionActionRecoveryResult> RecoverAsync(HumanReviewDecisionActionRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        if (!Valid(request) || !TryNow(out var observedAtUtc)) return new(HumanReviewDecisionActionRecoveryStatus.Invalid, null, false, []);
        HumanReviewDecisionActionRecoveryPage page;
        try { page = await _store.ListCandidatesAsync(request.MaximumCount, request.ScanCursor, observedAtUtc, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(HumanReviewDecisionActionRecoveryStatus.Unavailable, request.ScanCursor, false, []); }
        if (page is null || page.Status != HumanReviewDecisionActionRecoveryPageStatus.Current || page.Candidates is null) return new(page?.Status == HumanReviewDecisionActionRecoveryPageStatus.Invalid ? HumanReviewDecisionActionRecoveryStatus.Invalid : HumanReviewDecisionActionRecoveryStatus.Unavailable, request.ScanCursor, false, []);
        var items = new List<HumanReviewDecisionActionRecoveryItemResult>(page.Candidates.Count);
        foreach (var candidate in page.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await RecoverCandidateAsync(candidate, request, observedAtUtc, cancellationToken).ConfigureAwait(false));
        }
        return new(HumanReviewDecisionActionRecoveryStatus.Current, page.NextScanCursor, page.SourceTruncated, items);
    }

    private async Task<HumanReviewDecisionActionRecoveryItemResult> RecoverCandidateAsync(HumanReviewDecisionActionRecoveryCandidate candidate, HumanReviewDecisionActionRecoveryRequest request, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        if (!Valid(candidate)) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Invalid);
        if (observedAtUtc >= candidate.WakeExpiresAtUtc) return await RetireAsync(candidate, candidate.PriorClaim, candidate.ExpectedLifecycleVersion, HumanReviewContinuationOutcome.Expired, HumanReviewDecisionActionRetirementReason.Expired, cancellationToken).ConfigureAwait(false);
        if (!TryNow(out var claimedAtUtc) || !TryCreateClaim(candidate, request, claimedAtUtc, out var claim)) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked);
        HumanReviewDecisionActionStoreMutationResult claimed;
        try { claimed = await _store.ClaimAsync(new(candidate, claim!), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked); }
        if (claimed is null) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked);
        if (claimed.Status == HumanReviewDecisionActionStoreMutationStatus.LimitExceeded) return await RetireAsync(candidate, candidate.PriorClaim, candidate.ExpectedLifecycleVersion, HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.ClaimLimitExceeded, cancellationToken).ConfigureAwait(false);
        if (claimed.Status is not (HumanReviewDecisionActionStoreMutationStatus.Committed or HumanReviewDecisionActionStoreMutationStatus.Replayed)) return Item(candidate, claimed.Status is HumanReviewDecisionActionStoreMutationStatus.Conflict or HumanReviewDecisionActionStoreMutationStatus.NotFound ? HumanReviewDecisionActionRecoveryItemStatus.ClaimConflict : claimed.Status == HumanReviewDecisionActionStoreMutationStatus.Invalid ? HumanReviewDecisionActionRecoveryItemStatus.Invalid : HumanReviewDecisionActionRecoveryItemStatus.Parked);

        HumanReviewDecisionActionCandidateReadResult reread;
        try { reread = await _store.ReadAsync(Query(candidate, claim!), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked); }
        if (reread is null) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked);
        if (reread.Status != HumanReviewDecisionActionCandidateReadStatus.Current || reread.Candidate is null) return Item(candidate, reread.Status == HumanReviewDecisionActionCandidateReadStatus.Corrupt ? HumanReviewDecisionActionRecoveryItemStatus.Invalid : reread.Status == HumanReviewDecisionActionCandidateReadStatus.Unavailable ? HumanReviewDecisionActionRecoveryItemStatus.Parked : HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim);

        HumanReviewContinuationConsumptionResult consumed;
        try { consumed = await _consumer.ConsumeDecisionActionAsync(reread.Candidate.ConsumerCandidate, candidate.Decision, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked); }
        if (consumed is null || consumed.Status == HumanReviewContinuationConsumptionStatus.Invalid) return await RetireAsync(candidate, Reference(reread.Candidate.Claim), reread.Candidate.ConsumerCandidate.Run.LifecycleVersion, HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.Invalid, cancellationToken).ConfigureAwait(false);
        if (consumed.Status != HumanReviewContinuationConsumptionStatus.DecisionPathPrepared || consumed.Action is null || !TryCreateActionIntent(reread.Candidate, consumed.Action, out var action)) return consumed.Status == HumanReviewContinuationConsumptionStatus.Unavailable
            ? Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked)
            : await RetireAsync(candidate, Reference(reread.Candidate.Claim), reread.Candidate.ConsumerCandidate.Run.LifecycleVersion, HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.Invalid, cancellationToken).ConfigureAwait(false);

        HumanReviewDecisionActionCandidateReadResult releaseReread;
        try { releaseReread = await _store.ReadAsync(Query(candidate, claim!), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked); }
        if (releaseReread is null) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked);
        if (releaseReread.Status != HumanReviewDecisionActionCandidateReadStatus.Current || releaseReread.Candidate is null) return Item(candidate, releaseReread.Status == HumanReviewDecisionActionCandidateReadStatus.Corrupt ? HumanReviewDecisionActionRecoveryItemStatus.Invalid : releaseReread.Status == HumanReviewDecisionActionCandidateReadStatus.Unavailable ? HumanReviewDecisionActionRecoveryItemStatus.Parked : HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim);
        if (!HasSameExactActiveClaim(reread.Candidate, releaseReread.Candidate) || !TryRebuildActionIntent(releaseReread.Candidate, action!, out action)) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim);

        if (!TryNow(out var releaseAtUtc)) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked);
        if (!HasCurrentClaimAndWake(releaseReread.Candidate, releaseAtUtc))
        {
            return releaseAtUtc >= releaseReread.Candidate.Action.Wake!.ExpiresAtUtc
                ? await RetireAsync(candidate, Reference(releaseReread.Candidate.Claim), releaseReread.Candidate.ConsumerCandidate.Run.LifecycleVersion, HumanReviewContinuationOutcome.Expired, HumanReviewDecisionActionRetirementReason.Expired, cancellationToken).ConfigureAwait(false)
                : Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim);
        }

        HumanReviewDecisionActionReleaseResult released;
        try { released = await _release.ReleaseAsync(action!, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked); }
        if (released is null || released.Status == HumanReviewDecisionActionReleaseStatus.Unavailable) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked);
        if (released.Status != HumanReviewDecisionActionReleaseStatus.Completed || released.Completion is null) return await RetireAsync(candidate, Reference(releaseReread.Candidate.Claim), releaseReread.Candidate.ConsumerCandidate.Run.LifecycleVersion, HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.ReleaseInvalid, cancellationToken).ConfigureAwait(false);
        var complete = new HumanReviewDecisionActionCompletionIntent(action!.RunId, action.ExpectedLifecycleVersion, action.Wake, action.Claim, action.Reservation, action.ExpectedGeneration);
        try
        {
            var completed = await _store.CompleteAsync(complete, released.Completion, cancellationToken).ConfigureAwait(false);
            return Item(candidate, completed?.Status is HumanReviewDecisionActionStoreMutationStatus.Committed or HumanReviewDecisionActionStoreMutationStatus.Replayed ? HumanReviewDecisionActionRecoveryItemStatus.Completed : completed?.Status == HumanReviewDecisionActionStoreMutationStatus.Invalid ? HumanReviewDecisionActionRecoveryItemStatus.Invalid : HumanReviewDecisionActionRecoveryItemStatus.Parked);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked); }
    }

    private async Task<HumanReviewDecisionActionRecoveryItemResult> RetireAsync(HumanReviewDecisionActionRecoveryCandidate candidate, HumanReviewDecisionActionClaimReference? claim, int expectedLifecycleVersion, HumanReviewContinuationOutcome outcome, HumanReviewDecisionActionRetirementReason reason, CancellationToken cancellationToken)
    {
        if (!TryNow(out var retiredAtUtc) || !TryCreateRetirement(candidate, outcome, reason, retiredAtUtc, out var retirement)) return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked);
        try
        {
            var result = await _store.RetireAsync(new(candidate.RunId, expectedLifecycleVersion, candidate.Wake, claim, candidate.Reservation, candidate.ExpectedGeneration, outcome, reason), retirement!, cancellationToken).ConfigureAwait(false);
            return Item(candidate, result?.Status is HumanReviewDecisionActionStoreMutationStatus.Committed or HumanReviewDecisionActionStoreMutationStatus.Replayed ? HumanReviewDecisionActionRecoveryItemStatus.Retired : result?.Status == HumanReviewDecisionActionStoreMutationStatus.Invalid ? HumanReviewDecisionActionRecoveryItemStatus.Invalid : HumanReviewDecisionActionRecoveryItemStatus.Parked);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Item(candidate, HumanReviewDecisionActionRecoveryItemStatus.Parked); }
    }

    private static bool TryCreateActionIntent(HumanReviewDecisionActionCandidate candidate, HumanReviewContinuationActionIntent source, out HumanReviewDecisionActionIntent? action)
    {
        action = null;
        var expected = candidate.Action.Reservation.Decision;
        if (source.RunId != candidate.ConsumerCandidate.Run.Id || source.ExpectedLifecycleVersion != candidate.ConsumerCandidate.Run.LifecycleVersion || !Equals(source.Request, candidate.Action.Reservation.Request) || !Equals(source.Decision, expected) || source.Action != ExpectedAction(expected.Kind) || candidate.Action.Wake is null) return false;
        action = new(source.RunId, source.ExpectedLifecycleVersion, source.Request, source.Decision, Reference(candidate.Action.Wake), Reference(candidate.Claim), Reference(candidate.Action.Reservation), candidate.Action.ExpectedGeneration, Id("action-operation", candidate.Action.Reservation.ReservationHash));
        return true;
    }

    private static bool TryRebuildActionIntent(HumanReviewDecisionActionCandidate candidate, HumanReviewDecisionActionIntent source, out HumanReviewDecisionActionIntent? action)
    {
        action = null;
        if (candidate.Action.Wake is not { } wake || source.RunId != candidate.ConsumerCandidate.Run.Id || !Equals(source.Request, candidate.Action.Reservation.Request) || !Equals(source.Decision, candidate.Action.Reservation.Decision) || !Equals(source.Wake, Reference(wake)) || !Equals(source.Claim, Reference(candidate.Claim)) || !Equals(source.Reservation, Reference(candidate.Action.Reservation)) || source.ExpectedGeneration != candidate.Action.ExpectedGeneration || source.ActionOperationId != Id("action-operation", candidate.Action.Reservation.ReservationHash)) return false;
        action = new(candidate.ConsumerCandidate.Run.Id, candidate.ConsumerCandidate.Run.LifecycleVersion, candidate.Action.Reservation.Request, candidate.Action.Reservation.Decision, Reference(wake), Reference(candidate.Claim), Reference(candidate.Action.Reservation), candidate.Action.ExpectedGeneration, source.ActionOperationId);
        return true;
    }

    private static bool TryCreateClaim(HumanReviewDecisionActionRecoveryCandidate candidate, HumanReviewDecisionActionRecoveryRequest request, DateTimeOffset claimedAtUtc, out HumanReviewDecisionActionClaim? claim)
    {
        claim = null;
        var requestedExpiresAtUtc = claimedAtUtc + request.ClaimLease;
        var expiresAtUtc = requestedExpiresAtUtc <= candidate.WakeExpiresAtUtc ? requestedExpiresAtUtc : candidate.WakeExpiresAtUtc;
        if (!Utc(claimedAtUtc) || claimedAtUtc >= candidate.WakeExpiresAtUtc || expiresAtUtc <= claimedAtUtc) return false;
        var claimId = Id("action-claim", candidate.Reservation.ReservationHash + "|" + request.WorkerId + "|" + claimedAtUtc.UtcTicks);
        claim = HumanReviewDecisionActionContractHash.ApplyClaim(new(1, claimId, candidate.Wake, candidate.Reservation, candidate.ExpectedGeneration, request.WorkerId, claimedAtUtc, expiresAtUtc, Provenance("human-review-action-recovery", claimId, claimedAtUtc), string.Empty));
        return true;
    }

    private static bool TryCreateRetirement(HumanReviewDecisionActionRecoveryCandidate candidate, HumanReviewContinuationOutcome outcome, HumanReviewDecisionActionRetirementReason reason, DateTimeOffset retiredAtUtc, out HumanReviewDecisionActionRetirement? retirement)
    {
        retirement = null;
        var id = Id("action-retirement", candidate.Reservation.ReservationHash + "|" + candidate.ExpectedGeneration + "|" + (int)outcome + "|" + (int)reason);
        retirement = HumanReviewDecisionActionContractHash.ApplyRetirement(new(1, id, candidate.Wake, candidate.Reservation, candidate.ExpectedGeneration, outcome, reason, retiredAtUtc, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("human-review-action-recovery", id, retiredAtUtc), string.Empty));
        return true;
    }

    private bool TryNow(out DateTimeOffset value) { try { value = _clock.UtcNow; return Utc(value); } catch { value = default; return false; } }
    private static bool Valid(HumanReviewDecisionActionRecoveryRequest? value) => value is not null && value.MaximumCount is >= 1 and <= CustomLoopLimits.MaxRecentRunsPageSize && HumanReviewIdentifier.IsValid(value.WorkerId) && value.ClaimLease > TimeSpan.Zero && value.ClaimLease <= HumanReviewContractLimits.MaxContinuationClaimLease;
    private static bool Valid(HumanReviewDecisionActionRecoveryCandidate? value) => value is not null && CustomLoopArtifactIdentifier.IsValid(value.RunId) && value.ExpectedLifecycleVersion >= 1 && value.ExpectedGeneration >= 1 && value.Decision.Kind is HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation && Utc(value.WakeExpiresAtUtc);
    private static bool HasCurrentClaimAndWake(HumanReviewDecisionActionCandidate candidate, DateTimeOffset observedAtUtc)
        => candidate.Action.Wake is { } wake
            && candidate.Action.ExpectedGeneration == candidate.Claim.ExpectedGeneration
            && Equals(Reference(wake), candidate.Claim.Wake)
            && Equals(Reference(candidate.Action.Reservation), candidate.Claim.Reservation)
            && candidate.Action.Claims is { Length: > 0 }
            && Equals(Reference(candidate.Action.Claims[^1]), Reference(candidate.Claim))
            && Utc(observedAtUtc)
            && observedAtUtc >= wake.PublishedAtUtc
            && observedAtUtc < wake.ExpiresAtUtc
            && observedAtUtc >= candidate.Claim.ClaimedAtUtc
            && observedAtUtc < candidate.Claim.LeaseExpiresAtUtc;
    private static bool HasSameExactActiveClaim(HumanReviewDecisionActionCandidate previous, HumanReviewDecisionActionCandidate current)
        => !current.ConsumerCandidate.Run.IsTerminal
            && previous.Action.Wake is { } previousWake
            && current.Action.Wake is { } currentWake
            && current.Action.Completion is null
            && current.Action.Retirement is null
            && current.Action.Claims is { Length: > 0 }
            && Equals(Reference(previousWake), Reference(currentWake))
            && Equals(Reference(previous.Action.Reservation), Reference(current.Action.Reservation))
            && previous.Action.ExpectedGeneration == current.Action.ExpectedGeneration
            && Equals(Reference(previous.Claim), Reference(current.Claim))
            && Equals(Reference(current.Action.Claims[^1]), Reference(current.Claim));
    private static HumanReviewDecisionActionCandidateQuery Query(HumanReviewDecisionActionRecoveryCandidate candidate, HumanReviewDecisionActionClaim claim)
        => new(candidate.RunId, candidate.Request, candidate.Decision, candidate.Wake, Reference(claim), candidate.Reservation, candidate.ExpectedGeneration);
    private static HumanReviewContinuationAction ExpectedAction(HumanReviewDecisionKind kind) => kind switch { HumanReviewDecisionKind.Reject => HumanReviewContinuationAction.FailRejected, HumanReviewDecisionKind.Cancel => HumanReviewContinuationAction.Cancel, HumanReviewDecisionKind.RequestInformation => HumanReviewContinuationAction.ParkForInformation, _ => HumanReviewContinuationAction.None };
    private static HumanReviewProvenance Provenance(string source, string correlation, DateTimeOffset atUtc) => HumanReviewContractHash.ApplyProvenance(new(HumanReviewProvenanceKind.Coordinator, source, correlation, atUtc, string.Empty));
    private static HumanReviewDecisionActionClaimReference Reference(HumanReviewDecisionActionClaim value) => new(value.ClaimId, value.ClaimHash);
    private static HumanReviewDecisionActionReservationReference Reference(HumanReviewDecisionActionReservation value) => new(value.ReservationId, value.ReservationHash);
    private static HumanReviewDecisionActionWakeReference Reference(HumanReviewDecisionActionWake value) => new(value.WakeId, value.WakeHash);
    private static HumanReviewDecisionActionRecoveryItemResult Item(HumanReviewDecisionActionRecoveryCandidate candidate, HumanReviewDecisionActionRecoveryItemStatus status) => new(candidate, status);
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static string Id(string prefix, string value) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
