using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Binds an internal loop-cancellation request operation to the admitted run actor and parent control receipt.</summary>
/// <remarks>This server-bound authorizer is intentionally separate from public request lifecycle actor facades. It grants
/// no general request authority and only authorizes the one exact Cancel command constructed by cancellation convergence.</remarks>
internal sealed class LoopCancellationHumanInputRequestLifecycleActorAuthorizer : IHumanInputRequestLifecycleActorAuthorizer
{
    private readonly AuthorityActorId _actorId;
    private readonly string _authorityEvidenceHash;
    private readonly HumanInputRequestLifecycleCommand _command;
    private readonly string _workspaceId;

    public LoopCancellationHumanInputRequestLifecycleActorAuthorizer(
        HumanInputRequestLifecycleCommand command,
        AuthorityActorId actorId,
        string parentControlRequestHash,
        string workspaceId)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _actorId = actorId ?? throw new ArgumentNullException(nameof(actorId));
        _authorityEvidenceHash = parentControlRequestHash ?? throw new ArgumentNullException(nameof(parentControlRequestHash));
        _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
    }

    /// <inheritdoc />
    public Task<HumanInputRequestLifecycleActorAuthorization> AuthorizeAsync(
        HumanInputRequestLifecycleActorAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null
            || !Equals(request.Command, _command)
            || !string.Equals(request.RequestHash, _command.RequestHash, StringComparison.Ordinal)
            || !string.Equals(request.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || request.EvaluatedAtUtc == default
            || request.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || !HumanInputRequestLifecycleCommandHash.Matches(request.Command))
        {
            return Task.FromResult(new HumanInputRequestLifecycleActorAuthorization(
                HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable,
                request?.Command.OperationId ?? string.Empty,
                request?.RequestHash ?? string.Empty,
                request?.WorkspaceId ?? string.Empty,
                request?.EvaluatedAtUtc ?? default,
                null,
                string.Empty));
        }

        return Task.FromResult(new HumanInputRequestLifecycleActorAuthorization(
            HumanInputRequestLifecycleActorAuthorizationStatus.Authorized,
            request.Command.OperationId,
            request.RequestHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            _actorId,
            _authorityEvidenceHash));
    }
}
