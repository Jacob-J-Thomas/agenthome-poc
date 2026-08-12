using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>Composes governed-loop admission under one physical-workspace authority fence.</summary>
public static class GovernedLoopAdmissionFactory
{
    /// <summary>Creates production admission with the default server-owned trust root.</summary>
    /// <param name="paths">The initialized canonical workspace paths.</param>
    /// <param name="timeProvider">The trusted clock, or the system clock when omitted.</param>
    /// <returns>A disposable surface-neutral admission facade.</returns>
    public static GovernedLoopAdmissionFacade Create(WorkspacePaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Create(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), timeProvider);
    }

    /// <summary>Creates production admission with an explicit server-owned trust provider.</summary>
    /// <param name="paths">The initialized canonical workspace paths.</param>
    /// <param name="trustProvider">The exact trust provider shared by all authenticated stores.</param>
    /// <param name="timeProvider">The trusted clock, or the system clock when omitted.</param>
    /// <returns>A disposable surface-neutral admission facade.</returns>
    /// <remarks>
    /// One workspace-derived identity and one reentrant authority transaction are shared across admission evidence,
    /// graph/lifecycle resolution, role and grant resolution, and capability admission. Composition is inert and never
    /// synthesizes an ambient grant.
    /// </remarks>
    public static GovernedLoopAdmissionFacade Create(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var authorityTransaction = new CapabilityAuthorityTransaction(paths);
        var lifecycleStore = new GovernedLoopRevisionLifecycleStore(
            paths,
            trustProvider,
            authorityTransaction: authorityTransaction);
        var graphStore = new GovernedLoopGraphRevisionStore(
            paths,
            lifecycleStore,
            trustProvider,
            authorityTransaction: authorityTransaction);
        var publicationSource = new GovernedLoopPublishedRevisionSource(lifecycleStore, authorityTransaction);
        var bindingSource = new GovernedLoopGrantBindingSource(publicationSource, graphStore, authorityTransaction);
        var roleStore = new ContextualRoleRevisionStore(
            paths,
            workspaceId,
            timeProvider: timeProvider,
            authorityTransaction: authorityTransaction);

        try
        {
            var roleSource = new AuthorityGrantRoleSource(
                workspaceId,
                roleStore,
                roleStore,
                new WorkspaceContextualRoleInstructionSourceProbe(paths),
                authorityTransaction);
            var authorityStore = new AuthorityProfileStore(
                paths,
                trustProvider,
                timeProvider,
                authorityTransaction: authorityTransaction);
            var grantResolver = new AuthorityGrantResolver(
                authorityStore,
                new AuthorityGrantProfileSource(authorityStore),
                roleSource,
                publicationSource,
                bindingSource,
                authorityTransaction,
                timeProvider);
            var admissionStore = new GovernedLoopAdmissionStore(
                paths,
                trustProvider,
                authorityTransaction: authorityTransaction);
            var capabilityAdmission = CapabilityAdmissionFactory.Create(
                paths,
                trustProvider,
                authorityTransaction,
                timeProvider);
            var facade = Create(
                workspaceId,
                admissionStore,
                graphStore,
                bindingSource,
                roleSource,
                grantResolver,
                capabilityAdmission,
                authorityTransaction,
                new GovernedLoopAdmissionRunIdentityGenerator(),
                timeProvider,
                roleStore);
            return facade;
        }
        catch
        {
            roleStore.Dispose();
            throw;
        }
    }

    /// <summary>Creates inert admission over caller-owned Application ports.</summary>
    /// <param name="workspaceId">The exact canonical identity of the physical workspace.</param>
    /// <param name="store">The append-only admission evidence store.</param>
    /// <param name="graphStore">The exact immutable graph revision store.</param>
    /// <param name="bindingSource">The published graph-to-role binding source.</param>
    /// <param name="roleSource">The exact role revision source.</param>
    /// <param name="grantResolver">The current grant resolver.</param>
    /// <param name="capabilityAdmissionService">The current capability admission service.</param>
    /// <param name="authorityTransaction">The single reentrant authority fence shared by every supplied port.</param>
    /// <param name="runIdentityGenerator">The server-owned run identity generator.</param>
    /// <param name="timeProvider">The trusted clock, or the system clock when omitted.</param>
    /// <returns>A surface-neutral facade that does not own the supplied ports.</returns>
    public static GovernedLoopAdmissionFacade Create(
        string workspaceId,
        IGovernedLoopAdmissionStore store,
        IGovernedLoopGraphRevisionStore graphStore,
        IGovernedLoopGrantBindingSource bindingSource,
        IAuthorityGrantRoleSource roleSource,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAdmissionService capabilityAdmissionService,
        ICapabilityAuthorityTransaction authorityTransaction,
        IGovernedLoopAdmissionRunIdentityGenerator runIdentityGenerator,
        TimeProvider? timeProvider = null)
        => Create(
            workspaceId,
            store,
            graphStore,
            bindingSource,
            roleSource,
            grantResolver,
            capabilityAdmissionService,
            authorityTransaction,
            runIdentityGenerator,
            timeProvider,
            null);

    private static GovernedLoopAdmissionFacade Create(
        string workspaceId,
        IGovernedLoopAdmissionStore store,
        IGovernedLoopGraphRevisionStore graphStore,
        IGovernedLoopGrantBindingSource bindingSource,
        IAuthorityGrantRoleSource roleSource,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAdmissionService capabilityAdmissionService,
        ICapabilityAuthorityTransaction authorityTransaction,
        IGovernedLoopAdmissionRunIdentityGenerator runIdentityGenerator,
        TimeProvider? timeProvider,
        IDisposable? ownedResource)
    {
        var service = new GovernedLoopAdmissionService(
            workspaceId,
            store,
            graphStore,
            bindingSource,
            roleSource,
            grantResolver,
            capabilityAdmissionService,
            authorityTransaction,
            runIdentityGenerator,
            timeProvider);
        return new GovernedLoopAdmissionFacade(service, ownedResource);
    }
}
