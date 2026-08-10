namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record OutputJson(
    string? Id,
    string? ValueSchemaId,
    string? SourceNodeId,
    string? SourcePortId,
    bool Required);
