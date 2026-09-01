namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Describes one surface-owned Human Input lifecycle intent without lower-layer contract types or authority.</summary>
/// <param name="OperationId">The caller-owned workspace-global idempotency identity.</param>
/// <param name="Kind">The exact lifecycle operation token.</param>
/// <param name="RequestId">The route-bound request identity.</param>
/// <param name="ExpectedLifecycleVersion">The optimistic lifecycle version observed by the surface.</param>
/// <param name="ExpectedLifecycleStatus">The optimistic lifecycle status token observed by the surface.</param>
/// <param name="ExpectedRequest">The detached immutable request reference observed by the surface.</param>
/// <param name="CandidateKey">The opaque Startup-issued supersede candidate key, when applicable.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason.</param>
public sealed record HumanInputSurfaceLifecycleOperationInput(
    string OperationId,
    string Kind,
    string RequestId,
    long ExpectedLifecycleVersion,
    string ExpectedLifecycleStatus,
    HumanInputSurfaceRequestReference? ExpectedRequest,
    string? CandidateKey,
    string Reason);
