namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record ControlEdgeJson(
    string? Id,
    string? FromNodeId,
    string? ToNodeId,
    string? Condition);
