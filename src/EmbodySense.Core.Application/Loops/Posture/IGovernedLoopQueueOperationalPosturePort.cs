using EmbodySense.Core.Application.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Reads bounded authoritative queue and worker-lease evidence without selecting work.</summary>
public interface IGovernedLoopQueueOperationalPosturePort
{
    /// <summary>Reads one stable page at the supplied trusted observation instant.</summary>
    Task<GovernedLoopQueueEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}
