namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one coordinator-evidence read.</summary>
/// <param name="Status">The closed read outcome.</param>
/// <param name="Snapshot">The validated current evidence, present exactly when found.</param>
public sealed record GovernedLoopCoordinatorReadResult(
    GovernedLoopCoordinatorReadStatus Status,
    GovernedLoopCoordinatorSnapshot? Snapshot = null)
{
    /// <summary>Gets a detached current evidence snapshot when found.</summary>
    public GovernedLoopCoordinatorSnapshot? Snapshot { get; } = GovernedLoopCoordinatorEvidenceCopy.Snapshot(Snapshot);
}
