using EmbodySense.Core.Application.Loops.TraceRetention;

namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents an artifact scan result.
/// </summary>
/// <param name="Quota">The quota.</param>
internal sealed record ArtifactScanResult(CustomLoopTraceQuota Quota);
