namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Reports accumulated execution time and the current active interval for a run.
/// </summary>
/// <param name="AccumulatedRunningMilliseconds">The accumulated running milliseconds.</param>
/// <param name="ActiveSinceUtc">The active since utc.</param>
public sealed record LoopRunExecutionClockSnapshot(
    long AccumulatedRunningMilliseconds,
    DateTimeOffset? ActiveSinceUtc);
