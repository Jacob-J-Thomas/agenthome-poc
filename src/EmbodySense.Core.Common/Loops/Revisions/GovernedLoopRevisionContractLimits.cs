namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Defines finite schema-1 bounds for governed-loop revision lifecycle contracts.</summary>
public static class GovernedLoopRevisionContractLimits
{
    /// <summary>The only supported POC contract schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The maximum number of ASCII characters in any lifecycle identifier.</summary>
    public const int MaxIdentifierCharacters = 120;

    /// <summary>The number of lowercase hexadecimal characters in a SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;

    /// <summary>The maximum supported optimistic lifecycle version.</summary>
    public const long MaxLifecycleVersion = 9_007_199_254_740_991;

    /// <summary>The maximum immutable revision artifacts retained for one graph.</summary>
    public const int MaxArtifactsPerGraph = 1_024;

    /// <summary>The maximum append-only operation-evidence records retained for one graph.</summary>
    public const int MaxOperationsPerGraph = 4_096;

    /// <summary>The maximum graph lifecycle aggregates retained by one bounded store snapshot.</summary>
    public const int MaxGraphsPerStore = 1_024;

    /// <summary>The maximum number of structured validation errors returned per call.</summary>
    public const int MaxValidationErrors = 32;

    /// <summary>The maximum number of characters in a safe schema-relative error path.</summary>
    public const int MaxErrorPathCharacters = 160;
}
