using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput;

/// <summary>
/// Returns all deterministic validation errors for a human-input boundary contract.
/// </summary>
/// <param name="Errors">The validation errors.</param>
public sealed record HumanInputValidationResult(IReadOnlyList<HumanInputValidationError> Errors)
{
    /// <summary>Gets whether the inspected contract is valid.</summary>
    public bool IsValid => Errors.Count == 0;
}
