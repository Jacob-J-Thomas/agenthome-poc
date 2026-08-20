using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines one immutable, revision-pinned, authority-neutral time schedule.</summary>
/// <remarks>The definition contains references and hashes only. It never grants authority, resolves current time-zone rules, or carries secret values.</remarks>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="ScheduleId">The stable schedule identity.</param>
/// <param name="Revision">The positive immutable definition revision.</param>
/// <param name="Target">The exact published governed-loop revision and immutable authority-grant revision.</param>
/// <param name="TimeAdapter">The exact reviewed time-trigger adapter capability and implementation.</param>
/// <param name="ActorId">The exact actor provenance expected at occurrence authority resolution.</param>
/// <param name="SurfaceId">The exact bounded surface identity expected by trigger admission.</param>
/// <param name="WorkspaceId">The exact workspace scope.</param>
/// <param name="RoleId">The exact contextual-role identity.</param>
/// <param name="AuthorityProfile">The authority-profile revision that current evidence must re-prove.</param>
/// <param name="Payload">The governed payload reference and exact content hash.</param>
/// <param name="Priority">The bounded requested queue priority.</param>
/// <param name="Recurrence">The closed recurrence rule.</param>
/// <param name="TimeZone">The time-zone identifier and exact rules fingerprint.</param>
/// <param name="DaylightSaving">The explicit local gap/fold policy.</param>
/// <param name="Misfire">The explicit bounded misfire policy.</param>
/// <param name="Overlap">The explicit overlap policy.</param>
/// <param name="Enabled">Whether the revision permits a new due-occurrence claim.</param>
public sealed record ScheduleDefinition(
    int SchemaVersion,
    ScheduleId ScheduleId,
    long Revision,
    TriggerLoopReference Target,
    TriggerAdapterReference TimeAdapter,
    AuthorityActorId ActorId,
    string SurfaceId,
    string WorkspaceId,
    string RoleId,
    AuthorityProfileReference AuthorityProfile,
    SchedulePayloadReference Payload,
    SchedulePriority Priority,
    ScheduleRecurrenceRule Recurrence,
    ScheduleTimeZoneReference TimeZone,
    ScheduleDaylightSavingPolicy DaylightSaving,
    ScheduleMisfirePolicy Misfire,
    ScheduleOverlapPolicy Overlap,
    bool Enabled)
{
    /// <summary>Gets the only supported definition schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
