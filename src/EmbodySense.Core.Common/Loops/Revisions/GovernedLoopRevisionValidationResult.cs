using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Represents one bounded deterministic governed-loop revision validation result.</summary>
public sealed record GovernedLoopRevisionValidationResult
{
    private GovernedLoopRevisionValidationResult(IReadOnlyList<GovernedLoopRevisionValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets the bounded, value-free validation errors.</summary>
    public IReadOnlyList<GovernedLoopRevisionValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether the contract is valid.</summary>
    public bool IsValid => Errors.Count == 0;

    internal static GovernedLoopRevisionValidationResult FromErrors(IEnumerable<GovernedLoopRevisionValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.Distinct().Take(GovernedLoopRevisionContractLimits.MaxValidationErrors).ToArray();
        return new GovernedLoopRevisionValidationResult(Array.AsReadOnly(snapshot));
    }
}
