namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Represents one bounded deterministic reconciliation validation result.</summary>
public sealed class GovernedLoopEffectReconciliationValidationResult
{
    private GovernedLoopEffectReconciliationValidationResult(IReadOnlyList<GovernedLoopEffectReconciliationValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets the bounded distinct value-free errors.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether the contract was accepted.</summary>
    public bool IsValid => Errors.Count == 0;

    internal static GovernedLoopEffectReconciliationValidationResult FromErrors(IEnumerable<GovernedLoopEffectReconciliationValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.Distinct().Take(GovernedLoopEffectReconciliationContractLimits.MaxValidationErrors).ToArray();
        return new GovernedLoopEffectReconciliationValidationResult(Array.AsReadOnly(snapshot));
    }
}
