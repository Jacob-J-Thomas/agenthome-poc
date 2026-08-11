namespace EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;

internal sealed record GovernedLoopEffectAuthorityEvidenceStoreLoadResult(
    GovernedLoopEffectAuthorityEvidenceDocument? Document,
    GovernedLoopEffectAuthorityEvidenceDocument? Pending,
    GovernedLoopEffectAuthorityEvidenceStoreLoadDisposition Disposition);
