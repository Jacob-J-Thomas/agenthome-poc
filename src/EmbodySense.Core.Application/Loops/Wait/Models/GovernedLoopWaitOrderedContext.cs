using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Wait.Models;

/// <summary>Contains only the immutable canonical execution inputs reconstructed for Wait continuation.</summary>
public sealed record GovernedLoopWaitOrderedContext(
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopGraphRevisionArtifact Artifact);
