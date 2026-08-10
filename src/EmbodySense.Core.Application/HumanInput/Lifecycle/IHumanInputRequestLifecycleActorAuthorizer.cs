using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle;

/// <summary>Authenticates and authorizes one exact Human Input lifecycle command through a server-owned authority source.</summary>
public interface IHumanInputRequestLifecycleActorAuthorizer
{
    /// <summary>Evaluates current actor authority for one exact command and trusted workspace/time boundary.</summary>
    /// <param name="request">The exact server-constructed authorization request.</param>
    /// <param name="cancellationToken">A token that cancels authorization.</param>
    /// <returns>An exact echoed value-free decision and evidence digest.</returns>
    Task<HumanInputRequestLifecycleActorAuthorization> AuthorizeAsync(HumanInputRequestLifecycleActorAuthorizationRequest request, CancellationToken cancellationToken = default);
}
