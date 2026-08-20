using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules;

/// <summary>Resolves fresh, non-authoritative evidence for one claimed occurrence.</summary>
public interface IScheduleCurrentEvidencePort
{
    /// <summary>Resolves current target, adapter, actor, profile, recurrence permission, payload bytes, and the exact later observation instant.</summary>
    Task<ScheduleCurrentEvidenceResult> ResolveAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence occurrence,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}
