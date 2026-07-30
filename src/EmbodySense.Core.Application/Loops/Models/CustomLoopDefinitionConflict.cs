namespace EmbodySense.Core.Application.Loops.Models;

public sealed record CustomLoopDefinitionConflict(
    string LoopId,
    int ExpectedDefinitionVersion,
    int ActualDefinitionVersion,
    string CurrentContentHash,
    DateTimeOffset CurrentUpdatedAtUtc);
