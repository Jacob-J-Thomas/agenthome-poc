namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop definition conflict.
/// </summary>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="ExpectedDefinitionVersion">The expected definition version.</param>
/// <param name="ActualDefinitionVersion">The actual definition version.</param>
/// <param name="CurrentContentHash">The current content hash.</param>
/// <param name="CurrentUpdatedAtUtc">The current updated at UTC.</param>
public sealed record CustomLoopDefinitionConflict(
    string LoopId,
    int ExpectedDefinitionVersion,
    int ActualDefinitionVersion,
    string CurrentContentHash,
    DateTimeOffset CurrentUpdatedAtUtc);
