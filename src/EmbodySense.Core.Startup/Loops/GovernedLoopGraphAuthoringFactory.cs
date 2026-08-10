using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>Composes canonical governed-loop graph authoring over one workspace authority boundary.</summary>
public static class GovernedLoopGraphAuthoringFactory
{
    /// <summary>Creates the graph authoring service without reading or mutating workspace state.</summary>
    /// <param name="paths">The initialized workspace paths that own the shared authority boundary.</param>
    /// <param name="revisionStore">The trust-backed graph and lifecycle store bound to the same physical workspace.</param>
    /// <param name="nodeCatalog">The current exact executable-node catalog.</param>
    /// <param name="authorityProvider">The current role-authority snapshot provider.</param>
    /// <param name="actorAuthorizer">The server-owned lifecycle actor authorizer.</param>
    /// <param name="timeProvider">The trusted clock, or the system clock when omitted.</param>
    /// <returns>The fully composed surface-neutral graph authoring service.</returns>
    /// <remarks>
    /// The supplied store remains the sole persistence boundary and must use the same physical workspace as
    /// <paramref name="paths" />. The returned service creates no Web or runtime semantics and grants no authority.
    /// </remarks>
    public static GovernedLoopGraphAuthoringService Create(
        WorkspacePaths paths,
        IGovernedLoopGraphRevisionStore revisionStore,
        IGovernedLoopNodeCatalog nodeCatalog,
        IGovernedLoopAuthoritySnapshotProvider authorityProvider,
        IGovernedLoopRevisionActorAuthorizer actorAuthorizer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(revisionStore);
        ArgumentNullException.ThrowIfNull(nodeCatalog);
        ArgumentNullException.ThrowIfNull(authorityProvider);
        ArgumentNullException.ThrowIfNull(actorAuthorizer);

        var authorityTransaction = new CapabilityAuthorityTransaction(paths);
        var validationService = new GovernedLoopGraphValidationService(nodeCatalog, authorityProvider);
        return new GovernedLoopGraphAuthoringService(
            revisionStore,
            validationService,
            actorAuthorizer,
            authorityTransaction,
            timeProvider);
    }
}
