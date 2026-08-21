using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Executes typed, authority-bound, idempotent operational controls.</summary>
public interface IGovernedLoopOperationalController
{
    /// <summary>Executes or exactly replays one caller-owned operation.</summary>
    Task<GovernedLoopOperationalControlResult> ExecuteAsync(
        GovernedLoopOperationalControlRequest request,
        CancellationToken cancellationToken = default);
}
