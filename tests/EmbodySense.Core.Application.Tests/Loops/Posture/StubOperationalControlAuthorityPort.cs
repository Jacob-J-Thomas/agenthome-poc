using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Posture;

internal sealed class StubOperationalControlAuthorityPort : IGovernedLoopOperationalControlAuthorityPort
{
    internal required string WorkspaceId { get; init; }
    internal DateTimeOffset ObservedAtUtc { get; set; }
    internal string ActorId { get; set; } = "actor-1";
    internal string SurfaceId { get; set; } = "startup";
    internal bool Permitted { get; set; } = true;

    public Task<GovernedLoopOperationalControlAuthority?> ReadCurrentAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Create());

    public Task<GovernedLoopOperationalControlAuthority?> ReadAsync(
        GovernedLoopOperationalControlRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Create());

    private GovernedLoopOperationalControlAuthority? Create()
    {
        const string Reason = "test-operational-authority";
        return new GovernedLoopOperationalControlAuthority(
            GovernedLoopOperationalControlAuthority.CurrentSchemaVersion,
            WorkspaceId,
            ActorId,
            SurfaceId,
            ObservedAtUtc,
            GovernedLoopOperationalHash.Authority(WorkspaceId, ActorId, SurfaceId, ObservedAtUtc, Permitted, Reason),
            Permitted,
            Reason);
    }
}
