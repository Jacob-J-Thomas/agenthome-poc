namespace EmbodySense.Core.Persistence.WorkspaceActions.Models;

/// <summary>Names bounded observer points after native publication and before complete outcome evidence.</summary>
public enum WorkspaceActionDurabilityPoint
{
    /// <summary>No durability point was selected.</summary>
    Unknown = 0,

    /// <summary>The exact append/write after-image is durable at its target, but after evidence is not yet retained.</summary>
    AfterInstallBeforeEvidence = 1,

    /// <summary>The exact delete payload and tombstone are durable in quarantine, but after evidence is not yet retained.</summary>
    AfterDeleteTombstoneBeforeEvidence = 2,

    /// <summary>The exact after-state evidence is durable, but its distinct outcome record is not yet retained.</summary>
    AfterEvidenceBeforeOutcome = 3,
}
