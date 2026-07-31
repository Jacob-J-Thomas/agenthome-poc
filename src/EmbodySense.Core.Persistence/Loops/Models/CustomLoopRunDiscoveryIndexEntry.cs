using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents a custom loop run discovery index entry.
/// </summary>
/// <param name="Summary">The summary.</param>
/// <param name="ArtifactHash">The artifact hash.</param>
/// <param name="SummaryBindingHash">The summary binding hash.</param>
/// <param name="ArtifactUtf8Bytes">The artifact UTF-8 bytes.</param>
/// <param name="ArtifactLastWriteUtcTicks">The artifact last write UTC ticks.</param>
internal sealed record CustomLoopRunDiscoveryIndexEntry(
    CustomLoopRunSummary Summary,
    string ArtifactHash,
    string SummaryBindingHash,
    long ArtifactUtf8Bytes,
    long ArtifactLastWriteUtcTicks);
