using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Supplies the canonical runtime-surface actor for non-Web Human Input responses.</summary>
/// <remarks>This boundary never accepts an actor, role, workspace, grant, or authority assertion from a conversation or CLI
/// payload. Response-role eligibility is derived later from the exact canonical request by the shared response lifecycle.
/// Lifecycle mutation remains unavailable because non-Web adapters do not own request lifecycle controls.</remarks>
internal sealed class AgentRuntimeSurfaceHumanInputAuthorityProvider : IAgentRuntimeHumanInputAuthorityProvider
{
    private readonly AuthorityActorId _actor;

    internal AgentRuntimeSurfaceHumanInputAuthorityProvider(string actor)
    {
        if (!AuthorityActorId.TryParse(actor, out var parsed, out _))
        {
            throw new ArgumentException("The canonical runtime Human Input actor is invalid.", nameof(actor));
        }

        _actor = parsed!;
    }

    /// <inheritdoc />
    public Task<AgentRuntimeHumanInputLifecycleTerms> ResolveLifecycleTermsAsync(
        AgentRuntimeHumanInputLifecycleTermsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentRuntimeHumanInputLifecycleTerms(AgentRuntimeHumanInputAuthorityStatus.Unavailable, null, null));
    }

    /// <inheritdoc />
    public Task<AgentRuntimeHumanInputLifecycleAuthorization> AuthorizeLifecycleAsync(
        AgentRuntimeHumanInputLifecycleAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentRuntimeHumanInputLifecycleAuthorization(AgentRuntimeHumanInputAuthorityStatus.Unavailable, null, string.Empty));
    }

    /// <inheritdoc />
    public Task<AgentRuntimeHumanInputResponseAuthentication> AuthenticateResponseAsync(
        AgentRuntimeHumanInputResponseAuthenticationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new AgentRuntimeHumanInputResponseAuthentication(
            AgentRuntimeHumanInputAuthorityStatus.Ready,
            _actor,
            ComputeEvidenceHash(request)));
    }

    private string ComputeEvidenceHash(AgentRuntimeHumanInputResponseAuthenticationRequest request)
    {
        var material = string.Join(
            '\n',
            "embodysense.human-input.runtime-surface-response.v1",
            _actor.Value,
            request.OperationId,
            request.CommandHash,
            request.Kind.ToString(),
            request.RequestId,
            request.WorkspaceId,
            request.EvaluatedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
