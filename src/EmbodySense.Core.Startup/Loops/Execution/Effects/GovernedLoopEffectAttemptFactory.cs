using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Composes the canonical effect protocol over current capability truth and crash-safe workspace evidence.</summary>
public static class GovernedLoopEffectAttemptFactory
{
    /// <summary>Creates production workspace composition under one shared capability-authority transaction.</summary>
    public static GovernedLoopEffectAttemptFacade Create(
        WorkspacePaths paths,
        ICustomLoopRunStore runStore,
        ICapabilityCatalogTrustProvider trustProvider,
        ICapabilityAuthorityTransaction authorityTransaction,
        IGovernedActuatorOperationRegistry registry,
        IGovernedLoopEffectAuthorityDecisionBoundary authorityBoundary,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(authorityTransaction);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(authorityBoundary);
        if (!ReferenceEquals(authorityBoundary.AuthorityTransaction, authorityTransaction))
        {
            throw new ArgumentException("The effect authority boundary must use the same workspace authority transaction as the catalog and lifecycle stores.", nameof(authorityBoundary));
        }

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        var catalog = new CapabilityCatalogStore(
            paths,
            trustProvider,
            timeProvider,
            authorityTransaction: authorityTransaction);
        var lifecycle = new CapabilityLifecycleMutationStore(
            paths,
            trustProvider,
            timeProvider,
            authorityTransaction: authorityTransaction);
        var currentCatalog = new CapabilityLifecycleCatalogStore(catalog, lifecycle, authorityTransaction);
        return Create(
            paths,
            runStore,
            currentCatalog,
            registry,
            authorityBoundary,
            CapabilityHostRuntime.HostContractVersion,
            CapabilityHostRuntime.Platform,
            timeProvider);
    }

    /// <summary>Creates inert composition over caller-owned catalog, registry, and authority ports.</summary>
    public static GovernedLoopEffectAttemptFacade Create(
        WorkspacePaths paths,
        ICustomLoopRunStore runStore,
        ICapabilityCatalogStore catalogStore,
        IGovernedActuatorOperationRegistry registry,
        IGovernedLoopEffectAuthorityDecisionBoundary authorityBoundary,
        CapabilityVersion hostContractVersion,
        CapabilityPlatform hostPlatform,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(catalogStore);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(authorityBoundary);
        ArgumentNullException.ThrowIfNull(hostContractVersion);
        ArgumentNullException.ThrowIfNull(hostPlatform);

        return GovernedLoopEffectAttemptComposition.Create(paths, runStore)
            .CreateFacade(
                catalogStore,
                registry,
                authorityBoundary,
                hostContractVersion,
                hostPlatform,
                timeProvider);
    }
}
