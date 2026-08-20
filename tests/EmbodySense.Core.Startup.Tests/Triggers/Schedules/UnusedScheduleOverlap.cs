using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

internal sealed class UnusedScheduleOverlap : IScheduleOverlapPort
{
    public Task<ScheduleOverlapResult> GetStatusAsync(
        TriggerLoopReference target,
        ScheduleOccurrenceIdentity occurrenceIdentity,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The boundary test unexpectedly evaluated overlap.");
}
