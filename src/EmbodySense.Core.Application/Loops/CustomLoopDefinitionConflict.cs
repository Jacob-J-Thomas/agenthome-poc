namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopDefinitionConflict(
    string LoopId,
    int ExpectedDefinitionVersion,
    int ActualDefinitionVersion,
    string CurrentContentHash,
    DateTimeOffset CurrentUpdatedAtUtc);
