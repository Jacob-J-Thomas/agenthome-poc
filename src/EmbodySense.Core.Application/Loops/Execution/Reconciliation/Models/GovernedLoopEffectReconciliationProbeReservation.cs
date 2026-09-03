using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Retains immutable probe intent and the exact case/effect/source identities reserved before callback.</summary>
/// <param name="OperationId">The independent probe operation identity.</param>
/// <param name="RequestHash">The canonical reserved intent hash.</param>
/// <param name="ProbeInvocationId">The server-generated opaque callback identity, independent of the original operation identity.</param>
/// <param name="Context">The trusted exact reservation context.</param>
/// <param name="ReservedAtUtc">The trusted reservation time.</param>
public sealed record GovernedLoopEffectReconciliationProbeReservation(
    string OperationId,
    string RequestHash,
    string ProbeInvocationId,
    GovernedLoopEffectReconciliationProbeReservationContext Context,
    DateTimeOffset ReservedAtUtc)
{
    /// <summary>Gets the independent operation identity.</summary>
    public string OperationId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(OperationId, nameof(OperationId));
    /// <summary>Gets the canonical intent hash.</summary>
    public string RequestHash { get; } = GovernedLoopEffectReconciliationModelGuard.RequireSha256(RequestHash, nameof(RequestHash));
    /// <summary>Gets the server-generated opaque callback identity.</summary>
    public string ProbeInvocationId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(ProbeInvocationId, nameof(ProbeInvocationId));
    /// <summary>Gets the trusted exact reservation context.</summary>
    public GovernedLoopEffectReconciliationProbeReservationContext Context { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredProbeContext(Context, nameof(Context));
    /// <summary>Gets the trusted UTC reservation time.</summary>
    public DateTimeOffset ReservedAtUtc { get; } = GovernedLoopEffectReconciliationModelGuard.RequireUtc(ReservedAtUtc, nameof(ReservedAtUtc));
}
