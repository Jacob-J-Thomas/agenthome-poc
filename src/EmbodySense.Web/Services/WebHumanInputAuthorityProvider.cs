using EmbodySense.Core.Startup.HumanInput;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Services;

/// <summary>Adapts the current authenticated Web session to the server-owned Startup Human Input authority boundary.</summary>
/// <remarks>The provider reads HttpContext only for the current call, requires the local Web session scheme, pins the Web
/// actor, and ignores claims, browser connection identity, and payload actor fields.</remarks>
public sealed class WebHumanInputAuthorityProvider : IAgentRuntimeHumanInputAuthorityProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHumanInputSupersedeCandidateRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspaceId;

    /// <summary>Creates one request-context Web provider over the bounded Startup candidate registry.</summary>
    /// <param name="httpContextAccessor">The accessor for the current request only.</param>
    /// <param name="registry">The Startup-owned opaque supersede-candidate registry.</param>
    public WebHumanInputAuthorityProvider(IHttpContextAccessor httpContextAccessor, IHumanInputSupersedeCandidateRegistry registry)
        : this(httpContextAccessor, registry, string.Empty, null)
    {
    }

    /// <summary>Creates one request-context provider with the server-owned workspace root.</summary>
    /// <param name="httpContextAccessor">The accessor for the current request only.</param>
    /// <param name="registry">The Startup-owned opaque supersede-candidate registry.</param>
    /// <param name="workspaceRoot">The server-configured workspace root used to derive the exact scope.</param>
    /// <param name="timeProvider">The trusted authority evaluation clock.</param>
    public WebHumanInputAuthorityProvider(IHttpContextAccessor httpContextAccessor, IHumanInputSupersedeCandidateRegistry registry, string workspaceRoot, TimeProvider? timeProvider = null)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workspaceId = string.IsNullOrWhiteSpace(workspaceRoot) ? string.Empty : HumanInputWebAuthority.GetWorkspaceId(workspaceRoot);
    }

    /// <inheritdoc />
    public Task<AgentRuntimeHumanInputLifecycleTerms> ResolveLifecycleTermsAsync(AgentRuntimeHumanInputLifecycleTermsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var sessionStatus = GetSessionStatus();
        if (sessionStatus != AgentRuntimeHumanInputAuthorityStatus.Ready)
        {
            return Task.FromResult(new AgentRuntimeHumanInputLifecycleTerms(sessionStatus, null, null));
        }

        var kind = request.Kind.ToString();
        if (kind is "Reject" or "Cancel")
        {
            return Task.FromResult(new AgentRuntimeHumanInputLifecycleTerms(AgentRuntimeHumanInputAuthorityStatus.Ready, null, null));
        }

        // Remind has no candidate key or browser-supplied grant. Startup obtains and revalidates the
        // current canonical grant while constructing the lifecycle command.
        if (string.Equals(kind, "Remind", StringComparison.Ordinal))
        {
            return Task.FromResult(request.ExpectedRequest is not null && string.IsNullOrWhiteSpace(request.CandidateKey)
                ? new AgentRuntimeHumanInputLifecycleTerms(AgentRuntimeHumanInputAuthorityStatus.Ready, null, null)
                : new AgentRuntimeHumanInputLifecycleTerms(AgentRuntimeHumanInputAuthorityStatus.Unavailable, null, null));
        }

        if (kind is not ("Reroute" or "Amend" or "Supersede")
            || request.ExpectedRequest is null
            || string.IsNullOrWhiteSpace(request.CandidateKey)
            || string.IsNullOrWhiteSpace(_workspaceId)
            || !_registry.TryResolve(request.Kind, request.CandidateKey, _workspaceId, WorkspaceActors.Web, request.OperationId, request.RequestId, request.ExpectedLifecycleVersion, request.ExpectedRequest.RequestVersionId, request.ExpectedRequest.RequestHash, _timeProvider.GetUtcNow(), out var resolution)
            || resolution is null)
        {
            return Task.FromResult(new AgentRuntimeHumanInputLifecycleTerms(AgentRuntimeHumanInputAuthorityStatus.Unavailable, null, null));
        }

        return Task.FromResult(new AgentRuntimeHumanInputLifecycleTerms(AgentRuntimeHumanInputAuthorityStatus.Ready, resolution.CandidateRequest, resolution.GrantReference));
    }

    /// <inheritdoc />
    public Task<AgentRuntimeHumanInputLifecycleAuthorization> AuthorizeLifecycleAsync(AgentRuntimeHumanInputLifecycleAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var sessionStatus = GetSessionStatus();
        return sessionStatus == AgentRuntimeHumanInputAuthorityStatus.Ready
            ? Task.FromResult(HumanInputWebAuthority.AuthorizeLifecycle(true, request.OperationId, request.RequestHash, request.WorkspaceId, request.EvaluatedAtUtc))
            : Task.FromResult(new AgentRuntimeHumanInputLifecycleAuthorization(sessionStatus, null, string.Empty));
    }

    /// <inheritdoc />
    public Task<AgentRuntimeHumanInputResponseAuthentication> AuthenticateResponseAsync(AgentRuntimeHumanInputResponseAuthenticationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var sessionStatus = GetSessionStatus();
        return sessionStatus == AgentRuntimeHumanInputAuthorityStatus.Ready
            ? Task.FromResult(HumanInputWebAuthority.AuthenticateResponse(true, request.OperationId, request.CommandHash, request.WorkspaceId, request.EvaluatedAtUtc))
            : Task.FromResult(new AgentRuntimeHumanInputResponseAuthentication(sessionStatus, null, string.Empty));
    }

    private AgentRuntimeHumanInputAuthorityStatus GetSessionStatus()
    {
        HttpContext? context;
        try
        {
            context = _httpContextAccessor.HttpContext;
        }
        catch
        {
            return AgentRuntimeHumanInputAuthorityStatus.Unavailable;
        }

        if (context is null || context.RequestAborted.IsCancellationRequested)
        {
            return AgentRuntimeHumanInputAuthorityStatus.Unavailable;
        }

        try
        {
            var identities = context.User?.Identities?.ToArray();
            if (identities is null || identities.Length != 1 || !identities[0].IsAuthenticated)
            {
                return AgentRuntimeHumanInputAuthorityStatus.Unavailable;
            }

            return string.Equals(identities[0].AuthenticationType, WebSessionAuthenticationDefaults.Scheme, StringComparison.Ordinal)
                ? AgentRuntimeHumanInputAuthorityStatus.Ready
                : AgentRuntimeHumanInputAuthorityStatus.Denied;
        }
        catch
        {
            return AgentRuntimeHumanInputAuthorityStatus.Unavailable;
        }
    }

}
