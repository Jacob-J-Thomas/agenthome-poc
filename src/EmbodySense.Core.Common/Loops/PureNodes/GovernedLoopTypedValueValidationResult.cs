using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Returns an immutable snapshot of typed-value validation errors.</summary>
public sealed class GovernedLoopTypedValueValidationResult
{
    /// <summary>Initializes a validation result from the discovered errors.</summary>
    /// <param name="errors">The bounded validation errors.</param>
    internal GovernedLoopTypedValueValidationResult(IEnumerable<GovernedLoopTypedValueError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.ToArray();
        if (snapshot.Length > CustomLoopLimits.MaxGraphValidationErrors || snapshot.Any(error => error is null))
        {
            throw new ArgumentException("Typed-value validation results must contain only bounded validated errors.", nameof(errors));
        }

        Errors = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the immutable discovered errors.</summary>
    /// <value>The deterministic error snapshot.</value>
    public IReadOnlyList<GovernedLoopTypedValueError> Errors { get; }

    /// <summary>Gets whether validation succeeded.</summary>
    /// <value><see langword="true"/> only when no errors were discovered.</value>
    public bool IsValid => Errors.Count == 0;
}
