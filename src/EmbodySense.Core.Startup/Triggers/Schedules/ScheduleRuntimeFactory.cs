using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Triggers.Schedules;

/// <summary>Composes one inert, one-shot schedule runtime over canonical stores and trigger admission.</summary>
public static class ScheduleRuntimeFactory
{
    /// <summary>Creates production composition with the default server-owned capability trust root.</summary>
    public static ScheduleRuntimeFacade Create(
        WorkspacePaths paths,
        IScheduleGovernedPayloadSource payloadSource,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(payloadSource);
        return Create(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), payloadSource, timeProvider);
    }

    /// <summary>Creates production composition with explicit, retained trust and payload sources.</summary>
    /// <remarks>
    /// The supplied sources are captured once for the facade lifetime and are never accepted per evaluation. Composition
    /// creates no timer, worker, watcher, or background task; callers invoke <see cref="ScheduleRuntimeFacade.EvaluateOnceAsync"/>
    /// explicitly.
    /// </remarks>
    public static ScheduleRuntimeFacade Create(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        IScheduleGovernedPayloadSource payloadSource,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(payloadSource);
        trustProvider.RequireDisjointWorkspace(paths.RootPath);

        var clock = timeProvider ?? TimeProvider.System;
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var triggerWorkspaceId = workspaceId["workspace-sha256:".Length..];
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
            timeProvider: clock,
            authorityTransaction: authorityTransaction);

        try
        {
            var profileStore = new AuthorityProfileStore(
                paths,
                trustProvider,
                clock,
                authorityTransaction: authorityTransaction);
            var profileSource = new AuthorityGrantProfileSource(profileStore);
            var roleSource = new AuthorityGrantRoleSource(
                workspaceId,
                roleStore,
                roleStore,
                new WorkspaceContextualRoleInstructionSourceProbe(paths),
                authorityTransaction);
            var grantResolver = new AuthorityGrantResolver(
                profileStore,
                profileSource,
                roleSource,
                publicationSource,
                bindingSource,
                authorityTransaction,
                clock);
            var catalogStore = new CapabilityCatalogStore(
                paths,
                trustProvider,
                authorityTransaction: authorityTransaction);
            var lifecycle = new CapabilityLifecycleMutationStore(
                paths,
                trustProvider,
                authorityTransaction: authorityTransaction);
            var catalog = new CapabilityLifecycleCatalogStore(catalogStore, lifecycle, authorityTransaction);
            var currentEvidence = new ScheduleCurrentEvidenceAdapter(
                triggerWorkspaceId,
                bindingSource,
                grantResolver,
                profileSource,
                catalog,
                payloadSource,
                authorityTransaction,
                clock);
            var overlap = new ScheduleRunOverlapAdapter(new CustomLoopRunStore(paths));
            var queue = CreateQueue(paths, clock);
            return CreateCore(
                new ScheduleStore(paths),
                currentEvidence,
                overlap,
                new SystemScheduleTimeZoneAdapter(TimeZoneInfo.GetSystemTimeZones()),
                queue.Admission,
                queue.History,
                clock,
                roleStore);
        }
        catch
        {
            roleStore.Dispose();
            throw;
        }
    }

    /// <summary>Creates canonical durable schedule and queue composition over caller-owned evidence ports.</summary>
    /// <remarks>This overload is useful when an embedding host owns exact trust adapters outside the local stores.</remarks>
    public static ScheduleRuntimeFacade Create(
        WorkspacePaths paths,
        IScheduleCurrentEvidencePort currentEvidence,
        IScheduleOverlapPort overlap,
        IScheduleTimeZonePort timeZone,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        ArgumentNullException.ThrowIfNull(overlap);
        ArgumentNullException.ThrowIfNull(timeZone);
        var clock = timeProvider ?? TimeProvider.System;
        var queue = CreateQueue(paths, clock);
        return CreateCore(
            new ScheduleStore(paths),
            currentEvidence,
            overlap,
            timeZone,
            queue.Admission,
            queue.History,
            clock,
            null);
    }

    /// <summary>Creates canonical durable queue composition over a caller-owned schedule store and evidence ports.</summary>
    /// <remarks>
    /// This overload supports embedding hosts that retain an alternate crash-safe store while preserving the shared
    /// evaluator, trigger admission, and fail-closed facade result boundary.
    /// </remarks>
    public static ScheduleRuntimeFacade Create(
        WorkspacePaths paths,
        IScheduleStorePort store,
        IScheduleCurrentEvidencePort currentEvidence,
        IScheduleOverlapPort overlap,
        IScheduleTimeZonePort timeZone,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        ArgumentNullException.ThrowIfNull(overlap);
        ArgumentNullException.ThrowIfNull(timeZone);
        var clock = timeProvider ?? TimeProvider.System;
        var queue = CreateQueue(paths, clock);
        return CreateCore(store, currentEvidence, overlap, timeZone, queue.Admission, queue.History, clock, null);
    }

    private static (ITriggerQueueAdmissionPort Admission, ITriggerDeliveryAdmissionHistoryPort History) CreateQueue(WorkspacePaths paths, TimeProvider clock)
    {
        var store = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime, timeProvider: clock);
        return (new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(store), store), store);
    }

    private static ScheduleRuntimeFacade CreateCore(
        IScheduleStorePort store,
        IScheduleCurrentEvidencePort currentEvidence,
        IScheduleOverlapPort overlap,
        IScheduleTimeZonePort timeZone,
        ITriggerQueueAdmissionPort queue,
        ITriggerDeliveryAdmissionHistoryPort queueHistory,
        TimeProvider clock,
        IDisposable? ownedResource)
        => new(store, currentEvidence, overlap, timeZone, queue, queueHistory, clock, ownedResource);
}
