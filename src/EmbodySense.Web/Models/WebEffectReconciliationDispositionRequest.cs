using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Web.Models;

/// <summary>Carries one legal reconciliation disposition over an exact optimistic case reference.</summary>
/// <param name="Case">The exact redacted immutable case reference.</param>
/// <param name="OperationId">The stable idempotency identity.</param>
/// <param name="DispositionKind">The legal disposition selected by the server-owned route.</param>
/// <param name="SafeDetail">Optional bounded operator context that is never treated as evidence.</param>
public sealed record WebEffectReconciliationDispositionRequest(WebEffectReconciliationCaseReference? Case, string? OperationId, GovernedLoopEffectReconciliationDispositionKind DispositionKind, string? SafeDetail);
