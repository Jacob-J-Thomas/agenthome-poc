using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Loops.Schedules.Models;

/// <summary>Contains only bounded untrusted schedule authoring intent from a visible interface surface.</summary>
/// <remarks>
/// The input deliberately carries no actor, workspace, role, authority, publication, grant, adapter, payload,
/// schedule identity, rules fingerprint, UTC conversion, or state revision. Core.Startup derives those values from
/// current canonical evidence before a durable definition is created.
/// </remarks>
/// <param name="OperationId">The caller-retained stable idempotency identity for one create or successor edit.</param>
/// <param name="GraphId">The selected stable graph identifier.</param>
/// <param name="RevisionId">The selected immutable published graph revision.</param>
/// <param name="ExpectedGraphLifecycleVersion">The exact lifecycle version observed by the caller.</param>
/// <param name="ExpectedAuthorityPreviewHash">The optional server-derived confirmation preview hash being acknowledged.</param>
/// <param name="RecurrenceKind">The requested closed recurrence kind.</param>
/// <param name="FirstLocalOccurrence">The requested local wall-clock anchor with no UTC offset.</param>
/// <param name="FixedIntervalSeconds">The requested exact interval when <paramref name="RecurrenceKind"/> is fixed interval.</param>
/// <param name="TimeZoneId">The requested exact time-zone identifier from the server-composed rule snapshot.</param>
/// <param name="InvalidLocalTime">The requested gap policy.</param>
/// <param name="AmbiguousLocalTime">The requested fold policy.</param>
/// <param name="MisfireKind">The requested closed missed-occurrence policy.</param>
/// <param name="CatchUpLimit">The requested bounded catch-up limit.</param>
/// <param name="Overlap">The requested overlap policy.</param>
/// <param name="Priority">The requested bounded queue priority.</param>
/// <param name="Enabled">Whether the revision-1 state should initially be eligible for due-occurrence claims.</param>
public sealed record GovernedLoopScheduleAuthoringInput(
    string OperationId,
    string GraphId,
    string RevisionId,
    long ExpectedGraphLifecycleVersion,
    string? ExpectedAuthorityPreviewHash,
    ScheduleRecurrenceKind RecurrenceKind,
    DateTime FirstLocalOccurrence,
    long? FixedIntervalSeconds,
    string TimeZoneId,
    ScheduleInvalidLocalTimePolicy InvalidLocalTime,
    ScheduleAmbiguousLocalTimePolicy AmbiguousLocalTime,
    ScheduleMisfirePolicyKind MisfireKind,
    int CatchUpLimit,
    ScheduleOverlapPolicy Overlap,
    SchedulePriority Priority,
    bool Enabled);
