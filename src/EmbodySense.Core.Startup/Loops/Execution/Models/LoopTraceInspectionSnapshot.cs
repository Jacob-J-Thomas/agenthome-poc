namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects retained terminal trace metadata or the tombstone that replaced it.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="RunId">The run identifier.</param>
/// <param name="LoopId">The loop identifier.</param>
/// <param name="Status">The status.</param>
/// <param name="DefinitionVersion">The definition version.</param>
/// <param name="DefinitionHash">The definition hash.</param>
/// <param name="PersistedArtifactHash">The persisted artifact hash.</param>
/// <param name="PersistedArtifactUtf8Bytes">The persisted artifact utf8 bytes.</param>
/// <param name="OriginalTraceHash">The original trace hash.</param>
/// <param name="OriginalTraceUtf8Bytes">The original trace utf8 bytes.</param>
/// <param name="CreatedAtUtc">The created at utc.</param>
/// <param name="CompletedAtUtc">The completed at utc.</param>
/// <param name="IsDeleted">The is deleted.</param>
/// <param name="Tombstone">The tombstone.</param>
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
