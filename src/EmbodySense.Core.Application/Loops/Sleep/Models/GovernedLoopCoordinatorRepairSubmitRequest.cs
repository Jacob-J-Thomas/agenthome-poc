using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Submits one exact previously previewed immutable coordinator repair disposition.</summary>
public sealed record GovernedLoopCoordinatorRepairSubmitRequest(GovernedLoopCoordinatorRepairDisposition Disposition)
{
    /// <summary>Gets the immutable submitted preview binding.</summary>
    public GovernedLoopCoordinatorRepairDisposition Disposition { get; } = Disposition with { };
}
