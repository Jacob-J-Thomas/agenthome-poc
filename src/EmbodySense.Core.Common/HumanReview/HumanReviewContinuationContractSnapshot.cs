using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Captures independent, bounded continuation state snapshots before durable validation or storage.</summary>
public static class HumanReviewContinuationContractSnapshot
{
    /// <summary>Captures a validated independent continuation-state snapshot against the exact request and reservation.</summary>
    public static bool TryCaptureState(HumanReviewRequest? request, HumanReviewContinuationReservation? reservation, HumanReviewContinuationState? state, out HumanReviewContinuationState? snapshot, out HumanReviewContractValidationResult validation)
    {
        if (state is null)
        {
            snapshot = null;
            validation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, null);
            return false;
        }
        try
        {
            snapshot = Copy(state);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = new HumanReviewContractValidationResult([new HumanReviewContractValidationError("continuation_snapshot_unstable", "$", "The bounded continuation state changed while its snapshot was captured.")]);
            return false;
        }
        validation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, snapshot);
        if (validation.IsValid) return true;
        snapshot = null;
        return false;
    }

    private static HumanReviewContinuationState Copy(HumanReviewContinuationState state) => state with { Wake = Copy(state.Wake), Claims = state.Claims.IsDefault ? default : ImmutableCollectionsMarshal.AsImmutableArray(state.Claims.Select(Copy).ToArray()), Completion = state.Completion is null ? null : Copy(state.Completion), Retirement = state.Retirement is null ? null : Copy(state.Retirement) };
    private static HumanReviewContinuationWake Copy(HumanReviewContinuationWake wake) => wake with { Request = wake.Request is null ? null! : wake.Request with { }, Decision = wake.Decision is null ? null! : wake.Decision with { }, Reservation = wake.Reservation is null ? null! : wake.Reservation with { }, Provenance = wake.Provenance is null ? null! : wake.Provenance with { } };
    private static HumanReviewContinuationClaim Copy(HumanReviewContinuationClaim claim) => claim with { Wake = claim.Wake is null ? null! : claim.Wake with { }, Reservation = claim.Reservation is null ? null! : claim.Reservation with { }, Provenance = claim.Provenance is null ? null! : claim.Provenance with { } };
    private static HumanReviewContinuationCompletion Copy(HumanReviewContinuationCompletion completion) => completion with { Wake = completion.Wake is null ? null! : completion.Wake with { }, Claim = completion.Claim is null ? null! : completion.Claim with { }, Reservation = completion.Reservation is null ? null! : completion.Reservation with { }, ReleaseReceipt = Copy(completion.ReleaseReceipt), Evidence = Copy(completion.Evidence), Provenance = completion.Provenance is null ? null! : completion.Provenance with { } };
    private static HumanReviewContinuationRetirement Copy(HumanReviewContinuationRetirement retirement) => retirement with { Wake = retirement.Wake is null ? null! : retirement.Wake with { }, Reservation = retirement.Reservation is null ? null! : retirement.Reservation with { }, Evidence = Copy(retirement.Evidence), Provenance = retirement.Provenance is null ? null! : retirement.Provenance with { } };
    private static HumanReviewContinuationReleaseReceipt Copy(HumanReviewContinuationReleaseReceipt receipt) => receipt is null ? null! : receipt with { Wake = receipt.Wake is null ? null! : receipt.Wake with { }, Claim = receipt.Claim is null ? null! : receipt.Claim with { }, Reservation = receipt.Reservation is null ? null! : receipt.Reservation with { } };
    private static ImmutableArray<HumanReviewRedactedPreview> Copy(ImmutableArray<HumanReviewRedactedPreview> values) => values.IsDefault ? default : ImmutableCollectionsMarshal.AsImmutableArray(values.Select(value => value is null ? null! : value with { }).ToArray());
}
