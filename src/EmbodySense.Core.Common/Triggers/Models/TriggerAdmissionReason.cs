namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Identifies one stable reason for a visible trigger admission outcome.
/// </summary>
public enum TriggerAdmissionReason
{
    /// <summary>No supported reason is present.</summary>
    Unknown = 0,
    /// <summary>All admission evidence matched at the supplied instant.</summary>
    EvidenceAccepted = 1,
    /// <summary>The canonical delivery exactly matched prior evidence.</summary>
    ExactReplay = 2,
    /// <summary>A delivery or deduplication identity was reused with different content.</summary>
    IdentityConflict = 3,
    /// <summary>The not-before instant has not been reached.</summary>
    NotBefore = 4,
    /// <summary>The deadline has been exceeded.</summary>
    DeadlineExceeded = 5,
    /// <summary>The expiry instant has been reached.</summary>
    Expired = 6,
    /// <summary>The envelope contract is invalid.</summary>
    InvalidEnvelope = 7,
    /// <summary>The current loop revision or content hash is stale.</summary>
    StaleLoop = 8,
    /// <summary>The current adapter capability pin is stale.</summary>
    StaleAdapter = 9,
    /// <summary>The current actor does not match the captured actor.</summary>
    ActorMismatch = 10,
    /// <summary>The current surface does not match the captured surface.</summary>
    SurfaceMismatch = 11,
    /// <summary>The current workspace does not match the captured workspace.</summary>
    WorkspaceMismatch = 12,
    /// <summary>The current role does not match the captured role.</summary>
    RoleMismatch = 13,
    /// <summary>The authority profile or boundary receipt does not match current evidence.</summary>
    AuthorityMismatch = 14,
    /// <summary>The authority evidence is too old.</summary>
    StaleAuthority = 15,
    /// <summary>The authority boundary is not direct.</summary>
    AuthorityBoundary = 16,
    /// <summary>The delivery was received too long ago.</summary>
    StaleDelivery = 17,
    /// <summary>The pinned adapter is unavailable.</summary>
    AdapterUnavailable = 18,
    /// <summary>Server-owned terminal admission history could not be inspected safely.</summary>
    HistoryUnavailable = 19
}
