using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Selects server-owned candidate and grant terms for one Human Input lifecycle operation.</summary>
/// <param name="OperationId">The caller-held idempotency identity.</param>
/// <param name="Kind">The requested lifecycle operation kind.</param>
/// <param name="RequestId">The target stable request identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version supplied by the surface.</param>
/// <param name="ExpectedLifecycleStatus">The exact optimistic lifecycle posture supplied by the surface.</param>
/// <param name="ExpectedRequest">The exact optimistic immutable request reference supplied by the surface.</param>
/// <param name="CandidateKey">An optional opaque server-resolved candidate selector; it carries no actor, role, workspace, timing, routing, or grant data.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason supplied by the surface.</param>
public sealed record AgentRuntimeHumanInputLifecycleTermsRequest(
    string OperationId,
    HumanInputRequestLifecycleOperationKind Kind,
    string RequestId,
    long ExpectedLifecycleVersion,
    HumanInputRequestLifecycleStatus ExpectedLifecycleStatus,
    HumanInputRequestReference? ExpectedRequest,
    string? CandidateKey,
    string Reason);
