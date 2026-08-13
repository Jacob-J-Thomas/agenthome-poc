using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Triggers.Schedules;

/// <summary>Persists immutable schedule definitions and optimistic crash-safe state snapshots.</summary>
public interface IScheduleStorePort
{
    /// <summary>Reads one exact definition and state snapshot.</summary>
    Task<ScheduleStoreReadResult> ReadAsync(ScheduleId scheduleId, CancellationToken cancellationToken = default);

    /// <summary>Creates one definition and initial state atomically.</summary>
    Task<ScheduleStoreMutationResult> CreateAsync(ScheduleStoreCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces state only when the persisted state exactly matches the expected snapshot.</summary>
    Task<ScheduleStoreMutationResult> CompareExchangeAsync(ScheduleStateCompareExchange request, CancellationToken cancellationToken = default);
}
