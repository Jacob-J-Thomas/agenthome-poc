using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one coordinator-ledger repair-disposition compare-and-swap outcome.</summary>
public sealed record GovernedLoopCoordinatorRepairMutationResult(
    GovernedLoopCoordinatorRepairMutationStatus Status,
    GovernedLoopCoordinatorRepairDisposition? Disposition = null)
{
    /// <summary>Gets the retained immutable disposition when safely available.</summary>
    public GovernedLoopCoordinatorRepairDisposition? Disposition { get; } = Disposition is null ? null : Disposition with { };
}
