using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

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
    public bool IsDeleted => Kind == CustomLoopTraceArtifactKind.Tombstone;
}
