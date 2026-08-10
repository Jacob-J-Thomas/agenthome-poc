namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Identifies the closed lifecycle posture of one immutable grant revision.</summary>
public enum AuthorityGrantLifecycleStatus
{
    /// <summary>The status is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The grant may be evaluated after all exact dependencies and boundaries are revalidated.</summary>
    Active = 1,
    /// <summary>The grant is explicitly suspended and has no effective authority.</summary>
    Suspended = 2,
    /// <summary>The grant is terminally revoked.</summary>
    Revoked = 3,
    /// <summary>The grant is terminally expired.</summary>
    Expired = 4,
}
