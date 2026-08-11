using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage;

/// <summary>Atomically checks and reserves non-renewable usage of one exact governed-loop authority grant.</summary>
public interface IGovernedLoopEffectAuthorityUsageStore
{
    /// <summary>Checks completion posture and reserves a distinct target or first-run completion claim when required.</summary>
    /// <param name="request">The exact grant, run, attempt, boundary, target, and trusted-time coordinates.</param>
    /// <param name="cancellationToken">The token used while reading or durably advancing usage evidence.</param>
    /// <returns>The closed usage posture. Only allowed or newly/exactly reserved results permit effect evaluation to continue.</returns>
    Task<GovernedLoopEffectAuthorityUsageStoreResult> ReserveAsync(GovernedLoopEffectAuthorityUsageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Durably reserves the exact first-bound-run completion before its terminal callback starts.</summary>
    /// <param name="request">The exact conversation-publication usage request.</param>
    /// <param name="cancellationToken">The token used while durably reserving completion intent.</param>
    /// <returns>A newly pending result only when the terminal callback may start.</returns>
    Task<GovernedLoopEffectAuthorityUsageStoreResult> BeginCompletionAsync(GovernedLoopEffectAuthorityCompletionUsageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Durably completes a previously reserved first-bound-run completion after its terminal callback succeeds.</summary>
    /// <param name="request">The exact conversation-publication usage request whose callback completed.</param>
    /// <param name="cancellationToken">The token used while durably committing completion.</param>
    /// <returns>A newly completed result only when the grant is durably ineffective.</returns>
    Task<GovernedLoopEffectAuthorityUsageStoreResult> CompleteCompletionAsync(GovernedLoopEffectAuthorityCompletionUsageRequest request, CancellationToken cancellationToken = default);
}
