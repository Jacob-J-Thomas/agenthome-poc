namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Requests one current non-mutating preview for a caller-held coordinator-repair operation identity.</summary>
public sealed record GovernedLoopCoordinatorRepairPreviewRequest(string CoordinatorId, string OperationId);
