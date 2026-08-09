namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Describes safe provider posture without revealing credential material.</summary>
public enum CredentialProviderHealthStatus
{
    /// <summary>Provider material is reported available.</summary>
    Available = 0,
    /// <summary>Provider material is absent.</summary>
    Missing = 1,
    /// <summary>The provider cannot currently prove posture.</summary>
    Unavailable = 2,
    /// <summary>The provider reports corrupt material.</summary>
    Corrupt = 3,
    /// <summary>The registered provider configuration cannot be validated.</summary>
    Misconfigured = 4,
    /// <summary>The lifecycle outcome is ambiguous and requires explicit repair.</summary>
    NeedsRepair = 5,
    /// <summary>The reference is revoked regardless of provider material posture.</summary>
    Revoked = 6,
    /// <summary>The reference is administratively disabled regardless of provider material posture.</summary>
    Disabled = 7,
    /// <summary>The reference is expired regardless of provider material posture.</summary>
    Expired = 8
}
