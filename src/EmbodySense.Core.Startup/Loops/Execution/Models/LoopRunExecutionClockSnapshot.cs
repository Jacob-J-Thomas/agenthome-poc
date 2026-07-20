namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunExecutionClockSnapshot(
    long AccumulatedRunningMilliseconds,
    DateTimeOffset? ActiveSinceUtc);
