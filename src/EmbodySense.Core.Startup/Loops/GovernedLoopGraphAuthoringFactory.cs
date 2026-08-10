using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Revisions;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>Composes canonical governed-loop graph authoring over one workspace authority boundary.</summary>
public static class GovernedLoopGraphAuthoringFactory
{
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
