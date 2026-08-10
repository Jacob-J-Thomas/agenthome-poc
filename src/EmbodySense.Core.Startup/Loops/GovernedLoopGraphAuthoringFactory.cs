using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>Composes canonical governed-loop graph authoring over one workspace authority boundary.</summary>
public static class GovernedLoopGraphAuthoringFactory
{
    /// <summary>Creates workspace-bound graph authoring with the production server-owned trust root.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="nodeCatalog">The current exact executable-node catalog.</param>
    /// <param name="authorityProvider">The current role-authority snapshot provider.</param>
    /// <param name="actorAuthorizer">The server-owned lifecycle actor authorizer.</param>
    /// <param name="timeProvider">The trusted clock, or the system clock when omitted.</param>
    /// <returns>The fully composed surface-neutral graph authoring service.</returns>
    /// <remarks>
    /// Composition is inert until the returned service receives an authoring request. It does not admit or execute a
    /// runtime, publish to a surface, or grant authority.
    /// </remarks>
    public static GovernedLoopGraphAuthoringService Create(
        WorkspacePaths paths,
        IGovernedLoopNodeCatalog nodeCatalog,
        IGovernedLoopAuthoritySnapshotProvider authorityProvider,
        IGovernedLoopRevisionActorAuthorizer actorAuthorizer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Create(
            paths,
            FileCapabilityCatalogTrustProvider.CreateDefault(),
            nodeCatalog,
            authorityProvider,
            actorAuthorizer,
            timeProvider);
    }

    /// <summary>Creates workspace-bound graph authoring over an explicit server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="trustProvider">The server-owned trust provider outside mutable workspace storage.</param>
    /// <param name="nodeCatalog">The current exact executable-node catalog.</param>
    /// <param name="authorityProvider">The current role-authority snapshot provider.</param>
    /// <param name="actorAuthorizer">The server-owned lifecycle actor authorizer.</param>
    /// <param name="timeProvider">The trusted clock, or the system clock when omitted.</param>
    /// <returns>The fully composed surface-neutral graph authoring service.</returns>
    /// <remarks>
    /// One capability-authority transaction and the exact supplied trust provider are shared by the lifecycle and
    /// graph-payload stores. Generic lifecycle persistence remains the sole visibility authority.
    /// </remarks>
    public static GovernedLoopGraphAuthoringService Create(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        IGovernedLoopNodeCatalog nodeCatalog,
        IGovernedLoopAuthoritySnapshotProvider authorityProvider,
        IGovernedLoopRevisionActorAuthorizer actorAuthorizer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(nodeCatalog);
        ArgumentNullException.ThrowIfNull(authorityProvider);
        ArgumentNullException.ThrowIfNull(actorAuthorizer);

        var authorityTransaction = new CapabilityAuthorityTransaction(paths);
        var lifecycleStore = new GovernedLoopRevisionLifecycleStore(
            paths,
            trustProvider,
            authorityTransaction: authorityTransaction);
        var revisionStore = new GovernedLoopGraphRevisionStore(
            paths,
            lifecycleStore,
            trustProvider,
            authorityTransaction: authorityTransaction);
        return Create(
            revisionStore,
            nodeCatalog,
            authorityProvider,
            actorAuthorizer,
            authorityTransaction,
            timeProvider);
    }

    /// <summary>Creates the graph authoring service without reading or mutating workspace state.</summary>
    /// <param name="revisionStore">The trust-backed graph and lifecycle store.</param>
    /// <param name="nodeCatalog">The current exact executable-node catalog.</param>
    /// <param name="authorityProvider">The current role-authority snapshot provider.</param>
    /// <param name="actorAuthorizer">The server-owned lifecycle actor authorizer.</param>
    /// <param name="authorityTransaction">The exact workspace authority transaction shared with the revision store.</param>
    /// <param name="timeProvider">The trusted clock, or the system clock when omitted.</param>
    /// <returns>The fully composed surface-neutral graph authoring service.</returns>
    /// <remarks>
    /// The supplied store remains the sole persistence boundary. It and <paramref name="authorityTransaction" />
    /// must belong to the same physical workspace. The returned service creates no Web or runtime semantics and grants no authority.
    /// </remarks>
    public static GovernedLoopGraphAuthoringService Create(
        IGovernedLoopGraphRevisionStore revisionStore,
        IGovernedLoopNodeCatalog nodeCatalog,
        IGovernedLoopAuthoritySnapshotProvider authorityProvider,
        IGovernedLoopRevisionActorAuthorizer actorAuthorizer,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(revisionStore);
        ArgumentNullException.ThrowIfNull(nodeCatalog);
        ArgumentNullException.ThrowIfNull(authorityProvider);
        ArgumentNullException.ThrowIfNull(actorAuthorizer);
        ArgumentNullException.ThrowIfNull(authorityTransaction);

        var validationService = new GovernedLoopGraphValidationService(nodeCatalog, authorityProvider);
        return new GovernedLoopGraphAuthoringService(
            revisionStore,
            validationService,
            actorAuthorizer,
            authorityTransaction,
            timeProvider);
    }
}
