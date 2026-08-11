namespace EmbodySense.Core.Application.Loops.Execution.Authority.Models;

/// <summary>Identifies the append-only persistence outcome for one exact authority decision identity.</summary>
public enum GovernedLoopEffectAuthorityEvidenceStoreStatus
{
    /// <summary>No supported outcome was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact decision was appended by this call.</summary>
    Appended = 1,
    /// <summary>The exact decision identity and content were already durably present.</summary>
    AlreadyPresent = 2,
    /// <summary>The decision identity already belongs to different immutable content.</summary>
    Conflict = 3,
    /// <summary>The durable evidence store was unavailable.</summary>
    Unavailable = 4,
    /// <summary>The evidence store could not prove one coherent persistence outcome.</summary>
    Ambiguous = 5,
}
