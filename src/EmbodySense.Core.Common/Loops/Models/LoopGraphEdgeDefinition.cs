namespace EmbodySense.Core.Common.Loops.Models;

public sealed record LoopGraphEdgeDefinition(
    string Id,
    string FromNodeId,
    string ToNodeId,
    LoopGraphEdgeCondition Condition,
    string Description);
