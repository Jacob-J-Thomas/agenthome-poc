namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Reports the authoritative definition evidence returned after an optimistic-concurrency conflict.
/// </summary>
/// <param name="LoopId">The conflicting custom loop.</param>
/// <param name="ExpectedDefinitionVersion">The version supplied by the rejected mutation.</param>
/// <param name="ActualDefinitionVersion">The current durable version.</param>
/// <param name="CurrentContentHash">The current durable canonical content hash.</param>
/// <param name="CurrentUpdatedAtUtc">The current durable update timestamp.</param>
public sealed record LoopDefinitionConflict(
    string LoopId,
    int ExpectedDefinitionVersion,
    int ActualDefinitionVersion,
    string CurrentContentHash,
    DateTimeOffset CurrentUpdatedAtUtc);
