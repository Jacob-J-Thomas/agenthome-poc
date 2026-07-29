using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunInvocationResponse(
    string AdmissionStatus,
    string? ExecutionStatus,
    bool WasDispatched,
    LoopRunSnapshot? Run,
    IReadOnlyList<LoopValidationError> ValidationErrors,
    string Detail);
