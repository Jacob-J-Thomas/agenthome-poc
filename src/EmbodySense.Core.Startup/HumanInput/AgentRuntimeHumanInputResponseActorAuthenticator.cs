using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Adapts one server-owned runtime authority provider to the Application response-authentication port.</summary>
internal sealed class AgentRuntimeHumanInputResponseActorAuthenticator(
    IAgentRuntimeHumanInputAuthorityProvider provider) : IHumanInputResponseActorAuthenticator
{
    private readonly IAgentRuntimeHumanInputAuthorityProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<HumanInputResponseActorAuthentication> AuthenticateAsync(
        HumanInputResponseActorAuthenticationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AgentRuntimeHumanInputResponseAuthentication decision;
        try
        {
            decision = await _provider.AuthenticateResponseAsync(
                new AgentRuntimeHumanInputResponseAuthenticationRequest(
                    request.OperationId,
                    request.CommandHash,
                    request.Kind,
                    request.RequestId,
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
            AgentRuntimeHumanInputAuthorityStatus.Ready => new HumanInputResponseActorAuthentication(
                HumanInputResponseActorAuthenticationStatus.Authenticated,
                request.OperationId,
                request.CommandHash,
                request.WorkspaceId,
                request.EvaluatedAtUtc,
                decision.ActorId,
                decision.AuthenticationEvidenceHash),
            AgentRuntimeHumanInputAuthorityStatus.Denied => new HumanInputResponseActorAuthentication(
                HumanInputResponseActorAuthenticationStatus.Denied,
                request.OperationId,
                request.CommandHash,
                request.WorkspaceId,
                request.EvaluatedAtUtc,
                null,
                string.Empty),
            _ => Unavailable(request),
        };
    }

    private static HumanInputResponseActorAuthentication Unavailable(HumanInputResponseActorAuthenticationRequest request)
        => new(
            HumanInputResponseActorAuthenticationStatus.Unavailable,
            request.OperationId,
            request.CommandHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            null,
            string.Empty);
}
