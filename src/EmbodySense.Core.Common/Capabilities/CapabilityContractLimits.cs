namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Defines the schema-version-1 bounds for capability contracts.
/// </summary>
public static class CapabilityContractLimits
{
    /// <summary>Gets the maximum capability identifier length.</summary>
    public const int MaxCapabilityIdCharacters = 192;

    /// <summary>Gets the maximum capability kind token length.</summary>
    public const int MaxKindCharacters = 48;

    /// <summary>Gets the maximum provider identifier length.</summary>
    public const int MaxProviderIdCharacters = 253;

    /// <summary>Gets the maximum implementation identifier length.</summary>
    public const int MaxImplementationIdCharacters = 96;

    /// <summary>Gets the maximum semantic-version string length.</summary>
    public const int MaxVersionCharacters = 128;

    /// <summary>Gets the maximum compatible-version-range string length.</summary>
    public const int MaxVersionRangeCharacters = 264;

    /// <summary>Gets the maximum human-readable purpose length.</summary>
    public const int MaxPurposeCharacters = 1_024;

    /// <summary>Gets the maximum canonical JSON schema length.</summary>
    public const int MaxSchemaCharacters = 32_768;

    /// <summary>Gets the maximum JSON depth accepted for a schema.</summary>
    public const int MaxSchemaDepth = 16;

    /// <summary>Gets the maximum aggregate property and array-item count accepted for a schema.</summary>
    public const int MaxSchemaElements = 1_024;

    /// <summary>Gets the maximum provenance source URI length.</summary>
    public const int MaxSourceUriCharacters = 2_048;

    /// <summary>Gets the maximum source revision length.</summary>
    public const int MaxSourceRevisionCharacters = 128;

    /// <summary>Gets the maximum number of supported platforms.</summary>
    public const int MaxPlatforms = 32;

    /// <summary>Gets the maximum number of declared data classes.</summary>
    public const int MaxDataClasses = 16;

    /// <summary>Gets the maximum number of declared egress destinations.</summary>
    public const int MaxEgressDestinations = 32;

    /// <summary>Gets the maximum number of declared secret requirements.</summary>
    public const int MaxSecretRequirements = 32;

    /// <summary>Gets the maximum descriptor JSON length.</summary>
    public const int MaxDescriptorJsonCharacters = 131_072;

    /// <summary>Gets the maximum declared execution duration in milliseconds.</summary>
    public const int MaxExecutionMilliseconds = 86_400_000;

    /// <summary>Gets the maximum declared memory usage in bytes.</summary>
    public const long MaxMemoryBytes = 1_099_511_627_776;

    /// <summary>Gets the maximum declared output size in bytes.</summary>
    public const int MaxOutputBytes = 16_777_216;

    /// <summary>Gets the maximum declared concurrency.</summary>
    public const int MaxConcurrency = 1_024;

    /// <summary>Gets the maximum required or optional dependency declarations in one manifest.</summary>
    public const int MaxDependencyManifestDependencies = 64;

    /// <summary>Gets the maximum dependency edges resolved while producing one exact admission snapshot.</summary>
    public const int MaxResolvedDependencyGraphDependencies = 256;

    /// <summary>Gets the maximum exact capability pins preserved in one admission snapshot.</summary>
    public const int MaxCapabilityAdmissionPins = MaxResolvedDependencyGraphDependencies;

    /// <summary>Gets the maximum resolver observations preserved in one successful admission snapshot.</summary>
    public const int MaxCapabilityAdmissionEvidenceEntries = MaxResolvedDependencyGraphDependencies;

    /// <summary>Gets the maximum bounded diagnostic detail retained for one admission-resolution observation.</summary>
    public const int MaxCapabilityAdmissionEvidenceDetailCharacters = 1_024;

    /// <summary>Gets the maximum canonical dependency-manifest JSON length.</summary>
    public const int MaxDependencyManifestJsonCharacters = 32_768;

    /// <summary>Gets the maximum opaque signature evidence length.</summary>
    public const int MaxArtifactSignatureCharacters = 4_096;
}
