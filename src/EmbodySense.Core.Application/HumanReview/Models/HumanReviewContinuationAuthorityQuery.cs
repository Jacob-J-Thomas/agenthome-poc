using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies the exact admitted and reviewed evidence that a current-authority source must independently revalidate before release.</summary>
/// <param name="Binding">The immutable Human Review binding whose authority, capability, target, precondition, and payload hashes must still match.</param>
/// <param name="AdapterBinding">The complete immutable admission receipt and exact execution binding retained by the run.</param>
/// <param name="GraphArtifact">The immutable graph artifact that must exactly match the admitted revision and layout.</param>
public sealed record HumanReviewContinuationAuthorityQuery(
    HumanReviewBinding Binding,
    GovernedLoopSequentialAdapterBinding AdapterBinding,
    GovernedLoopGraphRevisionArtifact GraphArtifact);
