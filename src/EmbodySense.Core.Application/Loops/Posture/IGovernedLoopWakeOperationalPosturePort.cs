using EmbodySense.Core.Application.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Reads the complete bounded authoritative sleeping-checkpoint catalog for read-only posture projection.</summary>
public interface IGovernedLoopWakeOperationalPosturePort
{
    /// <summary>Reads one deterministic finite checkpoint page without claiming or continuing a checkpoint.</summary>
    Task<GovernedLoopWakeCatalogEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        CancellationToken cancellationToken = default);
}
