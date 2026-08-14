namespace EmbodySense.Core.Common.Loops.Execution.Authority;

/// <summary>Represents one bounded deterministic effect-authority validation result.</summary>
public sealed class GovernedLoopEffectAuthorityValidationResult
{
    private GovernedLoopEffectAuthorityValidationResult(IReadOnlyList<GovernedLoopEffectAuthorityValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets the bounded distinct value-free errors.</summary>
    public IReadOnlyList<GovernedLoopEffectAuthorityValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether the contract was accepted.</summary>
    public bool IsValid => Errors.Count == 0;

    internal static GovernedLoopEffectAuthorityValidationResult FromErrors(IEnumerable<GovernedLoopEffectAuthorityValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.Distinct().Take(GovernedLoopEffectAuthorityContractLimits.MaxValidationErrors).ToArray();
        return new GovernedLoopEffectAuthorityValidationResult(Array.AsReadOnly(snapshot));
    }
}
