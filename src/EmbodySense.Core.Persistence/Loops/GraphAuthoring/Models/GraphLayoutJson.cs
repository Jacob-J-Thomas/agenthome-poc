namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record GraphLayoutJson(
    string? DisplayName,
    string? Description,
    NodeLayoutJson[]? Nodes);
