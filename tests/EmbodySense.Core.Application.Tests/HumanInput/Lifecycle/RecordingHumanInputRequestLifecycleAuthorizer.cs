using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

internal sealed class RecordingHumanInputRequestLifecycleAuthorizer : IHumanInputRequestLifecycleActorAuthorizer
{
    internal List<HumanInputRequestLifecycleActorAuthorizationRequest> Requests { get; } = [];

    internal Func<HumanInputRequestLifecycleActorAuthorizationRequest, CancellationToken, HumanInputRequestLifecycleActorAuthorization>? Handler { get; set; }

    public Task<HumanInputRequestLifecycleActorAuthorization> AuthorizeAsync(
        HumanInputRequestLifecycleActorAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var decision = Handler?.Invoke(request, cancellationToken) ?? new HumanInputRequestLifecycleActorAuthorization(
            HumanInputRequestLifecycleActorAuthorizationStatus.Authorized,
            request.Command.OperationId,
            request.RequestHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            AuthorityGrantApplicationTestFixture.Actor("human-input-actor"),
            HumanInputRequestLifecycleTestData.Hash('a'));
        return Task.FromResult(decision);
    }
}
