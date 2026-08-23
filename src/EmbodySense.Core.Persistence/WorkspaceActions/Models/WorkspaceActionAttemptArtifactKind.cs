namespace EmbodySense.Core.Persistence.WorkspaceActions.Models;

/// <summary>Identifies the closed private artifact family authenticated by a workspace attempt marker.</summary>
internal enum WorkspaceActionAttemptArtifactKind
{
    /// <summary>A complete staged append or write after-image.</summary>
    Stage = 1,

    /// <summary>A reservation for one recoverable-delete quarantine payload.</summary>
    QuarantineReservation = 2,
}
