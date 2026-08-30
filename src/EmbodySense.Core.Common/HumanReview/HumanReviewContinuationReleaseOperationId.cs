using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Derives the stable idempotency identity for one Human Review continuation release.</summary>
/// <remarks>The worker claim is deliberately excluded so a strict-expiry takeover cannot create another irreversible release boundary.</remarks>
public static class HumanReviewContinuationReleaseOperationId
{
    /// <summary>Creates the deterministic release operation identifier for one request, wake, reservation, generation, and release kind.</summary>
    /// <param name="request">The exact reviewed request reference.</param>
    /// <param name="wake">The exact published continuation wake reference.</param>
    /// <param name="reservation">The exact accepted continuation reservation reference.</param>
    /// <param name="expectedGeneration">The exact wake generation.</param>
    /// <param name="kind">The exact governed release boundary.</param>
    /// <returns>The valid canonical identifier, or <see langword="null"/> when the inputs cannot form one schema-1 release identity.</returns>
    public static string? Create(HumanReviewRequestReference? request, HumanReviewContinuationWakeReference? wake, HumanReviewContinuationReservationReference? reservation, long expectedGeneration, HumanReviewContinuationReleaseKind kind)
    {
        try
        {
            if (request is null || wake is null || reservation is null || expectedGeneration <= 0 || kind == HumanReviewContinuationReleaseKind.Unknown || !Enum.IsDefined(kind)) return null;
            var material = string.Join('|', "human-review-continuation-release-operation-v1", request.RequestId, request.RequestHash, wake.WakeId, wake.WakeHash, reservation.ReservationId, reservation.ReservationHash, expectedGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture), ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var identifier = "release-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
            return HumanReviewIdentifier.IsValid(identifier) ? identifier : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Determines whether an untrusted identifier is the sole canonical release identity for its immutable release inputs.</summary>
    /// <param name="operationId">The untrusted pre-bound operation identifier.</param>
    /// <param name="request">The exact reviewed request reference.</param>
    /// <param name="wake">The exact published continuation wake reference.</param>
    /// <param name="reservation">The exact accepted continuation reservation reference.</param>
    /// <param name="expectedGeneration">The exact wake generation.</param>
    /// <param name="kind">The exact governed release boundary.</param>
    /// <returns><see langword="true"/> only when the identifier equals the canonical release identity.</returns>
    public static bool Matches(string? operationId, HumanReviewRequestReference? request, HumanReviewContinuationWakeReference? wake, HumanReviewContinuationReservationReference? reservation, long expectedGeneration, HumanReviewContinuationReleaseKind kind)
        => string.Equals(operationId, Create(request, wake, reservation, expectedGeneration, kind), StringComparison.Ordinal);
}
