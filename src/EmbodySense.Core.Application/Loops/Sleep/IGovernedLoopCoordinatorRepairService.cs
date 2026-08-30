using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Previews and submits authority-bound append-only repairs for failed local coordinators.</summary>
public interface IGovernedLoopCoordinatorRepairService
{
    /// <summary>Builds a current exact repair preview without mutating coordinator history or starting work.</summary>
    Task<GovernedLoopCoordinatorRepairPreview> PreviewAsync(
        GovernedLoopCoordinatorRepairPreviewRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Appends or exactly replays one previously previewed repair disposition without starting work itself.</summary>
    Task<GovernedLoopCoordinatorRepairSubmitResult> SubmitAsync(
        GovernedLoopCoordinatorRepairSubmitRequest request,
        CancellationToken cancellationToken = default);
}
