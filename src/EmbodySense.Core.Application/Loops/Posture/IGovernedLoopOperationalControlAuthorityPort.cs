using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Resolves current trusted authority independently of caller-provided identifiers.</summary>
public interface IGovernedLoopOperationalControlAuthorityPort
{
    /// <summary>Reads current trusted local authority for posture projection.</summary>
    Task<GovernedLoopOperationalControlAuthority?> ReadCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads current authority for an exact control request before receipt admission or mutation.</summary>
    Task<GovernedLoopOperationalControlAuthority?> ReadAsync(
        GovernedLoopOperationalControlRequest request,
        CancellationToken cancellationToken = default);
}
