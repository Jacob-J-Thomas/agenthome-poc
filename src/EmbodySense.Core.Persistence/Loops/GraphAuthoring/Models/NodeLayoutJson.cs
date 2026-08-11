namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record NodeLayoutJson(
    string? NodeId,
    string? DisplayName,
    string? Description,
    int? CanvasX,
    int? CanvasY);
