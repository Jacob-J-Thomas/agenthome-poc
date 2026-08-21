using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Reads one bounded aggregate from the authoritative operational plane.</summary>
public interface IGovernedLoopOperationalPostureReader
{
    /// <summary>Reads one finite, fail-closed operational posture.</summary>
    Task<GovernedLoopOperationalPostureResult> ReadAsync(
        GovernedLoopOperationalPostureQuery query,
        CancellationToken cancellationToken = default);
}
