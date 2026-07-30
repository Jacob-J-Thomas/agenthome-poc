using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
namespace EmbodySense.Core.Common.Loops.Custom.Execution;

public sealed record CustomLoopExecutionClock(
    long AccumulatedRunningMilliseconds,
    DateTimeOffset? ActiveSinceUtc)
{
    public static CustomLoopExecutionClock NotStarted()
    {
        return new CustomLoopExecutionClock(0, null);
    }
}
