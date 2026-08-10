namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Requests server-owned authentication for one exact Human Input response operation.</summary>
/// <param name="Command">The complete bounded command.</param>
/// <param name="CommandHash">The canonical exact-intent hash.</param>
/// <param name="WorkspaceId">The server-configured workspace identity.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC authentication instant.</param>
public sealed partial record HumanInputResponseActorAuthenticationRequest(
    HumanInputResponseLifecycleCommand Command,
    string CommandHash,
    string WorkspaceId,
    DateTimeOffset EvaluatedAtUtc);
