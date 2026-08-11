using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Returns lifecycle state and every retained immutable graph payload observed at one generation.</summary>
public sealed record GovernedLoopGraphRevisionSnapshot(
    GovernedLoopRevisionStoreSnapshot Lifecycle,
    IReadOnlyList<GovernedLoopGraphRevisionArtifact> Artifacts);
