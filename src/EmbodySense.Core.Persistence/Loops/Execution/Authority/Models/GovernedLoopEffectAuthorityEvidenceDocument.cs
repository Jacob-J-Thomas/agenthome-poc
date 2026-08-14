using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;

internal sealed record GovernedLoopEffectAuthorityEvidenceDocument(
    int SchemaVersion,
    string WorkspaceIdentity,
    long Generation,
    IReadOnlyList<GovernedLoopEffectAuthorityDecision> Decisions,
    IReadOnlyList<GovernedLoopEffectAuthorityTargetReservation> TargetReservations,
    IReadOnlyList<GovernedLoopEffectAuthorityCompletionClaim> CompletionClaims,
    string ContentDigest,
    string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
