namespace EmbodySense.Core.Persistence.Credentials;

/// <summary>Defines schema-version-1 credential-registry retention and artifact bounds.</summary>
public static class CredentialRegistryLimits
{
    /// <summary>Gets the maximum registered references.</summary>
    public const int MaximumEntries = 512;
    /// <summary>Gets the maximum immutable tombstones.</summary>
    public const int MaximumTombstones = 512;
    /// <summary>Gets the maximum immutable operation receipts.</summary>
    public const int MaximumOperations = 4_096;
    /// <summary>Gets the maximum immutable credential-use evidence records.</summary>
    public const int MaximumEvidence = 4_096;
    /// <summary>Gets the maximum UTF-8 bytes for one registry artifact.</summary>
    public const int MaximumArtifactUtf8Bytes = 4 * 1024 * 1024;
}
