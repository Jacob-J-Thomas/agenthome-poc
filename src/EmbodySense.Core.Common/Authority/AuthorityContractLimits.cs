namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Defines the bounded schema-version-1 limits for authority contracts.
/// </summary>
public static class AuthorityContractLimits
{
    /// <summary>Gets the maximum profile identifier length.</summary>
    public const int MaxProfileIdCharacters = 128;

    /// <summary>Gets the maximum actor identifier length.</summary>
    public const int MaxActorIdCharacters = 128;

    /// <summary>Gets the maximum human-readable purpose length.</summary>
    public const int MaxPurposeCharacters = 512;

    /// <summary>Gets the maximum serialized authority-profile JSON length.</summary>
    public const int MaxProfileJsonCharacters = 32_768;

    /// <summary>Gets the maximum profiles that may participate in one intersection.</summary>
    public const int MaxProfilesPerIntersection = 16;

    /// <summary>Gets the maximum exact capability identities in a ceiling.</summary>
    public const int MaxCapabilitiesPerCeiling = 32;

    /// <summary>Gets the maximum data classes in a ceiling.</summary>
    public const int MaxDataClassesPerCeiling = 16;

    /// <summary>Gets the maximum boundary conditions on one profile.</summary>
    public const int MaxBoundaryConditionsPerProfile = 32;

    /// <summary>Gets the maximum canonical conditions retained in one boundary receipt.</summary>
    public const int MaxBoundaryConditionsPerReceipt = 32;

    /// <summary>Gets the maximum generic targets that one ceiling may permit.</summary>
    public const int MaxTargetCount = 10_000;
}
