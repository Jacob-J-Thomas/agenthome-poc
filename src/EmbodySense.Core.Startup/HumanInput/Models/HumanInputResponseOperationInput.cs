using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Requests one exact Human Input response operation without actor, role, workspace, binding, timing, routing, or authority inputs.</summary>
/// <param name="OperationId">The caller-held workspace-global idempotency identity.</param>
/// <param name="Kind">The requested exact response operation kind.</param>
/// <param name="RequestId">The stable target request identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version.</param>
/// <param name="ExpectedLifecycleStatus">The exact optimistic lifecycle posture.</param>
/// <param name="ExpectedRequest">The exact immutable request reference.</param>
/// <param name="ResponseId">The proposed response identifier for submit, or the exact target response identifier for withdraw or select.</param>
/// <param name="Value">The untrusted response value for submit only.</param>
/// <param name="Explanation">The optional bounded untrusted explanation for submit only.</param>
public sealed record HumanInputResponseOperationInput(
    string OperationId,
    HumanInputResponseOperationKind Kind,
    string RequestId,
    long ExpectedLifecycleVersion,
    HumanInputRequestLifecycleStatus ExpectedLifecycleStatus,
    HumanInputRequestReference ExpectedRequest,
    string? ResponseId,
    HumanInputResponseValue? Value,
    string? Explanation);
