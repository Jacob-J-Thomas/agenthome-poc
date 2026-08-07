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
    Corrupt = 3
}
