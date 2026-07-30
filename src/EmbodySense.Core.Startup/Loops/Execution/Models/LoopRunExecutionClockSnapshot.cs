namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunExecutionClockSnapshot(
    long AccumulatedRunningMilliseconds,
    DateTimeOffset? ActiveSinceUtc);
