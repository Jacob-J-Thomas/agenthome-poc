namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Defines the closed result of server-owned credential provider resolution.</summary>
public enum CredentialValueProviderResolutionStatus
{
    /// <summary>The exact configured local provider was resolved.</summary>
    Resolved = 1,
    /// <summary>No configured provider matches the exact registry binding.</summary>
    NotConfigured = 2,
    /// <summary>Provider composition cannot currently prove its posture.</summary>
    Unavailable = 3,
}
