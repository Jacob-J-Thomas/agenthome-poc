namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Identifies deterministic observation points around opening a canonical custom-loop run artifact for reading.
/// </summary>
public enum CustomLoopRunReadBoundary
{
    /// <summary>The run artifact has been located and the reader is about to open it.</summary>
    BeforeCanonicalArtifactReadOpen,

    /// <summary>The canonical run artifact reader has opened its handle and has not read bytes yet.</summary>
    AfterCanonicalArtifactReadOpen,

    /// <summary>The canonical run artifact reader captured its first bounded byte snapshot and is about to verify it.</summary>
    AfterCanonicalArtifactReadFirstSnapshot,

    /// <summary>A canonical run artifact was unavailable during bounded reconciliation after it had been enumerated.</summary>
    AfterCanonicalArtifactReadMiss,
}
