using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Common.Loops.Admission;

/// <summary>Represents one immutable bounded admission-contract validation result.</summary>
public sealed record GovernedLoopAdmissionValidationResult
{
    private GovernedLoopAdmissionValidationResult(IReadOnlyList<GovernedLoopAdmissionValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets the stable bounded validation errors.</summary>
    public IReadOnlyList<GovernedLoopAdmissionValidationError> Errors { get; }

    /// <summary>Gets a value indicating whether validation succeeded.</summary>
    public bool IsValid => Errors.Count == 0;

    internal static GovernedLoopAdmissionValidationResult FromErrors(IEnumerable<GovernedLoopAdmissionValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.Distinct().Take(GovernedLoopAdmissionLimits.MaxValidationErrors).ToArray();
        return new GovernedLoopAdmissionValidationResult(Array.AsReadOnly(snapshot));
    }
}
