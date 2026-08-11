namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record PortJson(
    string? Id,
    string? Direction,
    string? BindingKind,
    string? ValueSchemaId,
    bool Required);
