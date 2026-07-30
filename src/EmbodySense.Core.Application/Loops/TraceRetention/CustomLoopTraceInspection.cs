using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

/// <summary>
/// Represents a custom loop trace inspection.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="TerminalStatus">The terminal status.</param>
/// <param name="DefinitionVersion">The monotonically increasing definition version.</param>
/// <param name="DefinitionHash">The definition hash.</param>
/// <param name="PersistedArtifactHash">The persisted artifact hash.</param>
/// <param name="PersistedArtifactUtf8Bytes">The persisted artifact UTF-8 bytes.</param>
/// <param name="OriginalTraceHash">The original trace hash.</param>
/// <param name="OriginalTraceUtf8Bytes">The original trace UTF-8 bytes.</param>
/// <param name="CreatedAtUtc">The UTC creation time.</param>
/// <param name="CompletedAtUtc">The UTC terminal time, or <see langword="null"/> while nonterminal.</param>
/// <param name="Tombstone">The tombstone.</param>
public sealed record CustomLoopTraceInspection(
    CustomLoopTraceArtifactKind Kind,
    string RunId,
    string LoopId,
    CustomLoopRunStatus TerminalStatus,
    int DefinitionVersion,
    string DefinitionHash,
    string PersistedArtifactHash,
    long PersistedArtifactUtf8Bytes,
    string OriginalTraceHash,
    long OriginalTraceUtf8Bytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    CustomLoopTraceTombstone? Tombstone)
{
    /// <summary>
    /// Gets a value indicating whether the value is deleted.
    /// </summary>
    /// <value><see langword="true"/> when the value is deleted; otherwise, <see langword="false"/>.</value>
    public bool IsDeleted => Kind == CustomLoopTraceArtifactKind.Tombstone;
}
