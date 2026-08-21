using EmbodySense.Core.Application.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Reads bounded authoritative durable-run evidence without changing lifecycle.</summary>
public interface IGovernedLoopRunOperationalPosturePort
{
    /// <summary>Reads one stable run page ordered by identity.</summary>
    Task<GovernedLoopRunEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        CancellationToken cancellationToken = default);
}
