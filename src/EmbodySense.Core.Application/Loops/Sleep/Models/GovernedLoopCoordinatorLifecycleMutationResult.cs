namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one fenced lifecycle mutation.</summary>
/// <param name="Status">The closed lifecycle outcome.</param>
/// <param name="Snapshot">The committed or conflicting current evidence when safely readable.</param>
public sealed record GovernedLoopCoordinatorLifecycleMutationResult(
    GovernedLoopCoordinatorLifecycleMutationStatus Status,
    GovernedLoopCoordinatorSnapshot? Snapshot = null)
{
    /// <summary>Gets a detached current evidence snapshot when safely readable.</summary>
    public GovernedLoopCoordinatorSnapshot? Snapshot { get; } = GovernedLoopCoordinatorEvidenceCopy.Snapshot(Snapshot);
}
