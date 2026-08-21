namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Defines the closed posture of fresh server-owned credential authority revalidation.</summary>
public enum CredentialLeaseCurrentVerificationStatus
{
    /// <summary>Every applicable exact authority source currently agrees.</summary>
    Authorized = 1,
    /// <summary>One current authority source conclusively denied the request.</summary>
    Denied = 2,
    /// <summary>One required authority source could not prove current posture.</summary>
    Unavailable = 3,
}
