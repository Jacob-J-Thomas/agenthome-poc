using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

/// <summary>
/// Represents a required edge.
/// </summary>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="FromNodeId">The from node ID.</param>
/// <param name="ToNodeId">The to node ID.</param>
/// <param name="Condition">The condition.</param>
internal sealed record RequiredEdge(string Id, string FromNodeId, string ToNodeId, LoopGraphEdgeCondition Condition)
{
    /// <summary>
    /// Determines whether the edge matches the expected required edge.
    /// </summary>
    /// <param name="edge">The edge.</param>
    /// <returns><see langword="true"/> when matches; otherwise, <see langword="false"/>.</returns>
    public bool Matches(LoopGraphEdgeDefinition edge)
    {
        return string.Equals(edge.Id, Id, StringComparison.Ordinal)
            && string.Equals(edge.FromNodeId, FromNodeId, StringComparison.Ordinal)
            && string.Equals(edge.ToNodeId, ToNodeId, StringComparison.Ordinal)
            && edge.Condition == Condition;
    }
}
