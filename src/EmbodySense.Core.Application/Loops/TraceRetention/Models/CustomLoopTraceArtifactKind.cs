namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Identifies the supported custom loop trace artifact kind values.
/// </summary>
public enum CustomLoopTraceArtifactKind
{
    /// <summary>
    /// Identifies the live trace custom loop trace artifact kind.
    /// </summary>
    LiveTrace = 1,
    /// <summary>
    /// Identifies the tombstone custom loop trace artifact kind.
    /// </summary>
    Tombstone = 2
}
