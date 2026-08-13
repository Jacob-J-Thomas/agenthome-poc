namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one fenced failure mutation.</summary>
/// <param name="Status">The closed failure outcome.</param>
/// <param name="Snapshot">The committed or conflicting current evidence when safely readable.</param>
public sealed record GovernedLoopCoordinatorFailureMutationResult(
    GovernedLoopCoordinatorFailureMutationStatus Status,
    GovernedLoopCoordinatorSnapshot? Snapshot = null)
{
    /// <summary>Gets a detached current evidence snapshot when safely readable.</summary>
    public GovernedLoopCoordinatorSnapshot? Snapshot { get; } = GovernedLoopCoordinatorEvidenceCopy.Snapshot(Snapshot);
}
