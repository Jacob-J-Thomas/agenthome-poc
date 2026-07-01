namespace EmbodySense.Core.Common.Loops.Models;

public enum LoopGraphEdgeCondition
{
    Unknown = 0,
    Always,
    Success,
    Failure,
    Cancellation,
    AuthorityBoundary
}
