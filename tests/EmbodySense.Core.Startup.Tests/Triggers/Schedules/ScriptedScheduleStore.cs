using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

internal sealed class ScriptedScheduleStore : IScheduleStorePort
{
    internal int ReadCallCount { get; private set; }

    internal Func<ScheduleId, CancellationToken, Task<ScheduleStoreReadResult>> ReadBehavior { get; set; }
        = (_, _) => Task.FromResult(new ScheduleStoreReadResult(ScheduleStoreReadStatus.NotFound, null, null));

    internal Func<ScheduleStoreCreateRequest, CancellationToken, Task<ScheduleStoreMutationResult>> CreateBehavior { get; set; }
        = (_, _) => Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Corrupt, null));

    internal Func<ScheduleStateCompareExchange, CancellationToken, Task<ScheduleStoreMutationResult>> CompareExchangeBehavior { get; set; }
        = (_, _) => Task.FromResult(new ScheduleStoreMutationResult(ScheduleStoreMutationStatus.Corrupt, null));

    public Task<ScheduleStoreReadResult> ReadAsync(ScheduleId scheduleId, CancellationToken cancellationToken = default)
    {
        ReadCallCount++;
        return ReadBehavior(scheduleId, cancellationToken);
    }

    public Task<ScheduleStoreMutationResult> CreateAsync(
        ScheduleStoreCreateRequest request,
        CancellationToken cancellationToken = default)
        => CreateBehavior(request, cancellationToken);

    public Task<ScheduleStoreMutationResult> CompareExchangeAsync(
        ScheduleStateCompareExchange request,
        CancellationToken cancellationToken = default)
        => CompareExchangeBehavior(request, cancellationToken);
}
