namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

internal sealed class MutableScheduleTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    internal DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
