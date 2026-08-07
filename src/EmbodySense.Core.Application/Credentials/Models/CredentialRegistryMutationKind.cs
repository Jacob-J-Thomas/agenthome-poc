namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Identifies one safe credential-registry lifecycle transition.</summary>
public enum CredentialRegistryMutationKind
{
    /// <summary>Registers one new value-free reference and exact binding.</summary>
    Register = 1,
    /// <summary>Updates only the provider health posture.</summary>
    SetHealth = 2,
    /// <summary>Irreversibly tombstones a registered reference.</summary>
    Tombstone = 3
}
