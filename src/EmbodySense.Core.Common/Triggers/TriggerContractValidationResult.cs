using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Represents the bounded result of validating a trigger-delivery contract.
/// </summary>
public sealed record TriggerContractValidationResult
{
    /// <summary>
    /// Initializes an immutable validation result.
    /// </summary>
    /// <param name="errors">The stable errors.</param>
    public TriggerContractValidationResult(IEnumerable<TriggerContractError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = Array.AsReadOnly(errors.Distinct().ToArray());
    }

    /// <summary>Gets the stable validation errors.</summary>
    public IReadOnlyList<TriggerContractError> Errors { get; }

    /// <summary>Gets a value indicating whether validation succeeded.</summary>
    public bool IsValid => Errors.Count == 0;
}
