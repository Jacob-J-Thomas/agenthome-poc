namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Represents a loop graph edge definition.
/// </summary>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="FromNodeId">The from node ID.</param>
/// <param name="ToNodeId">The to node ID.</param>
/// <param name="Condition">The condition.</param>
/// <param name="Description">The human-readable description.</param>
public sealed record LoopGraphEdgeDefinition(
    string Id,
    string FromNodeId,
    string ToNodeId,
    LoopGraphEdgeCondition Condition,
    string Description);
