using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests an atomic durable reservation before a registered probe callback is entered.</summary>
/// <param name="OperationId">The independent probe operation identity.</param>
/// <param name="RequestHash">The hash of every exact retained probe identity and immutable input fingerprint.</param>
/// <param name="Invocation">The server-composed exact callback context.</param>
public sealed record GovernedLoopEffectReconciliationProbeReservationRequest(
    string OperationId,
    string RequestHash,
    GovernedLoopEffectReconciliationProbeInvocationRequest Invocation)
{
    /// <summary>Gets the independent bounded probe operation identity.</summary>
    public string OperationId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(OperationId, nameof(OperationId));

    /// <summary>Gets the canonical complete request hash.</summary>
    public string RequestHash { get; } = GovernedLoopEffectReconciliationModelGuard.RequireSha256(RequestHash, nameof(RequestHash));

    /// <summary>Gets the detached exact callback context.</summary>
    public GovernedLoopEffectReconciliationProbeInvocationRequest Invocation { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredProbeInvocation(Invocation, nameof(Invocation));
}
