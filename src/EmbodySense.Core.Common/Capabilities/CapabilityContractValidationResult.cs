namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Represents the structured result of validating a capability contract.
/// </summary>
/// <param name="Errors">The discovered contract errors.</param>
public sealed record CapabilityContractValidationResult(IReadOnlyList<CapabilityContractError> Errors)
{
    /// <summary>
    /// Gets a value indicating whether the contract passed validation.
    /// </summary>
    /// <value><see langword="true"/> when no errors were discovered; otherwise, <see langword="false"/>.</value>
    public bool IsValid => Errors.Count == 0;
}
