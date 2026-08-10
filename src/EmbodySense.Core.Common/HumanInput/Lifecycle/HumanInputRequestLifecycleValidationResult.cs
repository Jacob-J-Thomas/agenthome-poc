namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

public sealed partial record HumanInputRequestLifecycleValidationResult
{
    /// <summary>Creates a validation result by snapshotting the supplied errors.</summary>
    /// <param name="errors">The bounded deterministic errors.</param>
    public HumanInputRequestLifecycleValidationResult(IEnumerable<HumanInputRequestLifecycleValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = Array.AsReadOnly(errors.Take(HumanInputRequestLifecycleContractLimits.MaxValidationErrors).ToArray());
    }

    /// <summary>Gets whether the inspected contract is valid.</summary>
    public bool IsValid => Errors.Count == 0;
}
