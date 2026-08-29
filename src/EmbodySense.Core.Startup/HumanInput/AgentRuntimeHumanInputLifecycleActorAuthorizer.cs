using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Adapts one server-owned runtime authority provider to the Application lifecycle-authorization port.</summary>
internal sealed class AgentRuntimeHumanInputLifecycleActorAuthorizer(
    IAgentRuntimeHumanInputAuthorityProvider provider) : IHumanInputRequestLifecycleActorAuthorizer
{
    private readonly IAgentRuntimeHumanInputAuthorityProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<HumanInputRequestLifecycleActorAuthorization> AuthorizeAsync(
        HumanInputRequestLifecycleActorAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AgentRuntimeHumanInputLifecycleAuthorization decision;
        try
        {
            decision = await _provider.AuthorizeLifecycleAsync(
                new AgentRuntimeHumanInputLifecycleAuthorizationRequest(
                    request.Command.OperationId,
                    request.RequestHash,
                    request.Command.Kind,
                    request.Command.RequestId,
                    request.Command.ExpectedLifecycleVersion,
                    request.WorkspaceId,
                    request.EvaluatedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable(request);
        }

        return decision?.Status switch
        {
            AgentRuntimeHumanInputAuthorityStatus.Ready => new HumanInputRequestLifecycleActorAuthorization(
                HumanInputRequestLifecycleActorAuthorizationStatus.Authorized,
                request.Command.OperationId,
                request.RequestHash,
                request.WorkspaceId,
                request.EvaluatedAtUtc,
                decision.ActorId,
                decision.AuthorityEvidenceHash),
            AgentRuntimeHumanInputAuthorityStatus.Denied => new HumanInputRequestLifecycleActorAuthorization(
                HumanInputRequestLifecycleActorAuthorizationStatus.Denied,
                request.Command.OperationId,
                request.RequestHash,
                request.WorkspaceId,
                request.EvaluatedAtUtc,
                decision.ActorId,
                decision.AuthorityEvidenceHash),
            _ => Unavailable(request),
        };
    }

    private static HumanInputRequestLifecycleActorAuthorization Unavailable(
        HumanInputRequestLifecycleActorAuthorizationRequest request)
        => new(
            HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable,
            request.Command.OperationId,
            request.RequestHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            null,
            string.Empty);
}
