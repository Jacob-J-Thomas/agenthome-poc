namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Requests server-owned actor authorization for one exact Human Input lifecycle command.</summary>
/// <param name="Command">The complete bounded command.</param>
/// <param name="RequestHash">The canonical exact-intent hash.</param>
/// <param name="WorkspaceId">The server-configured exact workspace identity.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC authorization instant.</param>
public sealed partial record HumanInputRequestLifecycleActorAuthorizationRequest(
    HumanInputRequestLifecycleCommand Command,
    string RequestHash,
    string WorkspaceId,
    DateTimeOffset EvaluatedAtUtc);
