using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns one immutable governed-loop revision aggregate at a consistent global store generation.</summary>
/// <param name="Head">The exact lifecycle head.</param>
/// <param name="Artifacts">All retained immutable artifacts for the graph.</param>
/// <param name="Operations">Every retained append-only lifecycle operation, including historical publication evidence.</param>
public sealed record GovernedLoopRevisionStoreSnapshot(
    GovernedLoopRevisionLifecycleHead Head,
    IReadOnlyList<GovernedLoopRevisionArtifact> Artifacts,
    IReadOnlyList<GovernedLoopRevisionOperationEvidence> Operations);
