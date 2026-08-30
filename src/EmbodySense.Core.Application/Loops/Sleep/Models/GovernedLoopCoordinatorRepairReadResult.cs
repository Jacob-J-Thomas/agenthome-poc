using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Returns the latest retained repair disposition for one exact failed ownership generation.</summary>
public sealed record GovernedLoopCoordinatorRepairReadResult(
    GovernedLoopCoordinatorRepairReadStatus Status,
    GovernedLoopCoordinatorRepairDisposition? Disposition = null)
{
    /// <summary>Gets the immutable retained disposition when found.</summary>
    public GovernedLoopCoordinatorRepairDisposition? Disposition { get; } = Disposition is null ? null : Disposition with { };
}
