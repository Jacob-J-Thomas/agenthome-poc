using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Requests one exact Human Input lifecycle operation without actor, role, workspace, timing, grant, routing, or authority inputs.</summary>
/// <param name="OperationId">The caller-held workspace-global idempotency identity.</param>
/// <param name="Kind">The requested exact lifecycle operation kind.</param>
/// <param name="RequestId">The stable target request identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version, or zero for creation.</param>
/// <param name="ExpectedLifecycleStatus">The exact optimistic posture, or unknown for creation.</param>
/// <param name="ExpectedRequest">The exact immutable request reference, or null for creation.</param>
/// <param name="CandidateKey">An optional opaque server-resolved candidate selector for candidate-bearing operations.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason.</param>
public sealed record HumanInputLifecycleOperationInput(
    string OperationId,
    HumanInputRequestLifecycleOperationKind Kind,
    string RequestId,
    long ExpectedLifecycleVersion,
    HumanInputRequestLifecycleStatus ExpectedLifecycleStatus,
    HumanInputRequestReference? ExpectedRequest,
    string? CandidateKey,
    string Reason);
