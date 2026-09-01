using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Loops;
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
    /// <remarks>
    /// <paramref name="runStore"/> remains owned by the caller and must outlive the returned facade. The facade and
    /// its overlap adapter borrow the store only; disposing the facade never disposes the store.
    /// </remarks>
    /// <param name="paths">The workspace paths for schedule and authority persistence.</param>
    /// <param name="payloadSource">The retained governed payload source.</param>
    /// <param name="runStore">The caller-owned canonical run store used for overlap reads.</param>
    /// <param name="timeProvider">The optional clock used by the composition.</param>
    /// <returns>An inert one-shot schedule facade.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/>, <paramref name="payloadSource"/>, or <paramref name="runStore"/> is null.</exception>
    public static ScheduleRuntimeFacade Create(
        WorkspacePaths paths,
        IScheduleGovernedPayloadSource payloadSource,
        ICustomLoopRunStore runStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(payloadSource);
        ArgumentNullException.ThrowIfNull(runStore);
        return Create(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), payloadSource, runStore, timeProvider);
    }

    /// <summary>Creates production composition with explicit, retained trust and payload sources.</summary>
    /// <remarks>
    /// The supplied sources are captured once for the facade lifetime and are never accepted per evaluation. Composition
    /// creates no timer, worker, watcher, or background task; callers invoke <see cref="ScheduleRuntimeFacade.EvaluateOnceAsync"/>
    /// explicitly. <paramref name="runStore"/> remains owned by the caller, must outlive the returned facade, and is
    /// borrowed only for schedule overlap reads; factory or facade disposal never disposes it.
    /// </remarks>
    /// <param name="paths">The workspace paths for schedule and authority persistence.</param>
    /// <param name="trustProvider">The retained capability catalog trust provider.</param>
    /// <param name="payloadSource">The retained governed payload source.</param>
    /// <param name="runStore">The caller-owned canonical run store used for overlap reads.</param>
    /// <param name="timeProvider">The optional clock used by the composition.</param>
    /// <returns>An inert one-shot schedule facade.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/>, <paramref name="trustProvider"/>, <paramref name="payloadSource"/>, or <paramref name="runStore"/> is null.</exception>
    public static ScheduleRuntimeFacade Create(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        IScheduleGovernedPayloadSource payloadSource,
        ICustomLoopRunStore runStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(payloadSource);
        ArgumentNullException.ThrowIfNull(runStore);
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
            var overlap = new ScheduleRunOverlapAdapter(runStore);
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

    /// <summary>Creates canonical durable schedule and queue composition over caller-owned current evidence and run state.</summary>
    /// <remarks>
    /// <paramref name="runStore"/> is borrowed only for overlap reads, remains owned by the caller, and must outlive
    /// the returned facade. Disposing the facade never disposes the store, including when construction or evaluation fails.
    /// </remarks>
    /// <param name="paths">The workspace paths for schedule and queue persistence.</param>
    /// <param name="currentEvidence">The caller-owned port that resolves current governed target evidence.</param>
    /// <param name="runStore">The caller-owned canonical run store used for overlap reads.</param>
    /// <param name="timeZone">The caller-owned time-zone evidence port.</param>
    /// <param name="timeProvider">The optional clock used by the composition.</param>
    /// <returns>An inert one-shot schedule facade.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/>, <paramref name="currentEvidence"/>, <paramref name="runStore"/>, or <paramref name="timeZone"/> is null.</exception>
    public static ScheduleRuntimeFacade Create(
        WorkspacePaths paths,
        IScheduleCurrentEvidencePort currentEvidence,
        ICustomLoopRunStore runStore,
        IScheduleTimeZonePort timeZone,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(timeZone);
        var clock = timeProvider ?? TimeProvider.System;
        var queue = CreateQueue(paths, clock);
        return CreateCore(
            new ScheduleStore(paths),
            currentEvidence,
            new ScheduleRunOverlapAdapter(runStore),
            timeZone,
            queue.Admission,
            queue.History,
            clock,
            null);
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

    internal static ScheduleRuntimeFacade Create(
        IScheduleStorePort store,
        IScheduleCurrentEvidencePort currentEvidence,
        IScheduleOverlapPort overlap,
        IScheduleTimeZonePort timeZone,
        ITriggerQueueAdmissionPort queue,
        ITriggerDeliveryAdmissionHistoryPort queueHistory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        ArgumentNullException.ThrowIfNull(overlap);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(queueHistory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return CreateCore(store, currentEvidence, overlap, timeZone, queue, queueHistory, timeProvider, null);
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
