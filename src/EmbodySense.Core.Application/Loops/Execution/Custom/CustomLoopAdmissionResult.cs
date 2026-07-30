using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Represents a custom loop admission result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Run">The run.</param>
/// <param name="ValidationErrors">The validation errors.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopAdmissionResult(
    CustomLoopAdmissionStatus Status,
    CustomLoopRunRecord? Run,
    IReadOnlyList<CustomLoopValidationError> ValidationErrors,
    string Detail)
{
    /// <summary>
    /// Gets a value indicating whether the value is admitted.
    /// </summary>
    /// <value><see langword="true"/> when the value is admitted; otherwise, <see langword="false"/>.</value>
    public bool IsAdmitted => Status is CustomLoopAdmissionStatus.Admitted or CustomLoopAdmissionStatus.Replayed;

    /// <summary>
    /// Creates a custom loop admission result representing invalid.
    /// </summary>
    /// <param name="errors">The errors.</param>
    /// <returns>The custom loop admission result.</returns>
    public static CustomLoopAdmissionResult Invalid(IReadOnlyList<CustomLoopValidationError> errors) => new(CustomLoopAdmissionStatus.Invalid, null, errors, "The custom-loop invocation is invalid.");
}
