namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopTraceDeletionResponse(string Status, bool IsCommitted, string Detail, LoopTraceTombstoneSnapshot? Tombstone);
