namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopDefinitionTombstone(
    int SchemaVersion,
    string LoopId,
    int LastDefinitionVersion,
    string LastContentHash,
    string MutationOperationId,
    DateTimeOffset DeletedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}
