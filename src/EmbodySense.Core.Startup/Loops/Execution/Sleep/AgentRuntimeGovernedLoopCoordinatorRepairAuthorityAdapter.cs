using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Adapts request-scoped authenticated interface authority to the coordinator repair admission port.</summary>
internal sealed class AgentRuntimeGovernedLoopCoordinatorRepairAuthorityAdapter : IGovernedLoopOperationalControlAuthorityPort
{
    private readonly IAgentRuntimeGovernedLoopCoordinatorRepairAuthorityProvider? _provider;
    private readonly string _surfaceId;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspaceId;

    internal AgentRuntimeGovernedLoopCoordinatorRepairAuthorityAdapter(
        string workspaceId,
        string surfaceId,
        IAgentRuntimeGovernedLoopCoordinatorRepairAuthorityProvider? provider,
        TimeProvider? timeProvider = null)
    {
        if (!GovernedLoopOperationalContract.IsWorkspaceId(workspaceId))
        {
            throw new ArgumentException("Coordinator repair authority requires a bounded trusted workspace identity.", nameof(workspaceId));
        }
        if (!CustomLoopArtifactIdentifier.IsValid(surfaceId, GovernedLoopOperationalPostureLimits.MaxSurfaceIdCharacters))
        {
            throw new ArgumentException("Coordinator repair authority requires a bounded trusted interface surface.", nameof(surfaceId));
        }

        _workspaceId = workspaceId;
        _surfaceId = surfaceId;
        _provider = provider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GovernedLoopOperationalControlAuthority?> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_provider is null)
        {
            throw new InvalidOperationException("Coordinator repair requires an authenticated interface authority provider.");
        }

        var decision = await _provider.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (decision is null || !Enum.IsDefined(decision.Status))
        {
            return null;
        }
        if (decision.Status == AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus.Unavailable)
        {
            throw new InvalidOperationException("The authenticated coordinator repair operator is unavailable.");
        }
        if (!CustomLoopArtifactIdentifier.IsValid(decision.ActorId, GovernedLoopOperationalPostureLimits.MaxActorIdCharacters))
        {
            return null;
        }

        return Create(
            decision.ActorId!,
            decision.Status == AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus.Ready,
            decision.Status == AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus.Ready
                ? "coordinator-repair-current-operator-authorized"
                : "coordinator-repair-current-operator-denied");
    }

    public async Task<GovernedLoopOperationalControlAuthority?> ReadAsync(
        GovernedLoopOperationalControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        var permitted = current.Permitted
            && string.Equals(request.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            && string.Equals(request.ActorId, current.ActorId, StringComparison.Ordinal)
            && string.Equals(request.SurfaceId, _surfaceId, StringComparison.Ordinal);
        return permitted
            ? current
            : Create(current.ActorId, false, "coordinator-repair-current-operator-scope-denied");
    }

    private GovernedLoopOperationalControlAuthority? Create(string actorId, bool permitted, string reasonCode)
    {
        var observedAtUtc = _timeProvider.GetUtcNow();
        return !GovernedLoopOperationalContract.IsUtc(observedAtUtc)
            ? null
            : new GovernedLoopOperationalControlAuthority(
                GovernedLoopOperationalControlAuthority.CurrentSchemaVersion,
                _workspaceId,
                actorId,
                _surfaceId,
                observedAtUtc,
                GovernedLoopOperationalHash.Authority(_workspaceId, actorId, _surfaceId, observedAtUtc, permitted, reasonCode),
                permitted,
                reasonCode);
    }
}
