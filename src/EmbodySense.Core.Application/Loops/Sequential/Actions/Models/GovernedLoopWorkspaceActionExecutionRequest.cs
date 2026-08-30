using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

/// <summary>Requests one exact admitted workspace Action execution or recovery.</summary>
/// <param name="Dispatch">The exact guarded canonical node-dispatch coordinates.</param>
/// <param name="GraphArtifact">The immutable graph revision containing the Action semantic input.</param>
/// <param name="AttemptOperationId">The exact durable frontier attempt operation identity.</param>
/// <param name="InputJson">The exact canonical semantic input retained in the immutable graph node.</param>
/// <param name="HumanReviewRelease">The optional durable pre-dispatch review release proof for the exact Action effect.</param>
public sealed record GovernedLoopWorkspaceActionExecutionRequest(
    GovernedLoopSequentialNodeDispatchRequest Dispatch,
    GovernedLoopGraphRevisionArtifact GraphArtifact,
    string AttemptOperationId,
    string InputJson,
    HumanReviewPreDispatchEffectRelease? HumanReviewRelease = null);
