namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Defines stable, value-free credential failure categories.</summary>
public enum CredentialFailureCode
{
    /// <summary>The request shape or relationship is invalid.</summary>
    InvalidRequest = 0,
    /// <summary>The requested reference or provider value was not found.</summary>
    NotFound = 1,
    /// <summary>The broker or provider is unavailable.</summary>
    Unavailable = 2,
    /// <summary>Current trusted authority did not permit use.</summary>
    Unauthorized = 3,
    /// <summary>The requested scope exceeds or conflicts with authority.</summary>
    ScopeMismatch = 4,
    /// <summary>The reference or proof is expired.</summary>
    Expired = 5,
    /// <summary>The reference or authority was revoked.</summary>
    Revoked = 6,
    /// <summary>An operation identity or optimistic state conflicts.</summary>
    Conflict = 7,
    /// <summary>A schema or provider bound was exceeded.</summary>
    LimitExceeded = 8,
    /// <summary>The trusted callback failed before a proved terminal result.</summary>
    CallbackFailed = 9,
    /// <summary>The side-effect outcome is uncertain.</summary>
    OutcomeUncertain = 10
}
