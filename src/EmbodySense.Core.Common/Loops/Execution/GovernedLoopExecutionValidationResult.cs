namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Represents one bounded deterministic governed-loop execution validation result.</summary>
/// <remarks>Construction deduplicates and caps errors before exposing an immutable view.</remarks>
public sealed record GovernedLoopExecutionValidationResult
{
    private GovernedLoopExecutionValidationResult(IReadOnlyList<GovernedLoopExecutionValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets the bounded distinct value-free errors.</summary>
    public IReadOnlyList<GovernedLoopExecutionValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether no rejection was produced.</summary>
    public bool IsValid => Errors.Count == 0;

    internal static GovernedLoopExecutionValidationResult FromErrors(IEnumerable<GovernedLoopExecutionValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.Distinct().Take(GovernedLoopExecutionLimits.MaxValidationErrors).ToArray();
        return new GovernedLoopExecutionValidationResult(Array.AsReadOnly(snapshot));
    }
}
