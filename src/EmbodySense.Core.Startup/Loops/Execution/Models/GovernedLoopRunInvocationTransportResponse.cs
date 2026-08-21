namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one governed-loop invocation without exposing internal authority or persistence evidence on an interface transport.</summary>
/// <param name="Status">The overall invocation disposition.</param>
/// <param name="AdmissionStatus">The canonical admission status, when admission was attempted.</param>
/// <param name="AdmissionFailureCode">The bounded admission failure code, when rejected.</param>
/// <param name="MaterializationStatus">The durable run materialization status, when materialization was attempted.</param>
/// <param name="ExecutionStatus">The ordered execution status, when execution was attempted.</param>
/// <param name="WasDispatched">Whether a provider request crossed its dispatch boundary.</param>
/// <param name="Run">The bounded caller-visible durable run projection, when available.</param>
/// <param name="Detail">A bounded safe disposition detail.</param>
public sealed record GovernedLoopRunInvocationTransportResponse(
    string Status,
    string? AdmissionStatus,
    string? AdmissionFailureCode,
    string? MaterializationStatus,
    string? ExecutionStatus,
    bool WasDispatched,
    GovernedLoopRunTransportSnapshot? Run,
    string Detail);
