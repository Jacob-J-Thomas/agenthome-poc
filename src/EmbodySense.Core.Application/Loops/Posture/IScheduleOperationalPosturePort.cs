using EmbodySense.Core.Application.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Reads the complete bounded authoritative schedule catalog for read-only posture projection.</summary>
public interface IScheduleOperationalPosturePort
{
    /// <summary>Reads one deterministic finite schedule page without evaluating or mutating a schedule.</summary>
    Task<GovernedLoopScheduleEvidenceReadResult> ReadAsync(
        GovernedLoopOperationalEvidencePageRequest request,
        CancellationToken cancellationToken = default);
}
