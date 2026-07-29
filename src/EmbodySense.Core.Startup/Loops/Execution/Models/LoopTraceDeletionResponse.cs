namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopTraceDeletionResponse(string Status, bool IsCommitted, bool IsOutcomeCommitted, string Detail, LoopTraceTombstoneSnapshot? Tombstone);
