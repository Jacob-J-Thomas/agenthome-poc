using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

/// <summary>
/// Represents the immutable result of validating an authority contract.
/// </summary>
public sealed record AuthorityContractValidationResult
{
    /// <summary>
    /// Initializes a validation result from a defensive snapshot of its structured errors.
    /// </summary>
    /// <param name="errors">The discovered structured errors.</param>
    public AuthorityContractValidationResult(IReadOnlyList<AuthorityContractError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    /// <summary>Gets the immutable structured errors.</summary>
    public IReadOnlyList<AuthorityContractError> Errors { get; }

    /// <summary>Gets a value indicating whether validation found no errors.</summary>
    public bool IsValid => Errors.Count == 0;
}
