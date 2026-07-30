using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents a run artifact.
/// </summary>
/// <param name="Location">The location.</param>
/// <param name="Run">The run.</param>
/// <param name="Tombstone">The tombstone.</param>
/// <param name="PersistedHash">The persisted hash.</param>
/// <param name="PersistedUtf8Bytes">The persisted UTF-8 bytes.</param>
internal sealed record RunArtifact(
    RunArtifactLocation Location,
    CustomLoopRunRecord? Run,
    CustomLoopTraceTombstone? Tombstone,
    string PersistedHash,
    long PersistedUtf8Bytes);
