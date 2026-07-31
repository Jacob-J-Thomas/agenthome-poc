namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Reports the durable and audited outcome of an idempotent terminal-trace deletion.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="IsCommitted">The is committed.</param>
/// <param name="IsOutcomeCommitted">The is outcome committed.</param>
/// <param name="Detail">The detail.</param>
/// <param name="Tombstone">The tombstone.</param>
public sealed record LoopTraceDeletionResponse(string Status, bool IsCommitted, bool IsOutcomeCommitted, string Detail, LoopTraceTombstoneSnapshot? Tombstone);
