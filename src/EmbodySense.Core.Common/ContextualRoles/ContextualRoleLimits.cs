namespace EmbodySense.Core.Common.ContextualRoles;

/// <summary>Defines bounded contract limits for contextual roles.</summary>
public static class ContextualRoleLimits
{
    /// <summary>Current contextual-role schema version.</summary>
    public const int SchemaVersion = 1;
    /// <summary>Maximum stable identifier characters.</summary>
    public const int MaxIdentifierCharacters = 120;
    /// <summary>Maximum display-name characters.</summary>
    public const int MaxDisplayNameCharacters = 120;
    /// <summary>Maximum purpose characters.</summary>
    public const int MaxPurposeCharacters = 2_000;
    /// <summary>Maximum instruction-source reference characters.</summary>
    public const int MaxInstructionSourceReferenceCharacters = 120;
    /// <summary>Maximum applicable workspace identifiers.</summary>
    public const int MaxWorkspaceScopes = 32;
    /// <summary>Maximum declared capability maxima.</summary>
    public const int MaxCapabilityMaximums = 64;
    /// <summary>Number of lowercase hexadecimal characters in a SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;
}
