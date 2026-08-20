namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Retains bounded evidence that one exact contiguous occurrence range was skipped or deferred.</summary>
/// <remarks>Misfire skips may aggregate a range. Invalid-local and overlap decisions remain exact singleton ranges. Invalid local times have no UTC mapping.</remarks>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="FirstOrdinal">The first positive occurrence ordinal in the range.</param>
/// <param name="LastOrdinal">The last positive occurrence ordinal in the range.</param>
/// <param name="Count">The exact contiguous ordinal count.</param>
/// <param name="FirstScheduledLocal">The first unqualified local wall-clock occurrence.</param>
/// <param name="LastScheduledLocal">The last unqualified local wall-clock occurrence.</param>
/// <param name="FirstScheduledAtUtc">The first selected UTC occurrence, or null only for an invalid local time.</param>
/// <param name="LastScheduledAtUtc">The last selected UTC occurrence, or null only for an invalid local time.</param>
/// <param name="TimeZone">The exact time-zone identifier and rules fingerprint used for the decision.</param>
/// <param name="Disposition">The closed skip/defer disposition.</param>
/// <param name="DecisionEvidenceHash">An optional exact policy/source evidence hash; required for overlap decisions.</param>
/// <param name="ReasonCode">The bounded stable reason code.</param>
/// <param name="RecordedAtUtc">When the disposition evidence was recorded.</param>
public sealed record ScheduleOccurrenceDispositionEvidence(
    int SchemaVersion,
    long FirstOrdinal,
    long LastOrdinal,
    long Count,
    DateTime FirstScheduledLocal,
    DateTime LastScheduledLocal,
    DateTimeOffset? FirstScheduledAtUtc,
    DateTimeOffset? LastScheduledAtUtc,
    ScheduleTimeZoneReference TimeZone,
    ScheduleOccurrenceDisposition Disposition,
    string? DecisionEvidenceHash,
    string ReasonCode,
    DateTimeOffset RecordedAtUtc)
{
    /// <summary>Gets the only supported disposition-evidence schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
