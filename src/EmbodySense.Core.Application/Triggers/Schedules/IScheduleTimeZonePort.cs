using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules;

/// <summary>Maps unqualified local schedule times through one exact time-zone rules source.</summary>
public interface IScheduleTimeZonePort
{
    /// <summary>Resolves a unique instant, a gap with its first valid instant, or an ordered fold pair.</summary>
    Task<ScheduleTimeZoneResolution> ResolveLocalAsync(
        ScheduleTimeZoneReference timeZone,
        DateTime scheduledLocal,
        CancellationToken cancellationToken = default);

    /// <summary>Maps one exact UTC instant back to its unqualified local wall-clock value.</summary>
    Task<ScheduleInstantResolution> ResolveInstantAsync(
        ScheduleTimeZoneReference timeZone,
        DateTimeOffset scheduledAtUtc,
        CancellationToken cancellationToken = default);
}
