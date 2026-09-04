using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputRuntimeFacadeTestAuthorityProvider : IAgentRuntimeHumanInputAuthorityProvider
{
    private AuthorityActorId _actor;

    internal HumanInputRuntimeFacadeTestAuthorityProvider(string actor = "user-one")
    {
        Assert.True(AuthorityActorId.TryParse(actor, out var parsed, out _));
        _actor = parsed!;
    }

    internal AgentRuntimeHumanInputAuthorityStatus LifecycleAuthorizationStatus { get; set; } = AgentRuntimeHumanInputAuthorityStatus.Ready;

    internal AgentRuntimeHumanInputAuthorityStatus LifecycleTermsStatus { get; set; } = AgentRuntimeHumanInputAuthorityStatus.Ready;

    internal AgentRuntimeHumanInputAuthorityStatus ResponseAuthenticationStatus { get; set; } = AgentRuntimeHumanInputAuthorityStatus.Ready;

    internal HumanInputRequest? LifecycleCandidateRequest { get; set; }

    internal IDictionary<string, HumanInputRequest> LifecycleCandidates { get; } = new Dictionary<string, HumanInputRequest>(StringComparer.Ordinal);

    internal AuthorityGrantReference? LifecycleGrantReference { get; set; }

    internal bool DelayLifecycleTermsUntilCancellation { get; set; }

    internal bool ThrowDuringLifecycleTerms { get; set; }

    internal bool ReturnNullLifecycleTerms { get; set; }

    internal TaskCompletionSource<bool>? LifecycleTermsEntered { get; set; }

    internal int LifecycleAuthorizations { get; private set; }

    internal int LifecycleTermsResolutions { get; private set; }

    internal int ResponseAuthentications { get; private set; }

    internal void UseActor(string actor)
    {
        Assert.True(AuthorityActorId.TryParse(actor, out var parsed, out _));
        _actor = parsed!;
    }

    public async Task<AgentRuntimeHumanInputLifecycleTerms> ResolveLifecycleTermsAsync(
        AgentRuntimeHumanInputLifecycleTermsRequest request,
        CancellationToken cancellationToken = default)
    {
        LifecycleTermsResolutions++;
        LifecycleTermsEntered?.TrySetResult(true);
        if (ThrowDuringLifecycleTerms)
        {
            throw new InvalidOperationException("The test authority boundary is unavailable.");
        }

        if (ReturnNullLifecycleTerms)
        {
            return null!;
        }

        if (DelayLifecycleTermsUntilCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var candidate = request.CandidateKey is not null && LifecycleCandidates.TryGetValue(request.CandidateKey, out var selectedCandidate)
            ? selectedCandidate
            : LifecycleCandidateRequest;
        return new AgentRuntimeHumanInputLifecycleTerms(LifecycleTermsStatus, candidate, LifecycleGrantReference);
    }

    public Task<AgentRuntimeHumanInputLifecycleAuthorization> AuthorizeLifecycleAsync(
        AgentRuntimeHumanInputLifecycleAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LifecycleAuthorizations++;
        return Task.FromResult(new AgentRuntimeHumanInputLifecycleAuthorization(
            LifecycleAuthorizationStatus,
            _actor,
            Hash("lifecycle\n" + request.OperationId + "\n" + request.RequestHash)));
    }

    public Task<AgentRuntimeHumanInputResponseAuthentication> AuthenticateResponseAsync(
        AgentRuntimeHumanInputResponseAuthenticationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResponseAuthentications++;
        return Task.FromResult(new AgentRuntimeHumanInputResponseAuthentication(
            ResponseAuthenticationStatus,
            _actor,
            Hash("response\n" + request.OperationId + "\n" + request.CommandHash)));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
