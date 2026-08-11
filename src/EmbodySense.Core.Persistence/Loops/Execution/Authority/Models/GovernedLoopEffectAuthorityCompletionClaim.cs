using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;

internal sealed record GovernedLoopEffectAuthorityCompletionClaim(
    int SchemaVersion,
    AuthorityGrantReference Grant,
    string AdmissionReceiptHash,
    string RunId,
    long ExecutionGeneration,
    string CompletionOperationId,
    GovernedLoopEffectAuthorityCompletionClaimStatus Status,
    DateTimeOffset RecordedAtUtc)
{
    internal const int CurrentSchemaVersion = 1;
}
