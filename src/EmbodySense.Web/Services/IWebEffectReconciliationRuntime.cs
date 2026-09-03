using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Web.Services;

/// <summary>Exposes authenticated Web access to the one retained effect-reconciliation runtime facade.</summary>
/// <remarks>
/// Implementations borrow the canonical Startup runtime and must bracket every operation with its custom-runtime
/// lifetime gate. This interface exposes no case-opening or resolution-publishing authority to the Web surface.
/// </remarks>
public interface IWebEffectReconciliationRuntime
{
    /// <summary>Gets whether the server-owned workspace is initialized.</summary>
    bool IsWorkspaceInitialized { get; }

    /// <summary>Lists one bounded page of detached reconciliation cases.</summary>
    /// <param name="request">The finite page size and opaque continuation cursor.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical read.</param>
    /// <returns>The canonical detached case page.</returns>
    Task<GovernedLoopEffectReconciliationPage> ListAsync(GovernedLoopEffectReconciliationPageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact detached reconciliation case.</summary>
    /// <param name="reference">The immutable case reference and optimistic binding hash.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical read.</param>
    /// <returns>The exact canonical case result.</returns>
    Task<GovernedLoopEffectReconciliationReadResult> ReadAsync(GovernedLoopEffectReconciliationCaseReference reference, CancellationToken cancellationToken = default);

    /// <summary>Lists one bounded page of server-registered read-only probe contracts.</summary>
    /// <param name="request">The finite page size and opaque continuation cursor.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical read.</param>
    /// <returns>The canonical detached probe catalog.</returns>
    Task<GovernedLoopEffectReconciliationProbeCatalogPage> ListProbeContractsAsync(GovernedLoopEffectReconciliationPageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Invokes one registered read-only probe for the exact case reference.</summary>
    /// <param name="operationId">The stable idempotency identity.</param>
    /// <param name="reference">The exact immutable case reference.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical operation.</param>
    /// <returns>The canonical operation result.</returns>
    Task<GovernedLoopEffectReconciliationOperationResult> ProbeAsync(string operationId, GovernedLoopEffectReconciliationCaseReference reference, CancellationToken cancellationToken = default);

    /// <summary>Derives one immutable assessment from the exact current evidence.</summary>
    /// <param name="operationId">The stable idempotency identity.</param>
    /// <param name="reference">The exact immutable case reference.</param>
    /// <param name="safeDetail">Optional bounded operator context, never treated as evidence.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical operation.</param>
    /// <returns>The canonical operation result.</returns>
    Task<GovernedLoopEffectReconciliationOperationResult> AssessAsync(string operationId, GovernedLoopEffectReconciliationCaseReference reference, string? safeDetail = null, CancellationToken cancellationToken = default);

    /// <summary>Applies one legal disposition to the exact current assessment.</summary>
    /// <param name="operationId">The stable idempotency identity.</param>
    /// <param name="reference">The exact immutable case reference.</param>
    /// <param name="kind">The legal disposition selected by the server-owned route.</param>
    /// <param name="safeDetail">Optional bounded operator context, never treated as evidence.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical operation.</param>
    /// <returns>The canonical operation result.</returns>
    Task<GovernedLoopEffectReconciliationOperationResult> ApplyDispositionAsync(string operationId, GovernedLoopEffectReconciliationCaseReference reference, GovernedLoopEffectReconciliationDispositionKind kind, string? safeDetail = null, CancellationToken cancellationToken = default);

    /// <summary>Reads the exact immutable resolution without invoking a resolver.</summary>
    /// <param name="reference">The exact immutable case reference.</param>
    /// <param name="cancellationToken">Cancels runtime acquisition or the canonical read.</param>
    /// <returns>The canonical detached resolution result.</returns>
    Task<GovernedLoopEffectReconciliationResolutionReadResult> ReadResolutionAsync(GovernedLoopEffectReconciliationCaseReference reference, CancellationToken cancellationToken = default);
}
