using EmbodySense.Core.Common.Loops.Execution.Wait.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Wait;

/// <summary>Represents one bounded deterministic Wait contract validation result.</summary>
public sealed class GovernedLoopWaitValidationResult
{
    private GovernedLoopWaitValidationResult(IReadOnlyList<GovernedLoopWaitValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets the bounded distinct value-free errors.</summary>
    public IReadOnlyList<GovernedLoopWaitValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether the contract was accepted.</summary>
    public bool IsValid => Errors.Count == 0;

    internal static GovernedLoopWaitValidationResult FromErrors(IEnumerable<GovernedLoopWaitValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.Distinct().Take(GovernedLoopWaitContractLimits.MaxValidationErrors).ToArray();
        return new GovernedLoopWaitValidationResult(Array.AsReadOnly(snapshot));
    }
}
