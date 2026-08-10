namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record NodeJson(
    string? Id,
    string? Kind,
    string? TypeId,
    int DescriptorVersion,
    string[]? AuthorityCeiling,
    IReadOnlyDictionary<string, string>? Parameters,
    PortJson[]? Ports);
