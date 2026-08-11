namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record GraphPayloadHashJson(
    int SchemaVersion,
    ExecutableGraphJson ExecutableGraph,
    GraphLayoutJson Layout);
