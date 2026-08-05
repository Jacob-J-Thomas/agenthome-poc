namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Describes public lifecycle posture without granting authority or proving provider health.</summary>
public enum CredentialLifecycleStatus
{
    /// <summary>The reference may be considered for use after all authority checks.</summary>
    Active = 0,
    /// <summary>The reference was administratively disabled.</summary>
    Disabled = 1,
    /// <summary>The reference reached its declared expiry.</summary>
    Expired = 2,
    /// <summary>The reference was explicitly revoked.</summary>
    Revoked = 3
}
