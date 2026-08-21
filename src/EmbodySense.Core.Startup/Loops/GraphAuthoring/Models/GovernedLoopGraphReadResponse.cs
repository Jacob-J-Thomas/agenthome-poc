using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Projects one exact immutable graph aggregate and optimistic lifecycle head.</summary>
/// <param name="Status">The closed lowercase read status.</param>
/// <param name="StoreGeneration">The exact global lifecycle generation when trustworthy.</param>
/// <param name="Lifecycle">The current exact lifecycle head.</param>
/// <param name="Artifacts">Every retained immutable canonical graph artifact.</param>
public sealed record GovernedLoopGraphReadResponse(
    string Status,
    long StoreGeneration,
    GovernedLoopRevisionLifecycleHead? Lifecycle,
    IReadOnlyList<GovernedLoopGraphRevisionArtifact> Artifacts);
