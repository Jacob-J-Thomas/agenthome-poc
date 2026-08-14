using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Returns an immutable snapshot of pure-node outcome validation errors.</summary>
public sealed class GovernedLoopPureNodeOutcomeValidationResult
{
    internal GovernedLoopPureNodeOutcomeValidationResult(IEnumerable<GovernedLoopPureNodeOutcomeError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.ToArray();
        if (snapshot.Length > CustomLoopLimits.MaxGraphValidationErrors || snapshot.Any(error => error is null))
        {
            throw new ArgumentException("Pure-node outcome validation results must contain only bounded validated errors.", nameof(errors));
        }

        Errors = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the immutable discovered errors.</summary>
    /// <value>The deterministic error snapshot.</value>
    public IReadOnlyList<GovernedLoopPureNodeOutcomeError> Errors { get; }

    /// <summary>Gets whether validation succeeded.</summary>
    /// <value><see langword="true"/> only when no errors were discovered.</value>
    public bool IsValid => Errors.Count == 0;
}
