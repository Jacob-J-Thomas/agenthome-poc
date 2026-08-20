using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Names the canonical immutable topology planner while preserving the established sequential runtime projection.</summary>
public static class GovernedLoopTopologyPlanBuilder
{
    /// <summary>Builds one exact immutable topology plan or fails closed with a value-free graph path.</summary>
    /// <param name="artifact">The exact immutable graph revision artifact.</param>
    /// <returns>The shared plan-build result consumed by linear and graph-aware runtimes.</returns>
    public static GovernedLoopSequentialPlanBuildResult Build(GovernedLoopGraphRevisionArtifact? artifact)
        => GovernedLoopSequentialPlanBuilder.Build(artifact);
}
