using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.E2ETests.Web;

internal sealed record HumanReviewResponseLossOfflineProbeResult(
    string RunReadStatus,
    CustomLoopRunRecord? Run,
    HumanReviewContinuationAuthorityReadStatus? AuthorityStatus,
    AuthorityGrantResolutionStatus? GrantStatus,
    CapabilityRevalidationStatus? CapabilityStatus,
    HumanReviewCurrentEffectAttemptEvidenceReadStatus? EvidenceStatus,
    GovernedLoopEffectCertaintySnapshotStatus? CertaintyStatus,
    GovernedLoopEffectAttemptReadStatus? AttemptStatus,
    GovernedLoopEffectPhase? EffectPhase,
    string? RunError,
    string? AuthorityError,
    string? EvidenceError,
    string? CertaintyError,
    string? AttemptError)
{
    public string Describe()
        => $"run={RunReadStatus}; authority={AuthorityStatus?.ToString() ?? "not-read"}; grant={GrantStatus?.ToString() ?? "not-read"}; capability={CapabilityStatus?.ToString() ?? "not-read"}; evidence={EvidenceStatus?.ToString() ?? "not-read"}; certainty={CertaintyStatus?.ToString() ?? "not-read"}; attempt={AttemptStatus?.ToString() ?? "not-read"}; phase={EffectPhase?.ToString() ?? "not-read"}; run-error={RunError ?? "none"}; authority-error={AuthorityError ?? "none"}; evidence-error={EvidenceError ?? "none"}; certainty-error={CertaintyError ?? "none"}; attempt-error={AttemptError ?? "none"}";
}
