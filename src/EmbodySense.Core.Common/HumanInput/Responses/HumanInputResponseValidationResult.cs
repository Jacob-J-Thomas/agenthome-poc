namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseValidationResult
{
    /// <summary>Creates a validation result by snapshotting the supplied bounded errors.</summary>
    /// <param name="errors">The deterministic value-free errors.</param>
    public HumanInputResponseValidationResult(IEnumerable<HumanInputResponseValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = Array.AsReadOnly(errors.Take(HumanInputResponseContractLimits.MaxValidationErrors).ToArray());
    }

    /// <summary>Gets whether the inspected contract is valid.</summary>
    public bool IsValid => Errors.Count == 0;
}
