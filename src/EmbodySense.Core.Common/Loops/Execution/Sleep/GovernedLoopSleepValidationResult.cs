namespace EmbodySense.Core.Common.Loops.Execution.Sleep;

/// <summary>Represents one bounded deterministic sleep/wake/coordinator validation result.</summary>
public sealed class GovernedLoopSleepValidationResult
{
    private GovernedLoopSleepValidationResult(IReadOnlyList<GovernedLoopSleepValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets the bounded distinct value-free errors.</summary>
    public IReadOnlyList<GovernedLoopSleepValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether the contract was accepted.</summary>
    public bool IsValid => Errors.Count == 0;

    internal static GovernedLoopSleepValidationResult FromErrors(IEnumerable<GovernedLoopSleepValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.Distinct().Take(GovernedLoopSleepContractLimits.MaxValidationErrors).ToArray();
        return new GovernedLoopSleepValidationResult(Array.AsReadOnly(snapshot));
    }
}
