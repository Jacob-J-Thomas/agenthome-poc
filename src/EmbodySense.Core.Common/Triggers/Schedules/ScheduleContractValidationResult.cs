using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Returns a bounded deterministic snapshot of schedule-contract validation failures.</summary>
public sealed class ScheduleContractValidationResult
{
    internal ScheduleContractValidationResult(IEnumerable<ScheduleContractError> errors)
    {
        Errors = Array.AsReadOnly((errors ?? throw new ArgumentNullException(nameof(errors)))
            .Distinct()
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Code, StringComparer.Ordinal)
            .Take(ScheduleContractLimits.MaxValidationErrors)
            .ToArray());
    }

    /// <summary>Gets a value indicating whether the contract is valid.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Gets the bounded deterministic error snapshot.</summary>
    public IReadOnlyList<ScheduleContractError> Errors { get; }
}
