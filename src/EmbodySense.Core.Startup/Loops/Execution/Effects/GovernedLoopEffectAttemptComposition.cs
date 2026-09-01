using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.EffectAttempts.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Owns one workspace-scoped effect-attempt store and its canonical Human Review evidence projection.</summary>
/// <remarks>
/// A runtime creates this composition once and asks it for one facade per registered operation registry. The facades
/// have independent catalog and authority resolvers, but all effect execution and Human Review evidence reads use the
/// same durable attempt store and the same read-only evidence source. This prevents a release decision from being
/// evaluated against a second attempt ledger.
/// </remarks>
internal sealed class GovernedLoopEffectAttemptComposition
{
    private readonly GovernedLoopEffectAttemptStore _attemptStorage;
    private readonly CanonicalHumanReviewEffectEvidenceSource _humanReviewEvidence;

    private GovernedLoopEffectAttemptComposition(
        GovernedLoopEffectAttemptStore attemptStore,
        CanonicalHumanReviewEffectEvidenceSource humanReviewEvidence)
    {
        _attemptStorage = attemptStore;
        AttemptStore = attemptStore;
        AttemptReadStore = attemptStore;
        _humanReviewEvidence = humanReviewEvidence;
    }

    /// <summary>Gets the single mutable effect-attempt port shared by all facades from this composition.</summary>
    /// <remarks>The port is server-owned and must not be replaced by a surface-specific or request-scoped store.</remarks>
    internal IGovernedLoopEffectAttemptStore AttemptStore { get; }

    /// <summary>Gets the read-only current-attempt port used by the canonical Human Review evidence projection.</summary>
    internal IGovernedLoopEffectAttemptReadStore AttemptReadStore { get; }

    /// <summary>Gets the canonical Human Review current-attempt evidence projection.</summary>
    internal IHumanReviewCurrentEffectAttemptEvidenceSource HumanReviewEffectEvidence => _humanReviewEvidence;

    /// <summary>Gets the canonical Human Review pre-dispatch release-evidence projection.</summary>
    internal IHumanReviewPreDispatchEffectReleaseEvidenceSource HumanReviewReleaseEvidence => _humanReviewEvidence;

    /// <summary>Gets the canonical effect certainty projection used to distinguish safe and ambiguous release.</summary>
    internal IGovernedLoopEffectCertaintySnapshotSource HumanReviewEffectCertainty => _humanReviewEvidence;

    /// <summary>Creates one shared-store composition for a canonical workspace and run store.</summary>
    /// <param name="paths">The server-owned workspace paths used by the durable attempt store.</param>
    /// <param name="runStore">The canonical custom-loop run store that retains reviewed effect bindings.</param>
    /// <param name="options">Optional bounded attempt-store limits.</param>
    /// <returns>A composition whose facades share one effect ledger and evidence source.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required dependency is missing.</exception>
    internal static GovernedLoopEffectAttemptComposition Create(
        WorkspacePaths paths,
        ICustomLoopRunStore runStore,
        GovernedLoopEffectAttemptStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(runStore);

        var attempts = new GovernedLoopEffectAttemptStore(paths, options);
        return new GovernedLoopEffectAttemptComposition(
            attempts,
            new CanonicalHumanReviewEffectEvidenceSource(runStore, attempts));
    }

    /// <summary>Creates a surface-neutral effect facade over this composition's shared attempt and evidence ports.</summary>
    /// <param name="catalogStore">The caller-owned current capability catalog store.</param>
    /// <param name="registry">The operation registry available to this facade.</param>
    /// <param name="authorityBoundary">The server-owned authority boundary for effect execution.</param>
    /// <param name="hostContractVersion">The host capability contract version.</param>
    /// <param name="hostPlatform">The host platform pin.</param>
    /// <param name="timeProvider">The trusted clock used by effect orchestration.</param>
    /// <returns>A facade with its own catalog resolver and a shared durable attempt protocol.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required dependency is missing.</exception>
    internal GovernedLoopEffectAttemptFacade CreateFacade(
        ICapabilityCatalogStore catalogStore,
        IGovernedActuatorOperationRegistry registry,
        IGovernedLoopEffectAuthorityDecisionBoundary authorityBoundary,
        CapabilityVersion hostContractVersion,
        CapabilityPlatform hostPlatform,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalogStore);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(authorityBoundary);
        ArgumentNullException.ThrowIfNull(hostContractVersion);
        ArgumentNullException.ThrowIfNull(hostPlatform);

        var resolver = new GovernedActuatorCatalogResolver(catalogStore, registry, hostContractVersion, hostPlatform);
        var service = new GovernedLoopEffectAttemptService(
            resolver,
            AttemptStore,
            authorityBoundary,
            HumanReviewReleaseEvidence,
            timeProvider);
        return new GovernedLoopEffectAttemptFacade(resolver, service);
    }

    /// <summary>Reads the sole effect-attempt ledger without creating, claiming, resuming, or dispatching an effect.</summary>
    /// <remarks>
    /// Missing evidence is healthy for an empty workspace. Any other closed result is treated as unavailable or corrupt so
    /// Human Review cannot advertise executability before its canonical evidence source is readable.
    /// </remarks>
    internal async Task<bool> IsHumanReviewEvidenceStorageHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _attemptStorage.ProbeStorageAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
