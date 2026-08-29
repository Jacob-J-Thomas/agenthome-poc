using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal sealed class HumanInputContinuationResponseActorAuthenticator(AuthorityActorId actor) : IHumanInputResponseActorAuthenticator
{
    private const string AuthenticationEvidenceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly AuthorityActorId _actor = actor;

    public Task<HumanInputResponseActorAuthentication> AuthenticateAsync(
        HumanInputResponseActorAuthenticationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanInputResponseActorAuthentication(
            HumanInputResponseActorAuthenticationStatus.Authenticated,
            request.OperationId,
            request.CommandHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            _actor,
            AuthenticationEvidenceHash));
}
