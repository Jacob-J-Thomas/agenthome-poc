namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Requests server-owned authentication for one exact Human Input response operation.</summary>
/// <param name="OperationId">The workspace-global operation identity.</param>
/// <param name="Kind">The requested response operation family.</param>
/// <param name="RequestId">The stable target request lifecycle identity.</param>
/// <param name="CommandHash">The canonical exact-intent hash.</param>
/// <param name="WorkspaceId">The server-configured workspace identity.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC authentication instant.</param>
public sealed partial record HumanInputResponseActorAuthenticationRequest(
    string OperationId,
    EmbodySense.Core.Common.HumanInput.Responses.Models.HumanInputResponseOperationKind Kind,
    string RequestId,
    string CommandHash,
    string WorkspaceId,
    DateTimeOffset EvaluatedAtUtc);
