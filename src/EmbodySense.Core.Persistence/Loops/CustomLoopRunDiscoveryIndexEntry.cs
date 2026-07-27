using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed record CustomLoopRunDiscoveryIndexEntry(CustomLoopRunSummary Summary, long ArtifactUtf8Bytes, long ArtifactLastWriteUtcTicks);
