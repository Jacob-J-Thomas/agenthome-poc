namespace EmbodySense.Core.Common.Credentials;

/// <summary>Defines the schema-version-1 bounds for credential contracts.</summary>
public static class CredentialContractLimits
{
    /// <summary>Gets the maximum identifier length.</summary>
    public const int MaxIdCharacters = 128;
    /// <summary>Gets the maximum token length.</summary>
    public const int MaxTokenCharacters = 96;
    /// <summary>Gets the maximum purpose length.</summary>
    public const int MaxPurposeCharacters = 1_024;
    /// <summary>Gets the maximum public metadata entry count.</summary>
    public const int MaxMetadataEntries = 8;
    /// <summary>Gets the maximum public metadata value length.</summary>
    public const int MaxMetadataValueCharacters = 256;
    /// <summary>Gets the maximum canonical contract JSON length.</summary>
    public const int MaxCanonicalJsonCharacters = 65_536;
    /// <summary>Gets the maximum safe failure reason length.</summary>
    public const int MaxFailureMessageCharacters = 512;
    /// <summary>Gets the maximum structured error path length.</summary>
    public const int MaxErrorPathCharacters = 256;
    /// <summary>Gets the maximum validation error count.</summary>
    public const int MaxValidationErrors = 64;
    /// <summary>Gets the maximum provider credential byte count.</summary>
    public const int MaxCredentialBytes = 65_536;
    /// <summary>Gets the maximum authority proof lifetime.</summary>
    public static readonly TimeSpan MaxProofLifetime = TimeSpan.FromHours(24);
}
