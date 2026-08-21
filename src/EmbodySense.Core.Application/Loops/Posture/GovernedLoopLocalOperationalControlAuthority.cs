using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Projects composition-retained local actor authority without trusting caller-supplied scope.</summary>
public sealed class GovernedLoopLocalOperationalControlAuthority : IGovernedLoopOperationalControlAuthorityPort
{
    private readonly string _actorId;
    private readonly TimeProvider _timeProvider;
    private readonly string _surfaceId;
    private readonly string _workspaceId;

    /// <summary>Creates a trusted local authority source bound to one runtime actor and workspace.</summary>
    public GovernedLoopLocalOperationalControlAuthority(string workspaceId, string actorId, string surfaceId, TimeProvider? timeProvider = null)
    {
        if (!GovernedLoopOperationalContract.IsWorkspaceId(workspaceId))
        {
            throw new ArgumentException("Operational control authority requires a bounded trusted workspace identity.", nameof(workspaceId));
        }
        if (!CustomLoopArtifactIdentifier.IsValid(actorId, GovernedLoopOperationalPostureLimits.MaxActorIdCharacters))
        {
            throw new ArgumentException("Operational control authority requires a bounded trusted actor identity.", nameof(actorId));
        }
        if (!CustomLoopArtifactIdentifier.IsValid(surfaceId, GovernedLoopOperationalPostureLimits.MaxSurfaceIdCharacters))
        {
            throw new ArgumentException("Operational control authority requires a bounded trusted caller surface.", nameof(surfaceId));
        }
        _workspaceId = workspaceId;
        _actorId = actorId;
        _surfaceId = surfaceId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<GovernedLoopOperationalControlAuthority?> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Create(permitted: true, "local-operational-control-authorized"));
    }

    /// <inheritdoc />
    public Task<GovernedLoopOperationalControlAuthority?> ReadAsync(
        GovernedLoopOperationalControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var permitted = string.Equals(request.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            && string.Equals(request.ActorId, _actorId, StringComparison.Ordinal);
        permitted = permitted && string.Equals(request.SurfaceId, _surfaceId, StringComparison.Ordinal);
        var reason = permitted ? "local-operational-control-authorized" : "local-operational-control-scope-denied";
        return Task.FromResult(Create(permitted, reason));
    }

    private GovernedLoopOperationalControlAuthority? Create(bool permitted, string reason)
    {
        var observedAtUtc = _timeProvider.GetUtcNow();
        return !GovernedLoopOperationalContract.IsUtc(observedAtUtc)
            ? null
            : new GovernedLoopOperationalControlAuthority(
            GovernedLoopOperationalControlAuthority.CurrentSchemaVersion,
            _workspaceId,
            _actorId,
            _surfaceId,
            observedAtUtc,
            GovernedLoopOperationalHash.Authority(_workspaceId, _actorId, _surfaceId, observedAtUtc, permitted, reason),
            permitted,
            reason);
    }
}
