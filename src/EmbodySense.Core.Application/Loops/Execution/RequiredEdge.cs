using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

internal sealed record RequiredEdge(string Id, string FromNodeId, string ToNodeId, LoopGraphEdgeCondition Condition)
{
    public bool Matches(LoopGraphEdgeDefinition edge)
    {
        return string.Equals(edge.Id, Id, StringComparison.Ordinal)
            && string.Equals(edge.FromNodeId, FromNodeId, StringComparison.Ordinal)
            && string.Equals(edge.ToNodeId, ToNodeId, StringComparison.Ordinal)
            && edge.Condition == Condition;
    }
}
