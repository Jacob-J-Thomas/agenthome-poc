using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Identifies one complete server-constructed Human Input lifecycle command for boundary authorization.</summary>
/// <param name="OperationId">The exact workspace-global operation identity.</param>
/// <param name="RequestHash">The exact canonical command hash.</param>
/// <param name="Kind">The requested lifecycle operation kind.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="ExpectedLifecycleVersion">The command's exact optimistic lifecycle version.</param>
/// <param name="WorkspaceId">The server-derived canonical workspace identity.</param>
/// <param name="EvaluatedAtUtc">The server-owned exact trusted evaluation instant.</param>
public sealed record AgentRuntimeHumanInputLifecycleAuthorizationRequest(
    string OperationId,
    string RequestHash,
    HumanInputRequestLifecycleOperationKind Kind,
    string RequestId,
    long ExpectedLifecycleVersion,
    string WorkspaceId,
    DateTimeOffset EvaluatedAtUtc);
