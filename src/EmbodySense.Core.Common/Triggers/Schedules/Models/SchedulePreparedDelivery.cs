using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Persists the exact immutable trigger envelope before the first queue-admission attempt.</summary>
/// <remarks>The embedded authority receipt is evidence, never a reusable current grant. Recovery must obtain and evaluate fresh current evidence before retrying admission.</remarks>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="Envelope">The exact canonical envelope to reconcile or submit.</param>
/// <param name="CanonicalEnvelopeHash">The lowercase SHA-256 hash of <paramref name="Envelope"/>.</param>
/// <param name="PreparedAtUtc">When this exact envelope became durable.</param>
public sealed record SchedulePreparedDelivery(
    int SchemaVersion,
    TriggerDeliveryEnvelope Envelope,
    string CanonicalEnvelopeHash,
    DateTimeOffset PreparedAtUtc)
{
    /// <summary>Gets the only supported prepared-delivery schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
