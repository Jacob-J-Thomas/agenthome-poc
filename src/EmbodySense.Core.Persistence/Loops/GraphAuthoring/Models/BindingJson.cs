namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record BindingJson(
    string? Id,
    string? Kind,
    string? FromNodeId,
    string? FromPortId,
    string? ToNodeId,
    string? ToPortId);
