namespace EmbodySense.Web.Models;

/// <summary>Carries one exact optimistic reconciliation operation without authority or evidence assertions.</summary>
/// <param name="Case">The exact redacted immutable case reference.</param>
/// <param name="OperationId">The stable idempotency identity.</param>
/// <param name="SafeDetail">Optional bounded operator context that is never treated as evidence.</param>
public sealed record WebEffectReconciliationOperationRequest(WebEffectReconciliationCaseReference? Case, string? OperationId, string? SafeDetail);
