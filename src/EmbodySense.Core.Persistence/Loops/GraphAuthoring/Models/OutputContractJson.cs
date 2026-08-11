namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record OutputContractJson(
    string? Summary,
    OutputJson[]? Outputs);
