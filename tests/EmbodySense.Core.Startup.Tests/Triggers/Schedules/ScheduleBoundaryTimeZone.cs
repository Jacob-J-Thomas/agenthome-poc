using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

internal sealed class ScheduleBoundaryTimeZone : IScheduleTimeZonePort
{
    public Task<ScheduleTimeZoneResolution> ResolveLocalAsync(
        ScheduleTimeZoneReference timeZone,
        DateTime scheduledLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ScheduleTimeZoneResolution(
            ScheduleTimeZoneResolutionStatus.Unique,
            timeZone.RulesFingerprint,
            scheduledLocal,
            new DateTimeOffset(scheduledLocal.AddHours(5), TimeSpan.Zero),
            null));
    }

    public Task<ScheduleInstantResolution> ResolveInstantAsync(
        ScheduleTimeZoneReference timeZone,
        DateTimeOffset scheduledAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ScheduleInstantResolution(
            ScheduleInstantResolutionStatus.Resolved,
            timeZone.RulesFingerprint,
            DateTime.SpecifyKind(scheduledAtUtc.UtcDateTime.AddHours(-5), DateTimeKind.Unspecified)));
    }
}
