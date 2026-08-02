namespace EmbodySense.Core.Common.Loops.Models.Custom.Retention;

/// <summary>
/// Identifies the capacity boundary preventing a receipt write or cleanup compaction.
/// </summary>
public enum CustomLoopReceiptQuotaExhaustionReason
{
    /// <summary>
    /// No capacity boundary is exhausted.
    /// </summary>
    None = 0,

    /// <summary>
    /// The artifact count ceiling is exhausted.
    /// </summary>
    ArtifactCountLimit,

    /// <summary>
    /// The aggregate artifact byte ceiling is exhausted.
    /// </summary>
    ArtifactByteLimit,

    /// <summary>
    /// Capacity reserved for completing pending artifact writes is exhausted.
    /// </summary>
    ReservedArtifactCountLimit,

    /// <summary>
    /// Byte capacity reserved for completing pending artifact writes is exhausted.
    /// </summary>
    ReservedArtifactByteLimit,

    /// <summary>
    /// The compact proof entry ceiling is exhausted.
    /// </summary>
    ProofCountLimit,

    /// <summary>
    /// The compact proof byte ceiling is exhausted.
    /// </summary>
    ProofByteLimit,

    /// <summary>
    /// The workspace-wide accounted byte ceiling is exhausted.
    /// </summary>
    WorkspaceByteLimit
}
