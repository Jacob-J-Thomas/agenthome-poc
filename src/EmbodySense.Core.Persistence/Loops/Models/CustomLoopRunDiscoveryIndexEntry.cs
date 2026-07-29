using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record CustomLoopRunDiscoveryIndexEntry(
    CustomLoopRunSummary Summary,
    string ArtifactHash,
    string SummaryBindingHash,
    long ArtifactUtf8Bytes,
    long ArtifactLastWriteUtcTicks);
