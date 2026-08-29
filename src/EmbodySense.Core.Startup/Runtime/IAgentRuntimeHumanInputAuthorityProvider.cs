using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>Supplies exact server-owned Human Input candidate, grant, authentication, and lifecycle-authorization decisions.</summary>
/// <remarks>Implementations belong at an authenticated interface boundary. The provider receives only server-constructed
/// operation metadata and must never trust actor, role, workspace, clock, grant, or authority assertions from a surface payload.</remarks>
public interface IAgentRuntimeHumanInputAuthorityProvider
{
    /// <summary>Resolves server-owned candidate and grant terms before one exact lifecycle command is built.</summary>
    /// <param name="request">The server-constructed operation terms request.</param>
    /// <param name="cancellationToken">A token that cancels preparation before lifecycle intent begins.</param>
    /// <returns>Server-owned candidate and grant terms, or a fail-closed result.</returns>
    Task<AgentRuntimeHumanInputLifecycleTerms> ResolveLifecycleTermsAsync(
        AgentRuntimeHumanInputLifecycleTermsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Authorizes one exact server-constructed lifecycle command at its trusted evaluation instant.</summary>
    /// <param name="request">The complete server-constructed lifecycle operation metadata.</param>
    /// <param name="cancellationToken">A token that cancels authorization.</param>
    /// <returns>An exact actor authorization decision and server-owned evidence digest.</returns>
    Task<AgentRuntimeHumanInputLifecycleAuthorization> AuthorizeLifecycleAsync(
        AgentRuntimeHumanInputLifecycleAuthorizationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Authenticates one exact server-constructed response operation at its trusted evaluation instant.</summary>
    /// <param name="request">The complete server-constructed response operation metadata.</param>
    /// <param name="cancellationToken">A token that cancels authentication.</param>
    /// <returns>An exact actor authentication decision and server-owned evidence digest.</returns>
    Task<AgentRuntimeHumanInputResponseAuthentication> AuthenticateResponseAsync(
        AgentRuntimeHumanInputResponseAuthenticationRequest request,
        CancellationToken cancellationToken = default);
}
