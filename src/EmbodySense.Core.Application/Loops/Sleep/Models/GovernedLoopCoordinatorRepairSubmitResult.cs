using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one authority-checked append-only coordinator repair submission.</summary>
public sealed record GovernedLoopCoordinatorRepairSubmitResult(
    GovernedLoopCoordinatorRepairSubmitStatus Status,
    string OperationId,
    GovernedLoopCoordinatorRepairDisposition? Disposition,
    string ReasonCode)
{
    /// <summary>Gets the retained immutable disposition when safely available.</summary>
    public GovernedLoopCoordinatorRepairDisposition? Disposition { get; } = Disposition is null ? null : Disposition with { };
}
