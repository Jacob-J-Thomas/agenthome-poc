namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Represents the structured result of validating a capability contract.
/// </summary>
public sealed record CapabilityContractValidationResult
{
    /// <summary>
    /// Initializes a validation result from an immutable snapshot of the discovered errors.
    /// </summary>
    /// <param name="errors">The discovered contract errors.</param>
    public CapabilityContractValidationResult(IReadOnlyList<CapabilityContractError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    /// <summary>
    /// Gets the immutable snapshot of discovered contract errors.
    /// </summary>
    /// <value>The discovered contract errors.</value>
    public IReadOnlyList<CapabilityContractError> Errors { get; }

    /// <summary>
    /// Gets a value indicating whether the contract passed validation.
    /// </summary>
    /// <value><see langword="true"/> when no errors were discovered; otherwise, <see langword="false"/>.</value>
    public bool IsValid => Errors.Count == 0;
}
