using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewOrderedReleaseProcessEffectIntentFactory
{
    internal static async Task<HumanReviewOrderedReleaseProcessEffectIntent?> CreateAsync(CustomLoopRunRecord? run, IGovernedLoopEffectAttemptReadStore attempts)
    {
        var review = run?.HumanReview;
        var state = review?.Continuation;
        var wake = state?.Wake;
        var claim = state?.Claims.LastOrDefault();
        var reservation = review?.ContinuationReservation;
        var decision = review?.AcceptedTerminalDecision;
        var effectBinding = review?.Request.Binding.EffectAttempt;
        if (run is null
            || review is null
            || wake is null
            || claim is null
            || reservation is null
            || decision?.Kind != HumanReviewDecisionKind.Approve
            || effectBinding is null)
        {
            return null;
        }

        var read = await attempts.ReadAsync(review.Request.Binding.WorkspaceId, effectBinding.OperationId, effectBinding.EffectGeneration);
        if (read.Status != GovernedLoopEffectAttemptReadStatus.Current || read.Attempt is null)
        {
            return null;
        }

        var request = new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash);
        var decisionReference = new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash);
        var wakeReference = new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash);
        var claimReference = new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash);
        var reservationReference = new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash);
        var identity = HumanReviewEffectReleaseContract.CreateIdentity(review.Request.Binding, read.Attempt);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(review.Request.Binding, read.Attempt);
        var releaseOperationId = state!.Completion?.ReleaseReceipt.ReleaseOperationId
            ?? HumanReviewContinuationReleaseOperationId.Create(request, wakeReference, reservationReference, wake.ExpectedGeneration, HumanReviewContinuationReleaseKind.PreDispatchEffect);
        var effectReceiptHash = state.Completion?.ReleaseReceipt.EffectReceiptHash
            ?? HumanReviewEffectReleaseContract.Create(review.Request.Binding, read.Attempt, read.Attempt.Payload.UpdatedAtUtc).SnapshotHash;
        if (releaseOperationId is null || effectReceiptHash is null)
        {
            return null;
        }

        var receipt = new HumanReviewContinuationReleaseReceiptIntent(
            releaseOperationId,
            request,
            wakeReference,
            claimReference,
            reservationReference,
            wake.ExpectedGeneration,
            HumanReviewContinuationReleaseKind.PreDispatchEffect,
            effectReceiptHash);
        var action = new HumanReviewContinuationActionIntent(
            HumanReviewContinuationAction.ReleaseEffect,
            run.Id,
            run.LifecycleVersion,
            request,
            decisionReference,
            wakeReference,
            claimReference,
            reservationReference,
            wake.ExpectedGeneration,
            new GovernedLoopEffectCertaintySnapshotQuery(identity, preparation),
            receipt);
        var completion = new HumanReviewContinuationCompletionIntent(
            run.Id,
            run.LifecycleVersion,
            request,
            wakeReference,
            claimReference,
            reservationReference,
            wake.ExpectedGeneration,
            receipt);
        var releaseAtUtc = new[] { run.UpdatedAtUtc, claim.ClaimedAtUtc, read.Attempt.Payload.UpdatedAtUtc }.Max().AddTicks(1);
        return new HumanReviewOrderedReleaseProcessEffectIntent(action, completion, releaseAtUtc);
    }
}
