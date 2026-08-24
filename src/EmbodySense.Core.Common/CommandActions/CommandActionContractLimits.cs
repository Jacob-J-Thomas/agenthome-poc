namespace EmbodySense.Core.Common.CommandActions;

/// <summary>Defines hard schema-1 bounds for structured command actions.</summary>
public static class CommandActionContractLimits
{
    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>Gets the maximum template identifier length.</summary>
    public const int MaxTemplateIdCharacters = 128;
    /// <summary>Gets the maximum slot count.</summary>
    public const int MaxSlots = 32;
    /// <summary>Gets the maximum argument-token count.</summary>
    public const int MaxArguments = 64;
    /// <summary>Gets the maximum fixed environment-entry count.</summary>
    public const int MaxEnvironmentEntries = 32;
    /// <summary>Gets the maximum environment-variable name length.</summary>
    public const int MaxEnvironmentNameCharacters = 64;
    /// <summary>Gets the maximum UTF-8 bytes in one argument, slot, stdin value, or environment value.</summary>
    public const int MaxValueUtf8Bytes = 16 * 1024;
    /// <summary>Gets the maximum combined UTF-8 bytes in materialized arguments, stdin, and environment values.</summary>
    public const int MaxMaterializedInputUtf8Bytes = 256 * 1024;
    /// <summary>Gets the maximum process execution duration.</summary>
    public const int MaxExecutionMilliseconds = 86_400_000;
    /// <summary>Gets the maximum process-tree termination wait.</summary>
    public const int MaxTerminationMilliseconds = 30_000;
    /// <summary>Gets the maximum retained combined standard-output and standard-error bytes.</summary>
    public const int MaxOutputBytes = 16 * 1024 * 1024;
    /// <summary>Gets the maximum retained redacted character count per standard stream.</summary>
    public const int MaxRetainedOutputCharacters = 4_096;
    /// <summary>Gets the maximum declared memory ceiling.</summary>
    public const long MaxMemoryBytes = 1_099_511_627_776;
    /// <summary>Gets the maximum concurrency ceiling.</summary>
    public const int MaxConcurrency = 1_024;
    /// <summary>Gets the maximum immutable evidence-record bytes.</summary>
    public const int MaxEvidenceUtf8Bytes = 256 * 1024;
    /// <summary>Gets the maximum number of retained records per evidence family.</summary>
    public const int MaxEvidenceRecordsPerKind = 8_192;
}
