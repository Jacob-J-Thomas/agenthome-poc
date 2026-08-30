using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Returns an exact current repair disposition preview and its closed non-mutating eligibility result.</summary>
public sealed record GovernedLoopCoordinatorRepairPreview(
    GovernedLoopCoordinatorRepairPreviewStatus Status,
    string OperationId,
    GovernedLoopCoordinatorRepairDisposition? Disposition,
    string ReasonCode)
{
    /// <summary>Gets the immutable submit binding when preview eligibility was established.</summary>
    public GovernedLoopCoordinatorRepairDisposition? Disposition { get; } = Disposition is null ? null : Disposition with { };
}
