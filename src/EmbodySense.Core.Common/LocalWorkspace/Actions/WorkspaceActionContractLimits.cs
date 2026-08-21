namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Defines the finite schema-1 limits for governed workspace file actions and their value-free evidence.</summary>
public static class WorkspaceActionContractLimits
{
    /// <summary>Gets the only supported workspace-action schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum normalized workspace-relative target length.</summary>
    public const int MaxTargetCharacters = 512;

    /// <summary>Gets the maximum target path depth.</summary>
    public const int MaxTargetSegments = 16;

    /// <summary>Gets the maximum normalized target segment length.</summary>
    public const int MaxTargetSegmentCharacters = 128;

    /// <summary>Gets the maximum number of ordered content segments.</summary>
    public const int MaxContentSegments = 32;

    /// <summary>Gets the maximum characters in one literal segment.</summary>
    public const int MaxLiteralCharacters = 16_384;

    /// <summary>Gets the maximum admitted literal UTF-8 bytes across all segments.</summary>
    public const int MaxLiteralUtf8Bytes = 24 * 1024;

    /// <summary>Gets the maximum number of value-free credential references.</summary>
    public const int MaxCredentialReferences = 8;

    /// <summary>Gets the maximum bytes read from an existing target.</summary>
    public const int MaxBeforeImageBytes = 1024 * 1024;

    /// <summary>Gets the maximum complete staged after-image bytes.</summary>
    public const int MaxAfterImageBytes = 1024 * 1024;

    /// <summary>Gets the maximum retained immutable evidence record bytes.</summary>
    public const int MaxEvidenceUtf8Bytes = 16 * 1024;

    /// <summary>Gets the maximum number of retained before, after, outcome, or tombstone records.</summary>
    public const int MaxEvidenceRecordsPerKind = 16_384;

    /// <summary>Gets the maximum number of authenticated staging entries.</summary>
    public const int MaxStagingEntries = 256;

    /// <summary>Gets the maximum number of retained delete tombstones and quarantine payloads.</summary>
    public const int MaxTombstones = 1_024;

    /// <summary>Gets the maximum total bytes retained in recoverable delete quarantine.</summary>
    public const long MaxQuarantineBytes = 256L * 1024 * 1024;

    /// <summary>Gets the maximum length of a scope or evidence identifier.</summary>
    public const int MaxIdentifierCharacters = 160;
}
