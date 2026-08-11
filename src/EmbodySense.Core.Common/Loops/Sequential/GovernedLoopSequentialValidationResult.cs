namespace EmbodySense.Core.Common.Loops.Sequential;

/// <summary>Represents one bounded deterministic sequential hand-off validation result.</summary>
public sealed class GovernedLoopSequentialValidationResult
{
    private GovernedLoopSequentialValidationResult(IReadOnlyList<GovernedLoopSequentialValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets the bounded distinct value-free errors.</summary>
    public IReadOnlyList<GovernedLoopSequentialValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether the contract was accepted.</summary>
    public bool IsValid => Errors.Count == 0;

    internal static GovernedLoopSequentialValidationResult FromErrors(IEnumerable<GovernedLoopSequentialValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.Distinct().Take(GovernedLoopSequentialContractLimits.MaxValidationErrors).ToArray();
        return new GovernedLoopSequentialValidationResult(Array.AsReadOnly(snapshot));
    }
}
