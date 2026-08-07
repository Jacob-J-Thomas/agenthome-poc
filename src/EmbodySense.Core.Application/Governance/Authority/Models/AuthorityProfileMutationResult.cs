namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Returns one durable authority-profile mutation result.</summary>
/// <param name="Status">The structured mutation outcome.</param>
/// <param name="OperationId">The request idempotency identity.</param>
/// <param name="Record">The resulting or replayed profile record when known.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record AuthorityProfileMutationResult(AuthorityProfileMutationStatus Status, string OperationId, AuthorityProfileRecord? Record, string Detail);
