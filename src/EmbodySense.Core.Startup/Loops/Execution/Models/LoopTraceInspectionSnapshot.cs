namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopTraceInspectionSnapshot(
    string Kind,
    string RunId,
    string LoopId,
    string Status,
    int DefinitionVersion,
    string DefinitionHash,
    string PersistedArtifactHash,
    long PersistedArtifactUtf8Bytes,
    string OriginalTraceHash,
    long OriginalTraceUtf8Bytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool IsDeleted,
    LoopTraceTombstoneSnapshot? Tombstone);
