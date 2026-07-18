using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopAdmissionResult(
    CustomLoopAdmissionStatus Status,
    CustomLoopRunRecord? Run,
    IReadOnlyList<CustomLoopValidationError> ValidationErrors,
    string Detail)
{
    public bool IsAdmitted => Status is CustomLoopAdmissionStatus.Admitted or CustomLoopAdmissionStatus.Replayed;

    public static CustomLoopAdmissionResult Invalid(IReadOnlyList<CustomLoopValidationError> errors) => new(CustomLoopAdmissionStatus.Invalid, null, errors, "The custom-loop invocation is invalid.");
}
