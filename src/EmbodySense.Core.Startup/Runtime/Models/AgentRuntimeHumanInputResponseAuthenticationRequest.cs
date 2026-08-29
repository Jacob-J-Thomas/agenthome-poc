using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Identifies one complete server-constructed Human Input response command for boundary authentication.</summary>
/// <param name="OperationId">The exact workspace-global operation identity.</param>
/// <param name="CommandHash">The exact canonical response-command hash.</param>
/// <param name="Kind">The requested response operation kind.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="WorkspaceId">The server-derived canonical workspace identity.</param>
/// <param name="EvaluatedAtUtc">The server-owned exact trusted evaluation instant.</param>
public sealed record AgentRuntimeHumanInputResponseAuthenticationRequest(
    string OperationId,
    string CommandHash,
    HumanInputResponseOperationKind Kind,
    string RequestId,
    string WorkspaceId,
    DateTimeOffset EvaluatedAtUtc);
