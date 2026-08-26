using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

/// <summary>Binds one Human Input checkpoint to the exact admitted run, published graph revision, frontier activation, node visit, and generation.</summary>
/// <param name="SchemaVersion">The binding schema version, which must be 1.</param>
/// <param name="WorkspaceId">The exact canonical workspace scope.</param>
/// <param name="Execution">The exact run, immutable graph revision, and execution generation.</param>
/// <param name="Publication">The exact publication pin for that immutable graph revision.</param>
/// <param name="GraphArtifactHash">The exact immutable graph-artifact hash.</param>
/// <param name="GraphLayoutHash">The exact immutable graph-layout hash.</param>
/// <param name="AdmissionReceiptHash">The exact successful run-admission receipt hash.</param>
/// <param name="FrontierVersion">The exact optimistic frontier version.</param>
/// <param name="FrontierHash">The exact canonical frontier hash.</param>
/// <param name="ActivationOrdinal">The exact zero-based activation ordinal.</param>
/// <param name="CycleId">The optional exact cycle identity.</param>
/// <param name="CycleIteration">The optional positive cycle iteration, present exactly with <paramref name="CycleId"/>.</param>
/// <param name="NodeId">The exact graph node identity.</param>
/// <param name="NodeVisitOrdinal">The exact positive visit ordinal for <paramref name="NodeId"/>.</param>
/// <param name="CheckpointId">The stable Human Input checkpoint identity bound into the request.</param>
public sealed record GovernedLoopHumanInputWaitingCheckpointBinding(
    int SchemaVersion,
    string WorkspaceId,
    GovernedLoopExecutionBinding Execution,
    GovernedLoopRevisionPublicationPin Publication,
    string GraphArtifactHash,
    string GraphLayoutHash,
    string AdmissionReceiptHash,
    long FrontierVersion,
    string FrontierHash,
    int ActivationOrdinal,
    string? CycleId,
    int? CycleIteration,
    string NodeId,
    int NodeVisitOrdinal,
    string CheckpointId);
