namespace EmbodySense.Core.Common.Loops.Execution.Sleep;

/// <summary>Defines finite schema-1 bounds for sleep, wake, and local coordinator evidence.</summary>
public static class GovernedLoopSleepContractLimits
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopExecutionLimits.CurrentSchemaVersion;

    /// <summary>Gets the maximum stable identifier length.</summary>
    public const int MaxIdentifierCharacters = GovernedLoopExecutionLimits.MaxIdentifierCharacters;

    /// <summary>Gets the maximum bounded evidence-reference length.</summary>
    public const int MaxEvidenceReferenceCharacters = GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters;

    /// <summary>Gets the maximum optimistic evidence or ownership version.</summary>
    public const long MaxVersion = GovernedLoopExecutionLimits.MaxVersion;

    /// <summary>Gets the maximum wait-attempt number.</summary>
    public const int MaxWaitAttempt = GovernedLoopExecutionLimits.MaxNodeAttempt;

    /// <summary>Gets the maximum retained validation errors.</summary>
    public const int MaxValidationErrors = GovernedLoopExecutionLimits.MaxValidationErrors;

    /// <summary>Gets the maximum safe validation-path length.</summary>
    public const int MaxErrorPathCharacters = GovernedLoopExecutionLimits.MaxErrorPathCharacters;

    /// <summary>Gets the lowercase hexadecimal character count of one SHA-256 digest.</summary>
    public const int Sha256HexCharacters = GovernedLoopExecutionLimits.Sha256HexCharacters;
}
