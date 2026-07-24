namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopTraceDeletionResponse(string Status, bool IsCommitted, bool IsOutcomeCommitted, string Detail, LoopTraceTombstoneSnapshot? Tombstone);
