using EmbodySense.Core.Common.Loops.Execution.Sleep;

namespace EmbodySense.Core.Common.Loops.Execution.Wait;

/// <summary>Defines finite schema-1 bounds for Wait admission, parking, and continuation evidence.</summary>
public static class GovernedLoopWaitContractLimits
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopExecutionLimits.CurrentSchemaVersion;

    /// <summary>Gets the maximum stable identifier length.</summary>
    public const int MaxIdentifierCharacters = GovernedLoopSleepContractLimits.MaxIdentifierCharacters;

    /// <summary>Gets the maximum governed event-reference length.</summary>
    public const int MaxEventReferenceCharacters = GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters;

    /// <summary>Gets the maximum optimistic frontier version.</summary>
    public const long MaxVersion = GovernedLoopSleepContractLimits.MaxVersion;

    /// <summary>Gets the maximum retained validation-error count.</summary>
    public const int MaxValidationErrors = GovernedLoopSleepContractLimits.MaxValidationErrors;

    /// <summary>Gets the maximum safe validation-path length.</summary>
    public const int MaxErrorPathCharacters = GovernedLoopSleepContractLimits.MaxErrorPathCharacters;

    /// <summary>Gets the lowercase hexadecimal character count of one SHA-256 digest.</summary>
    public const int Sha256HexCharacters = GovernedLoopSleepContractLimits.Sha256HexCharacters;
}
