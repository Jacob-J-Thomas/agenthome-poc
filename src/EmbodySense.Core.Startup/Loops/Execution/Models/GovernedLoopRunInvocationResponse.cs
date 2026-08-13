namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one canonical governed-loop coordination outcome without surface-specific reinterpretation.</summary>
/// <param name="Status">The closed Startup coordination status.</param>
/// <param name="AdmissionStatus">The canonical admission status when admission was attempted.</param>
/// <param name="AdmissionFailureCode">The value-free definitive admission failure code, when rejected.</param>
/// <param name="MaterializationStatus">The canonical run-materialization status when admission succeeded.</param>
/// <param name="ExecutionStatus">The ordered-runtime status when execution was considered.</param>
/// <param name="WasDispatched">Whether the ordered runtime crossed a provider boundary in this call.</param>
/// <param name="AdmissionOutcome">The validated exact immutable admission or rejection evidence, when durably proved.</param>
/// <param name="Run">The latest authenticated public run projection, when available.</param>
/// <param name="Detail">A bounded non-secret diagnostic.</param>
public sealed record GovernedLoopRunInvocationResponse(
    string Status,
    string? AdmissionStatus,
    string? AdmissionFailureCode,
    string? MaterializationStatus,
    string? ExecutionStatus,
    bool WasDispatched,
    GovernedLoopAdmissionOutcomeSnapshot? AdmissionOutcome,
    LoopRunSnapshot? Run,
    string Detail);
