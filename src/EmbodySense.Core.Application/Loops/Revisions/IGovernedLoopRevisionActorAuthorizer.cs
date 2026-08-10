using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Evaluates current server-owned actor authority for one exact canonical lifecycle request.</summary>
public interface IGovernedLoopRevisionActorAuthorizer
{
    /// <summary>Authorizes one exact request without trusting any client-supplied authority flag or evidence.</summary>
    /// <param name="request">The exact canonical request binding and trusted evaluation time.</param>
    /// <param name="cancellationToken">The cancellation token used while evaluating current authority.</param>
    /// <returns>A bounded decision that echoes the exact request binding and server-owned evidence digest.</returns>
    Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
        GovernedLoopRevisionActorAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}
