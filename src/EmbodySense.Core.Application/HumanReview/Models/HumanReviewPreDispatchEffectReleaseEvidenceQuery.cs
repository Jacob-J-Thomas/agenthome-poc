using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies only expectations for a server-owned current pre-dispatch release-evidence read.</summary>
/// <param name="WorkspaceId">The exact admitted workspace expected to own the canonical run and effect attempt.</param>
/// <param name="AdmissionReceiptHash">The exact canonical admission receipt expected by both the run and effect attempt.</param>
/// <param name="Release">The caller-supplied release envelope, which is never authority by itself.</param>
/// <param name="Attempt">The exact retained effect attempt already read under the effect-attempt protocol.</param>
public sealed record HumanReviewPreDispatchEffectReleaseEvidenceQuery(
    string WorkspaceId,
    string AdmissionReceiptHash,
    HumanReviewPreDispatchEffectRelease Release,
    GovernedLoopEffectAttempt Attempt);
