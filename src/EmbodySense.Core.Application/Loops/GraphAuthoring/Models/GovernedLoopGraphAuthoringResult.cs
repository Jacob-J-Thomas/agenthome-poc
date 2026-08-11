using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Returns bounded exact evidence for one immutable graph authoring operation.</summary>
public sealed record GovernedLoopGraphAuthoringResult(
    GovernedLoopGraphAuthoringStatus Status,
    string OperationId,
    string AuthoringRequestHash,
    GovernedLoopRevisionLifecycleMutationResult? LifecycleResult,
    string? GraphValidationEvidenceHash,
    GovernedLoopGraphRevisionChangeKind ChangeKind,
    GovernedLoopGraphRevisionIdentity? RevisionIdentity,
    IReadOnlyList<GovernedLoopGraphValidationError> GraphValidationErrors,
    IReadOnlyList<GovernedLoopRevisionLifecycleValidationError> LifecycleValidationErrors);
