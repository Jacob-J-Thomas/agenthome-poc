namespace EmbodySense.Core.Startup.Loops.Schedules.Models;

/// <summary>Projects only the bounded visible authoring and state fields for one canonical schedule.</summary>
/// <remarks>
/// This is deliberately not a copy of the persisted schedule definition or state. Actor, surface, workspace, role,
/// authority profile, grant, payload reference and digest, publication evidence, time-zone fingerprint, pending delivery,
/// terminal evidence, and other operational evidence remain server-owned and are never serialized to Web clients.
/// </remarks>
/// <param name="ScheduleId">The opaque canonical schedule identity.</param>
/// <param name="GraphId">The exact graph selected by the schedule's immutable published target.</param>
/// <param name="RevisionId">The exact immutable graph revision selected by that target.</param>
/// <param name="Enabled">Whether the current state permits a new due-occurrence claim.</param>
/// <param name="StateRevision">The exact optimistic schedule-state revision for existing operational controls.</param>
/// <param name="NextOccurrenceAtUtc">The next due occurrence, when one remains.</param>
/// <param name="RecurrenceKind">The closed recurrence token.</param>
/// <param name="FirstLocalOccurrence">The configured local wall-clock anchor.</param>
/// <param name="FixedIntervalSeconds">The configured fixed interval, when applicable.</param>
/// <param name="TimeZoneId">The configured visible time-zone identifier.</param>
/// <param name="InvalidLocalTimePolicy">The configured local-time gap policy token.</param>
/// <param name="AmbiguousLocalTimePolicy">The configured local-time fold policy token.</param>
/// <param name="MisfirePolicy">The configured missed-occurrence policy token.</param>
/// <param name="CatchUpLimit">The configured bounded catch-up limit.</param>
/// <param name="OverlapPolicy">The configured overlap policy token.</param>
/// <param name="Priority">The configured queue priority token.</param>
public sealed record GovernedLoopScheduleAuthoringSnapshot(
    string ScheduleId,
    string GraphId,
    string RevisionId,
    bool Enabled,
    long StateRevision,
    DateTimeOffset? NextOccurrenceAtUtc,
    string RecurrenceKind,
    DateTime FirstLocalOccurrence,
    long? FixedIntervalSeconds,
    string TimeZoneId,
    string InvalidLocalTimePolicy,
    string AmbiguousLocalTimePolicy,
    string MisfirePolicy,
    int CatchUpLimit,
    string OverlapPolicy,
    string Priority);
