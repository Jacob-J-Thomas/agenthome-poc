namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Declares control flow independently from data and context bindings.</summary>
/// <param name="Id">The stable edge identifier.</param>
/// <param name="FromNodeId">The source node identifier.</param>
/// <param name="ToNodeId">The destination node identifier.</param>
/// <param name="Condition">The explicit control condition.</param>
public sealed record GovernedLoopControlEdgeDefinition(string Id, string FromNodeId, string ToNodeId, GovernedLoopControlCondition Condition);
