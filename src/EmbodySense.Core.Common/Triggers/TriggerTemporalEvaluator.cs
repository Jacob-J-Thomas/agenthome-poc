using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Classifies structurally valid temporal evidence at a caller-supplied UTC instant.
/// </summary>
public static class TriggerTemporalEvaluator
{
    /// <summary>
    /// Evaluates exact endpoints without reading a wall clock.
    /// </summary>
    /// <remarks>Not-before is inclusive, deadline is inclusive, and expiry is exclusive-validity with expiry taking precedence.</remarks>
    /// <param name="evidence">The validated temporal evidence.</param>
    /// <param name="evaluatedAtUtc">The exact UTC instant to classify.</param>
    /// <returns>The closed temporal state, or <see cref="TriggerTemporalState.Unknown"/> for invalid evidence or a non-UTC instant.</returns>
    public static TriggerTemporalState Evaluate(TriggerTemporalEvidence? evidence, DateTimeOffset evaluatedAtUtc)
    {
        if (evaluatedAtUtc.Offset != TimeSpan.Zero || TriggerDeliveryValidator.ValidateTemporal(evidence).Count > 0)
        {
            return TriggerTemporalState.Unknown;
        }

        if (evidence!.ExpiresAtUtc is { } expiresAtUtc && evaluatedAtUtc >= expiresAtUtc)
        {
            return TriggerTemporalState.Expired;
        }

        if (evidence.DeadlineUtc is { } deadlineUtc && evaluatedAtUtc > deadlineUtc)
        {
            return TriggerTemporalState.DeadlineExceeded;
        }

        if (evidence.NotBeforeUtc is { } notBeforeUtc && evaluatedAtUtc < notBeforeUtc)
        {
            return TriggerTemporalState.NotYetEligible;
        }

        return TriggerTemporalState.Eligible;
    }
}
