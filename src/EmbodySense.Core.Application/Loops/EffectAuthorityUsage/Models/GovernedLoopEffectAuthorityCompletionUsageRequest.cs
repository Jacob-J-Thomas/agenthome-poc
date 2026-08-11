using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

/// <summary>Coordinates one exact first-bound-run completion mutation in the durable usage ledger.</summary>
/// <param name="SchemaVersion">The persisted contract schema version.</param>
/// <param name="Grant">The exact immutable admitted grant revision and content hash.</param>
/// <param name="AdmissionReceiptHash">The canonical immutable admission receipt hash.</param>
/// <param name="RunId">The exact bound run identity.</param>
/// <param name="ExecutionGeneration">The current positive frontier generation retained as evidence.</param>
/// <param name="CompletionOperationId">The stable idempotency identity shared by every completion retry for this run.</param>
/// <param name="EvaluatedAtUtc">The trusted exact UTC instant at which this completion phase was evaluated.</param>
public sealed record GovernedLoopEffectAuthorityCompletionUsageRequest(
    int SchemaVersion,
    AuthorityGrantReference Grant,
    string AdmissionReceiptHash,
    string RunId,
    long ExecutionGeneration,
    string CompletionOperationId,
    DateTimeOffset EvaluatedAtUtc)
{
    /// <summary>Gets the only supported POC schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
