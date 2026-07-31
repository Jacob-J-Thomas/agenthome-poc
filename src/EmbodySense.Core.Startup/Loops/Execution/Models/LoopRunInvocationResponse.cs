using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Reports durable admission, execution, validation, reconciliation, and integrity outcomes for an invocation.
/// </summary>
/// <param name="AdmissionStatus">The admission status.</param>
/// <param name="ExecutionStatus">The execution status.</param>
/// <param name="WasDispatched">The was dispatched.</param>
/// <param name="Run">The run.</param>
/// <param name="ValidationErrors">The validation errors.</param>
/// <param name="Detail">The detail.</param>
public sealed record LoopRunInvocationResponse(
    string AdmissionStatus,
    string? ExecutionStatus,
    bool WasDispatched,
    LoopRunSnapshot? Run,
    IReadOnlyList<LoopValidationError> ValidationErrors,
    string Detail);
