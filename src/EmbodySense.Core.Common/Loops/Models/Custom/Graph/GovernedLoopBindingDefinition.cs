namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Declares one explicit typed data or context binding between node ports.</summary>
/// <param name="Id">The stable binding identifier.</param>
/// <param name="Kind">The data or context channel.</param>
/// <param name="FromNodeId">The source node identifier.</param>
/// <param name="FromPortId">The source output-port identifier.</param>
/// <param name="ToNodeId">The destination node identifier.</param>
/// <param name="ToPortId">The destination input-port identifier.</param>
public sealed record GovernedLoopBindingDefinition(string Id, GovernedLoopBindingKind Kind, string FromNodeId, string FromPortId, string ToNodeId, string ToPortId);
