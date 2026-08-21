using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Carries one validated run summary plus exact canonical revision and artifact evidence.</summary>
public sealed record GovernedLoopRunEvidenceSnapshot(
    CustomLoopRunSummary Summary,
    string? GraphId,
    string? RevisionId,
    string EvidenceHash);
