using EmbodySense.Core.Application.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Resolves the current authenticated actor through a server-owned identity boundary.</summary>
public interface IHumanInputResponseActorAuthenticator
{
    /// <summary>Authenticates the current caller for one exact response operation and trusted evaluation instant.</summary>
    /// <param name="request">The server-constructed authentication request.</param>
    /// <param name="cancellationToken">A token that cancels authentication.</param>
    /// <returns>An exact echoed authentication disposition and evidence digest.</returns>
    Task<HumanInputResponseActorAuthentication> AuthenticateAsync(HumanInputResponseActorAuthenticationRequest request, CancellationToken cancellationToken = default);
}
