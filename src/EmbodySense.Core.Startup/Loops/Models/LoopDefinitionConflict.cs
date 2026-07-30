namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopDefinitionConflict(
    string LoopId,
    int ExpectedDefinitionVersion,
    int ActualDefinitionVersion,
    string CurrentContentHash,
    DateTimeOffset CurrentUpdatedAtUtc);
