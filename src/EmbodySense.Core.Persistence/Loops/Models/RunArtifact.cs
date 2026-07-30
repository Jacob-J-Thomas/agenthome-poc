using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record RunArtifact(
    RunArtifactLocation Location,
    CustomLoopRunRecord? Run,
    CustomLoopTraceTombstone? Tombstone,
    string PersistedHash,
    long PersistedUtf8Bytes);
