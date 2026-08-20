namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Retains one exact queue-admission observation with a pending delivery.</summary>
/// <remarks>Unavailable and ambiguous results remain evidence only and never imply delivery.</remarks>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="Kind">The closed queue-admission observation.</param>
/// <param name="ReasonCode">The bounded stable adapter/application reason code.</param>
/// <param name="CanonicalEnvelopeHash">The lowercase SHA-256 hash of the exact trigger envelope submitted to admission.</param>
/// <param name="RecordedAtUtc">When the result evidence was recorded.</param>
public sealed record ScheduleDeliveryResultEvidence(
    int SchemaVersion,
    ScheduleDeliveryResultKind Kind,
    string ReasonCode,
    string CanonicalEnvelopeHash,
    DateTimeOffset RecordedAtUtc)
{
    /// <summary>Gets the only supported result-evidence schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
