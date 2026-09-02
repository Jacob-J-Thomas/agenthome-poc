namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Defines the finite schema-1 bounds for value-free effect reconciliation.</summary>
public static class GovernedLoopEffectReconciliationContractLimits
{
    /// <summary>Gets the only supported experimental reconciliation schema.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum stable identifier length.</summary>
    public const int MaxIdentifierCharacters = 120;

    /// <summary>Gets the lowercase hexadecimal character count of a SHA-256 digest.</summary>
    public const int Sha256HexCharacters = 64;

    /// <summary>Gets the maximum number of retained observations.</summary>
    public const int MaxObservations = 32;

    /// <summary>Gets the maximum exact observation references in one assessment.</summary>
    public const int MaxObservationReferences = 32;

    /// <summary>Gets the maximum number of retained assessments.</summary>
    public const int MaxAssessments = 32;

    /// <summary>Gets the maximum registered evidence sources.</summary>
    public const int MaxEvidenceSources = 32;

    /// <summary>Gets the maximum case receipt hashes.</summary>
    public const int MaxCaseReceipts = 64;

    /// <summary>Gets the maximum predecessor case hashes retained as history.</summary>
    public const int MaxCaseHistory = 64;

    /// <summary>Gets the maximum operator-safe summary length.</summary>
    public const int MaxSummaryCharacters = 1_024;

    /// <summary>Gets the maximum operator-safe detail length.</summary>
    public const int MaxDetailCharacters = 1_024;

    /// <summary>Gets the maximum canonical record size.</summary>
    public const int MaxRecordUtf8Bytes = 65_536;

    /// <summary>Gets the maximum accepted JSON nesting depth.</summary>
    public const int MaxJsonDepth = 32;

    /// <summary>Gets the maximum retained validation errors.</summary>
    public const int MaxValidationErrors = 128;

    /// <summary>Gets the maximum safe validation path length.</summary>
    public const int MaxErrorPathCharacters = 256;
}
