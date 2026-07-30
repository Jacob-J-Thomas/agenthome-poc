using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Tracks accumulated running time independently from paused wall-clock time.
/// </summary>
/// <param name="AccumulatedRunningMilliseconds">Whole milliseconds committed from completed running intervals.</param>
/// <param name="ActiveSinceUtc">The UTC start of the current running interval, or <see langword="null"/> while not actively running.</param>
public sealed record CustomLoopExecutionClock(
    long AccumulatedRunningMilliseconds,
    DateTimeOffset? ActiveSinceUtc)
{
    /// <summary>
    /// Creates a clock with no accumulated or active running interval.
    /// </summary>
    /// <returns>A zero-millisecond inactive clock.</returns>
    public static CustomLoopExecutionClock NotStarted()
    {
        return new CustomLoopExecutionClock(0, null);
    }
}
