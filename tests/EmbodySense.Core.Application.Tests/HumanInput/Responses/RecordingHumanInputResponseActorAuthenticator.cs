using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

internal sealed class RecordingHumanInputResponseActorAuthenticator : IHumanInputResponseActorAuthenticator
{
    internal AuthorityActorId ActorId { get; set; } = HumanInputResponseLifecycleTestData.Actor("user-one");

    internal HumanInputResponseActorAuthenticationStatus Status { get; set; } = HumanInputResponseActorAuthenticationStatus.Authenticated;

    internal bool Throw { get; set; }

    internal Func<HumanInputResponseActorAuthenticationRequest, HumanInputResponseActorAuthentication>? Override { get; set; }

    internal List<HumanInputResponseActorAuthenticationRequest> Requests { get; } = [];

    public Task<HumanInputResponseActorAuthentication> AuthenticateAsync(
        HumanInputResponseActorAuthenticationRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Throw)
        {
            throw new InvalidOperationException("Authentication unavailable.");
        }
        if (Override is not null)
        {
            return Task.FromResult(Override(request));
        }
        var authenticated = Status == HumanInputResponseActorAuthenticationStatus.Authenticated;
        return Task.FromResult(
            new HumanInputResponseActorAuthentication(
                Status,
                request.OperationId,
                request.CommandHash,
                request.WorkspaceId,
                request.EvaluatedAtUtc,
                authenticated ? ActorId : null,
                authenticated ? HumanInputResponseLifecycleTestData.Hash('a') : string.Empty));
    }
}
