using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.HumanInput.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Publication;
using EmbodySense.Core.Application.HumanInput.Publication.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Posture.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Loops.Execution.Authority;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.GraphAuthoring;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Loops.InvocationPreparation;
using EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;
using EmbodySense.Core.Startup.Loops.Posture.Models;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Capabilities.Models;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Startup.Tests.Capabilities;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Tests.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;
using EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;
using EmbodySense.HumanInputContinuationHost;
using EmbodySense.Core.Startup.Triggers;
using EmbodySense.Core.Startup.Triggers.Models;
using EmbodySense.Core.Startup.Tests.Triggers;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Tests.HumanInput;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed class AgentRuntimeFactoryTests
{
    internal static async Task CreateAsync_exposes_authoring_that_observes_the_runtime_materialized_nonterminal_run_until_runtime_disposal()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var attemptStartedPath = workspace.File("runtime-authoring-attempt-started.marker");
        var attemptReleasePath = workspace.File("runtime-authoring-attempt-release.marker");
        var executablePath = await CreateFakeCodexExecutableAsync(workspace, turnStartMarkerPath: attemptStartedPath, turnReleaseMarkerPath: attemptReleasePath);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        var authoring = runStoreProvider.CreateLoopAuthoringFacade();
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider);
        var runtime = await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Web);
        Task<LoopRunInvocationResponse>? invocation = null;

        try
        {
            var created = Assert.IsType<LoopDefinitionSnapshot>((await authoring.CreateAsync("create-runtime-authoring-active-loop")).Definition);
            var invocationInput = new LoopRunInvocationInput(
                created.Id,
                created.DefinitionVersion,
                created.ContentHash,
                "invoke-runtime-authoring-active-loop",
                "hold this runtime-owned custom-loop run");
            invocation = runtime.InvokeCustomLoopAsync(invocationInput);

            await WaitForHeldAttemptAsync(attemptStartedPath, invocation);
            var materialized = Assert.Single(await runtime.ListCustomLoopRunsAsync(), run => run.LoopId == created.Id);
            var exactRun = Assert.IsType<LoopRunSnapshot>(await runtime.GetCustomLoopRunAsync(materialized.Id));
            var update = await authoring.UpdateAsync(
                created.Id,
                created.DefinitionVersion,
                "update-runtime-authoring-active-loop",
                new LoopDefinitionInput(
                    created.DisplayName,
                    "This update must remain blocked by the exact active runtime run.",
                    created.TriggerPolicy,
                    created.InferenceSteps,
                    created.ToolAssignments,
                    created.ExitPolicy));
            var delete = await authoring.DeleteAsync(
                created.Id,
                created.DefinitionVersion,
                "delete-runtime-authoring-active-loop");

            Assert.Null(materialized.CompletedAtUtc);
            Assert.Equal(created.Id, exactRun.LoopId);
            Assert.Null(exactRun.CompletedAtUtc);
            Assert.Equal("ActiveRunExists", update.Status);
            Assert.Equal("ActiveRunExists", delete.Status);
            Assert.Equal(created.Id, (await runtime.LoopAuthoring.GetAsync(created.Id))!.Id);

            await File.WriteAllTextAsync(attemptReleasePath, "release");
            var completed = await invocation;
            Assert.Equal("Completed", completed.ExecutionStatus);
            Assert.Equal("Completed", completed.Run!.Status);
            Assert.False(File.Exists(attemptReleasePath));

            await runtime.DisposeAsync();
            await runtime.DisposeAsync();
            Assert.Equal(created.Id, (await authoring.GetAsync(created.Id))!.Id);
        }
        finally
        {
            if (invocation is { IsCompleted: false })
            {
                await File.WriteAllTextAsync(attemptReleasePath, "release");
                await invocation;
            }

            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_exposes_one_shared_operational_facade_over_the_canonical_runtime_stores()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(workspace.RootPath)["workspace-sha256:".Length..];
        Assert.True(AuthorityActorId.TryParse("owner", out var actorId, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actorId, "runtime", workspaceId, "operator", out var actorContext, out _));
        var envelope = TriggerWorkerTestData.Envelope(actorContext: actorContext);
        var store = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime);
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, envelope.Adapter, true, envelope.ActorContext, envelope.Authority, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(3), out var delivery, out _));
        var admission = await new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(store), store).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(delivery!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal));

        var posture = await runtime.GovernedLoopOperations.ReadAsync(new GovernedLoopOperationalPostureQuery(3, 4, 5, 6));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admission.Status);
        Assert.Equal(GovernedLoopOperationalPostureReadStatus.Available, posture.Status);
        var snapshot = Assert.IsType<GovernedLoopOperationalPostureSnapshot>(posture.Snapshot);
        Assert.Equal(CapabilityWorkspaceScopeId.Create(workspace.RootPath), snapshot.WorkspaceId);
        Assert.Equal(envelope.DeliveryId.Value, Assert.Single(snapshot.Queue.Items).DeliveryId);
        Assert.Empty(snapshot.Schedules.Items);
        Assert.Empty(snapshot.Wakes.Items);
        Assert.Empty(snapshot.Runs.Items);
        Assert.Equal("local-background", snapshot.Coordinator.CoordinatorId);
        Assert.Equal("stopped", snapshot.Coordinator.State);

        var control = await runtime.GovernedLoopOperations.ControlAsync(new LoopOperationalControlInput(
            "operational-missing-delivery",
            GovernedLoopOperationalControlKind.CancelDelivery,
            "delivery-missing",
            1,
            new string('a', 64),
            snapshot.ControlAuthorityEvidenceHash));
        var replay = await runtime.GovernedLoopOperations.ControlAsync(new LoopOperationalControlInput(
            "operational-missing-delivery",
            GovernedLoopOperationalControlKind.CancelDelivery,
            "delivery-missing",
            1,
            new string('a', 64),
            snapshot.ControlAuthorityEvidenceHash));

        Assert.Equal(GovernedLoopOperationalControlStatus.NotFound, control.Status);
        Assert.Equal(GovernedLoopOperationalControlStatus.NotFound, replay.Status);
        Assert.Equal(control.ReceiptHash, replay.ReceiptHash);
        Assert.Single(Directory.EnumerateFiles(paths.GovernedLoopOperationalControlReceiptsPath, "*.json"));
    }

    [Fact]
    public async Task StartGovernedLoopLocalBackgroundAsync_parks_schedule_authoring_pending_work_and_reuses_the_factory_owned_queue()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var scheduleDefinition = ScheduleContractTestData.Definition();
        Assert.True(ScheduleContractHash.TryComputeDefinition(scheduleDefinition, out var scheduleDefinitionHash, out _));
        var scheduleState = ScheduleContractTestData.State(
            definitionRevision: scheduleDefinition.Revision,
            definitionHash: scheduleDefinitionHash!,
            scheduleId: scheduleDefinition.ScheduleId);
        var schedules = new ScheduleStore(paths);
        var scheduleCreated = await schedules.CreateAsync(new ScheduleStoreCreateRequest(scheduleDefinition, scheduleState, scheduleDefinitionHash!));
        var workspaceId = CapabilityWorkspaceScopeId.Create(workspace.RootPath)["workspace-sha256:".Length..];
        Assert.True(AuthorityActorId.TryParse("owner", out var actorId, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actorId, "runtime", workspaceId, "operator", out var actorContext, out _));
        var envelope = TriggerWorkerTestData.Envelope(actorContext: actorContext);
        var queue = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime);
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(
            envelope,
            envelope.Loop,
            envelope.Adapter,
            true,
            envelope.ActorContext,
            envelope.Authority,
            TriggerWorkerTestData.CreatedAtUtc.AddSeconds(3),
            out var delivery,
            out _));
        var admission = await new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(queue), queue)
            .AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(delivery!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal));

        Assert.Equal(ScheduleStoreMutationStatus.Applied, scheduleCreated.Status);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admission.Status);
        long terminalLifecycleVersion;
        await using (var runtime = await CreateRuntimeAsync(workspace))
        {
            var activation = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
            var repeated = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
            var rejected = await WaitForDispatchRejectedAsync(queue);
            var coordinator = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync("local-background");
            var parkedSchedule = await schedules.ReadAsync(scheduleDefinition.ScheduleId);
            var ready = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();
            var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();
            var repeatedStop = await runtime.StopGovernedLoopLocalBackgroundAsync();
            var stoppedStatus = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();

            Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, activation.Status);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Ready, activation.Readiness);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Local, activation.Ownership);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning, repeated.Status);
            Assert.Equal(TriggerQueueEntryState.DispatchRejected, rejected.State);
            Assert.Equal(TriggerDispatchOutcome.Rejected, rejected.Dispatch?.Outcome);
            Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, coordinator?.Status);
            Assert.Equal(GovernedLoopCoordinatorStatus.Running, coordinator?.Snapshot?.LatestLifecycle.Status);
            Assert.Equal(ScheduleStoreReadStatus.Found, parkedSchedule.Status);
            Assert.Equal(scheduleState.StateRevision, parkedSchedule.State?.StateRevision);
            Assert.Equal(scheduleState.NextOccurrence, parkedSchedule.State?.NextOccurrence);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Ready, ready.Readiness);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Local, ready.Ownership);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, stop.Status);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, stop.Readiness);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, stop.Ownership);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, repeatedStop.Status);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, stoppedStatus.Readiness);
            Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, stoppedStatus.Ownership);
            Assert.Single((await queue.GetSnapshotAsync(DateTimeOffset.UtcNow)).Entries);
            terminalLifecycleVersion = Assert.IsType<long>((await new GovernedLoopCoordinatorEvidenceStore(paths)
                .ReadAsync("local-background"))?.Snapshot?.LatestLifecycle.LifecycleVersion);
        }

        var stopped = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync("local-background");
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, stopped?.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Stopped, stopped?.Snapshot?.LatestLifecycle.Status);
        Assert.Equal(terminalLifecycleVersion, stopped?.Snapshot?.LatestLifecycle.LifecycleVersion);
    }

    [Fact]
    public async Task StartGovernedLoopLocalBackgroundAsync_reports_live_peer_ownership_without_attempting_duplicate_delivery()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var owner = await CreateRuntimeAsync(workspace);
        await using var peer = await CreateRuntimeAsync(workspace);

        var ownerStart = await owner.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var peerStart = await peer.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var peerStatus = await peer.ReadGovernedLoopLocalBackgroundStatusAsync();
        var peerStop = await peer.StopGovernedLoopLocalBackgroundAsync();
        var ownerStatus = await owner.ReadGovernedLoopLocalBackgroundStatusAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, ownerStart.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer, peerStart.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, peerStart.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer, peerStart.Ownership);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer, peerStatus.Ownership);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.OwnedByLivePeer, peerStop.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Ready, ownerStatus.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Local, ownerStatus.Ownership);
    }

    [Fact]
    public async Task Concurrent_background_activation_projects_the_losing_acquisition_as_live_peer()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var first = await CreateRuntimeAsync(workspace);
        await using var second = await CreateRuntimeAsync(workspace);

        var results = await Task.WhenAll(
            first.StartGovernedLoopLocalBackgroundWithStatusAsync(),
            second.StartGovernedLoopLocalBackgroundWithStatusAsync());

        Assert.Single(results, result => result.Status == AgentRuntimeGovernedLoopBackgroundStartStatus.Started);
        Assert.Single(results, result => result.Status == AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer);
        Assert.Contains(results, result => result.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer);
    }

    [Fact]
    public async Task Concurrent_legacy_background_activation_projects_live_peer_as_available()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var first = await CreateRuntimeAsync(workspace);
        await using var second = await CreateRuntimeAsync(workspace);

        var results = await Task.WhenAll(
            first.StartGovernedLoopLocalBackgroundAsync(),
            second.StartGovernedLoopLocalBackgroundAsync());

        Assert.All(results, result => Assert.True(result.Available));
        Assert.All(results, result => Assert.Equal("Available", result.Status));
    }

    [Fact]
    public async Task Failed_durable_background_evidence_projects_degraded_status_and_requires_repair()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var acquisition = await store.TryAcquireAsync(ExpiredPeerAcquisition());
        var snapshot = Assert.IsType<GovernedLoopCoordinatorSnapshot>(acquisition!.Snapshot);
        var terminalAtUtc = snapshot.LatestLifecycle.UpdatedAtUtc.AddTicks(1);
        var failed = GovernedLoopSleepContractHash.Apply(snapshot.LatestLifecycle with
        {
            LifecycleVersion = snapshot.LatestLifecycle.LifecycleVersion + 1,
            Status = GovernedLoopCoordinatorStatus.Failed,
            UpdatedAtUtc = terminalAtUtc,
            TerminalAtUtc = terminalAtUtc,
            ContentHash = string.Empty
        });
        var mutation = await store.AppendLifecycleAsync(new GovernedLoopCoordinatorLifecycleMutationRequest(
            snapshot.Ownership,
            snapshot.Ownership.ContentHash,
            snapshot.LatestLifecycle.LifecycleVersion,
            snapshot.LatestLifecycle.ContentHash,
            failed));
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, mutation!.Status);

        await using var runtime = await CreateRuntimeAsync(workspace);
        var status = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync();
        var typedStart = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var legacyStart = await runtime.StartGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, status.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, status.Ownership);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired, typedStart.Status);
        Assert.False(typedStart.RetryAllowed);
        Assert.False(legacyStart.Available);
        Assert.Equal("Failed", legacyStart.Status);
    }

    [Fact]
    public async Task Coordinator_repair_is_unavailable_without_an_authenticated_current_operator_provider()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        await using var runtime = await CreateRuntimeAsync(workspace);
        var preview = await runtime.GovernedLoopCoordinatorRepair.PreviewAsync(
            new GovernedLoopCoordinatorRepairPreviewRequest("local-background", "repair-local-background"));

        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, preview.Status);
        Assert.Null(preview.Disposition);
    }

    [Theory]
    [InlineData(AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus.Denied, "authenticated-operator", false, GovernedLoopCoordinatorRepairPreviewStatus.Unauthorized)]
    [InlineData(AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus.Unavailable, "authenticated-operator", false, GovernedLoopCoordinatorRepairPreviewStatus.Unavailable)]
    [InlineData(AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus.Ready, null, false, GovernedLoopCoordinatorRepairPreviewStatus.Corrupt)]
    [InlineData(AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus.Ready, "authenticated-operator", true, GovernedLoopCoordinatorRepairPreviewStatus.Corrupt)]
    public async Task Coordinator_repair_preview_fails_closed_for_nonready_or_malformed_current_operator_authority(
        AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus authorityStatus,
        string? actorId,
        bool returnNull,
        GovernedLoopCoordinatorRepairPreviewStatus expectedStatus)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var authority = new GovernedLoopCoordinatorRepairTestAuthorityProvider(actorId, authorityStatus) { ReturnNull = returnNull };

        await using var runtime = await CreateRuntimeAsync(workspace, coordinatorRepairAuthorityProvider: authority);
        var preview = await runtime.GovernedLoopCoordinatorRepair.PreviewAsync(
            new GovernedLoopCoordinatorRepairPreviewRequest("local-background", "repair-local-background"));

        Assert.Equal(expectedStatus, preview.Status);
        Assert.Null(preview.Disposition);
    }

    [Fact]
    public async Task Coordinator_repair_facade_previews_exact_failure_and_starts_one_fenced_successor()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var acquired = await store.TryAcquireAsync(ExpiredPeerAcquisition());
        var initial = Assert.IsType<GovernedLoopCoordinatorSnapshot>(acquired!.Snapshot);
        var occurredAtUtc = initial.LatestHeartbeat.RecordedAtUtc.AddTicks(1);
        var failure = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorFailure(
            GovernedLoopCoordinatorFailure.CurrentSchemaVersion,
            1,
            initial.Ownership,
            GovernedLoopCoordinatorFailureKind.CorruptState,
            "test-failed-coordinator",
            occurredAtUtc,
            string.Empty));
        Assert.Equal(
            GovernedLoopCoordinatorFailureMutationStatus.Appended,
            (await store.AppendFailureAsync(new GovernedLoopCoordinatorFailureMutationRequest(
                initial.Ownership,
                initial.Ownership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.None,
                0,
                null,
                failure)))!.Status);
        var failedAtUtc = occurredAtUtc.AddTicks(1);
        var failed = GovernedLoopSleepContractHash.Apply(initial.LatestLifecycle with
        {
            LifecycleVersion = initial.LatestLifecycle.LifecycleVersion + 1,
            Status = GovernedLoopCoordinatorStatus.Failed,
            UpdatedAtUtc = failedAtUtc,
            TerminalAtUtc = failedAtUtc,
            ContentHash = string.Empty
        });
        Assert.Equal(
            GovernedLoopCoordinatorLifecycleMutationStatus.Appended,
            (await store.AppendLifecycleAsync(new GovernedLoopCoordinatorLifecycleMutationRequest(
                initial.Ownership,
                initial.Ownership.ContentHash,
                initial.LatestLifecycle.LifecycleVersion,
                initial.LatestLifecycle.ContentHash,
                failed)))!.Status);

        await using var runtime = await CreateRuntimeAsync(
            workspace,
            coordinatorRepairAuthorityProvider: new GovernedLoopCoordinatorRepairTestAuthorityProvider("authenticated-operator"));
        var preview = await runtime.GovernedLoopCoordinatorRepair.PreviewAsync(
            new GovernedLoopCoordinatorRepairPreviewRequest("local-background", "repair-local-background"));
        var submitted = await runtime.GovernedLoopCoordinatorRepair.SubmitAsync(
            new GovernedLoopCoordinatorRepairSubmitRequest(preview.Disposition!));
        var durable = await store.ReadAsync("local-background");

        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Ready, preview.Status);
        Assert.Equal(initial.Ownership, preview.Disposition!.FailedOwnership);
        Assert.Equal("authenticated-operator", preview.Disposition.ActorId);
        Assert.Equal(failed.ContentHash, preview.Disposition.TerminalLifecycleHash);
        Assert.Equal(failure.ContentHash, preview.Disposition.LatestFailureHash);
        Assert.Equal(GovernedLoopCoordinatorRepairExecutionStatus.Repaired, submitted.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Accepted, submitted.Submission.Status);
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, durable!.Status);
        Assert.Equal(2, durable.Snapshot!.Ownership.OwnershipEpoch);
        Assert.Equal(GovernedLoopCoordinatorStatus.Running, durable.Snapshot.LatestLifecycle.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task Cli_runtime_starts_stops_and_observes_the_human_input_family_through_the_canonical_background_host()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var observer = new SignalingCoordinatorBoundaryObserver();
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithGovernedLoopLocalCoordinatorBoundaryObserver(observer);
        await using var runtime = await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Cli);

        var beforeProbe = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        await observer.HeartbeatDue.WaitAsync(TimeSpan.FromSeconds(5));
        await observer.HumanInputWorkAttempted.WaitAsync(TimeSpan.FromSeconds(5));
        var afterProbe = await WaitForHumanInputExecutableAsync(runtime);
        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.False(HumanInputDescriptor(beforeProbe).IsExecutable);
        Assert.True(HumanInputDescriptor(afterProbe).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, stop.Status);
    }

    [Fact]
    public async Task Factory_keeps_human_input_non_executable_when_the_canonical_run_store_is_incompatible_and_projects_actionable_repair()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var runStore = new CustomLoopRunStore(paths);
        await PersistRunningRunAsync(runStore, RunningRun("incompatible-human-input-recovery"));
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var observer = new SignalingCoordinatorBoundaryObserver();
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithGovernedLoopLocalCoordinatorBoundaryObserver(observer);
        await using var runtime = await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Cli);

        var beforeStart = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var started = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        await observer.HumanInputWorkAttempted.WaitAsync(TimeSpan.FromSeconds(5));
        Directory.CreateDirectory(paths.CustomLoopRunsPath);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        await File.WriteAllTextAsync(indexPath, "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}");
        var degraded = await WaitForBackgroundReadinessAsync(runtime, AgentRuntimeGovernedLoopBackgroundReadiness.Degraded);
        var repair = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        Assert.False(HumanInputDescriptor(beforeStart).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, started.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, degraded.Ownership);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired, repair.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, repair.Readiness);
        Assert.False(repair.RetryAllowed);
        Assert.False(HumanInputDescriptor(catalog).IsExecutable);
        Assert.Contains("requires explicit repair", repair.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Factory_keeps_human_input_non_executable_when_the_canonical_policy_source_is_corrupt_and_projects_actionable_repair()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var policiesPath = Path.Combine(paths.AgentPath, "human-input", "policies");
        Directory.CreateDirectory(policiesPath);
        await File.WriteAllTextAsync(Path.Combine(policiesPath, "generation"), "{not-json");
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var observer = new SignalingCoordinatorBoundaryObserver();
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithGovernedLoopLocalCoordinatorBoundaryObserver(observer);
        await using var runtime = await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Cli);

        var beforeStart = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var started = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        await observer.HumanInputWorkAttempted.WaitAsync(TimeSpan.FromSeconds(5));
        var degraded = await WaitForBackgroundReadinessAsync(runtime, AgentRuntimeGovernedLoopBackgroundReadiness.Degraded);
        var repair = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        Assert.False(HumanInputDescriptor(beforeStart).IsExecutable);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, started.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, degraded.Ownership);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired, repair.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, repair.Readiness);
        Assert.False(repair.RetryAllowed);
        Assert.False(HumanInputDescriptor(catalog).IsExecutable);
        Assert.Contains("requires explicit repair", repair.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_coordinator_boundary_observer_configuration_is_immutable()
    {
        var factory = new AgentRuntimeFactory(new RejectingApprovalPrompt());
        var observer = new SignalingCoordinatorBoundaryObserver();

        var configured = factory.WithGovernedLoopLocalCoordinatorBoundaryObserver(observer);

        Assert.NotSame(factory, configured);
        Assert.Throws<ArgumentNullException>(() => factory.WithGovernedLoopLocalCoordinatorBoundaryObserver(null!));
    }

    [Fact]
    public async Task StartGovernedLoopLocalBackgroundAsync_immediately_restarts_a_confirmed_local_terminal_owner_with_exact_fenced_evidence()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var store = new GovernedLoopCoordinatorEvidenceStore(new WorkspacePaths(workspace.RootPath));

        var initial = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var stopped = await runtime.StopGovernedLoopLocalBackgroundAsync();
        var restarted = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var durable = await store.ReadAsync("local-background");

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, initial.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, stopped.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, restarted.Status);
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, durable!.Status);
        Assert.Equal(2, durable.Snapshot!.Ownership.OwnershipEpoch);
        Assert.Equal(GovernedLoopCoordinatorStatus.Running, durable.Snapshot.LatestLifecycle.Status);
        Assert.True(durable.Snapshot.LatestHeartbeat.LeaseExpiresAtUtc > durable.Snapshot.LatestHeartbeat.RecordedAtUtc);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    [Fact]
    public async Task StopGovernedLoopLocalBackgroundAsync_preserves_expired_nonterminal_peer_posture_instead_of_fabricating_stopped()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var acquisition = await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(ExpiredPeerAcquisition());
        await using var runtime = await CreateRuntimeAsync(workspace);

        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();
        var durable = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync("local-background");

        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, acquisition!.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, stop.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Degraded, stop.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Unknown, stop.Ownership);
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, durable!.Status);
        Assert.Equal(GovernedLoopCoordinatorStatus.Starting, durable.Snapshot!.LatestLifecycle.Status);
        Assert.Equal("expired-peer", durable.Snapshot.Ownership.OwnerId);
    }

    [Fact]
    public async Task StopGovernedLoopLocalBackgroundAsync_preserves_corrupt_durable_evidence_instead_of_fabricating_stopped()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, (await store.TryAcquireAsync(ExpiredPeerAcquisition()))!.Status);
        var ledger = Directory.EnumerateFiles(
                paths.AgentFile(Path.Combine("loops", "execution", "coordinator")),
                "ledger-*.json")
            .Order(StringComparer.Ordinal)
            .Last();
        await File.WriteAllTextAsync(ledger, "{invalid");
        await using var runtime = await CreateRuntimeAsync(workspace);

        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();
        var durable = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync("local-background");

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable, stop.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable, stop.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Unknown, stop.Ownership);
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Corrupt, durable!.Status);
    }

    [Fact]
    public async Task StopGovernedLoopLocalBackgroundAsync_replays_already_stopped_when_a_new_host_confirms_terminal_evidence()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var owner = await CreateRuntimeAsync(workspace);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, (await owner.StartGovernedLoopLocalBackgroundWithStatusAsync()).Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await owner.StopGovernedLoopLocalBackgroundAsync()).Status);

        await using var replacement = await CreateRuntimeAsync(workspace);
        var stop = await replacement.StopGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped, stop.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Stopped, stop.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.None, stop.Ownership);
    }

    [Fact]
    public async Task StopGovernedLoopLocalBackgroundAsync_preserves_a_running_session_failure_when_durable_evidence_becomes_unreadable()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);

        var started = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var ledger = Directory.EnumerateFiles(
                paths.AgentFile(Path.Combine("loops", "execution", "coordinator")),
                "ledger-*.json")
            .Order(StringComparer.Ordinal)
            .Last();
        await File.WriteAllTextAsync(ledger, "{invalid");

        var stop = await runtime.StopGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, started.Status);
        Assert.Contains(stop.Status, new[]
        {
            AgentRuntimeGovernedLoopBackgroundStopStatus.Failed,
            AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable
        });
        Assert.NotEqual(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, stop.Status);
    }

    [Fact]
    public async Task StartGovernedLoopLocalBackgroundWithStatusAsync_projects_corrupt_durable_evidence_as_repair_required()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, (await store.TryAcquireAsync(ExpiredPeerAcquisition()))!.Status);
        var ledger = Directory.EnumerateFiles(
                paths.AgentFile(Path.Combine("loops", "execution", "coordinator")),
                "ledger-*.json")
            .Order(StringComparer.Ordinal)
            .Last();
        await File.WriteAllTextAsync(ledger, "{invalid");
        await using var runtime = await CreateRuntimeAsync(workspace);

        var typedStart = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        var legacyStart = await runtime.StartGovernedLoopLocalBackgroundAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired, typedStart.Status);
        Assert.False(typedStart.RetryAllowed);
        Assert.False(legacyStart.Available);
        Assert.Equal("Failed", legacyStart.Status);
    }

    [Fact]
    public async Task StartGovernedLoopLocalBackgroundWithStatusAsync_reports_unavailable_without_claiming_background_ownership()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
        using var activeExecution = gate.TryAcquire("background-start-unavailable", new string('a', CustomLoopLimits.Sha256HexCharacters)).Lease!;
        await using var runtime = await CreateRuntimeAsync(workspace);

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable, start.Status);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable, start.Readiness);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundOwnership.Unknown, start.Ownership);
        Assert.True(start.RetryAllowed);
    }

    [Fact]
    public async Task CreateAsync_keeps_human_input_non_executable_until_the_background_worker_proves_current_storage_and_exposes_role_bound_graph_authoring()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var retryPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-infer",
            "infer",
            ["retryable-no-effect"],
            [],
            3,
            1_000,
            10_000,
            "fixed",
            250,
            250,
            "none",
            0,
            3_000,
            null,
            null,
            null,
            null));
        var rejectedRetryPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-infer",
            "infer",
            ["retryable-no-effect"],
            [],
            catalog.RetryPolicies.MaximumAttempts + 1,
            1_000,
            10_000,
            "fixed",
            250,
            250,
            "none",
            0,
            null,
            null,
            null,
            null,
            null));
        var exponentialPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-exponential",
            "infer",
            ["dispatch-proved-not-started"],
            ["provider-dispatch-not-started"],
            5,
            100,
            10_000,
            "exponential",
            4,
            15,
            "deterministic-bounded",
            5,
            20,
            2,
            30,
            "USD",
            4));
        var noBackoffPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-none",
            "infer",
            ["timeout-cancellation-no-effect"],
            [],
            2,
            100,
            10_000,
            "none",
            0,
            0,
            "none",
            0,
            null,
            null,
            null,
            null,
            null));
        var malformedRetryPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-malformed",
            "infer",
            ["unknown-failure"],
            [],
            2,
            100,
            10_000,
            "unknown-backoff",
            0,
            0,
            "unknown-jitter",
            0,
            null,
            null,
            null,
            null,
            null));
        var nullFailureClassesPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-missing-failure-class",
            "infer",
            null!,
            [],
            2,
            100,
            10_000,
            "fixed",
            10,
            10,
            "none",
            0,
            null,
            null,
            null,
            null,
            null));
        var dependencyPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-dependency",
            "infer",
            ["dependency-unavailable-before-dispatch"],
            [],
            2,
            100,
            10_000,
            "fixed",
            10,
            10,
            "none",
            0,
            null,
            null,
            null,
            null,
            null));
        var unknownBackoffPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-unknown-backoff",
            "infer",
            ["retryable-no-effect"],
            [],
            2,
            100,
            10_000,
            "unrecognized",
            0,
            0,
            "none",
            0,
            null,
            null,
            null,
            null,
            null));
        var unknownJitterPreview = runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(new GovernedLoopRetryPolicyPreviewInput(
            "retry-unknown-jitter",
            "infer",
            ["retryable-no-effect"],
            [],
            2,
            100,
            10_000,
            "fixed",
            1,
            1,
            "unrecognized",
            0,
            null,
            null,
            null,
            null,
            null));
        var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
        var candidate = BrowserGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
            role.ContentHash));
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-browser-governed-graph",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var reloaded = await runtime.GovernedLoopGraphAuthoring.ReadAsync(candidate.GraphId!);

        Assert.Equal("available", catalog.Status);
        Assert.Contains(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.Trigger && item.IsExecutable);
        Assert.Contains(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.Wait && item.IsExecutable);
        var humanReview = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        Assert.True(humanReview.IsAdvertised);
        Assert.False(humanReview.IsExecutable);
        Assert.Equal(GovernedLoopHumanReviewNodeCatalogContract.Descriptor.Descriptor, humanReview.Descriptor);
        var humanInput = Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanInput);
        Assert.True(humanInput.IsAdvertised);
        Assert.False(humanInput.IsExecutable);
        Assert.True(GovernedLoopSequentialNodeDescriptors.IsSupported(humanInput.Descriptor));
        Assert.All(catalog.NodeDescriptors.Where(item => item.IsExecutable), item => Assert.True(GovernedLoopSequentialNodeDescriptors.IsSupported(item.Descriptor)));
        Assert.Equal(8, catalog.RetryPolicies.MaximumAttempts);
        Assert.Equal(["none", "fixed", "exponential"], catalog.RetryPolicies.BackoffStrategies);
        Assert.Equal("valid", retryPreview.Status);
        var retryPolicy = Assert.IsType<GovernedLoopRetryPolicy>(retryPreview.Policy);
        var retryBounds = Assert.IsType<GovernedLoopRetryPolicyPreviewSnapshot>(retryPreview.Preview);
        Assert.Equal("retry-infer", retryPolicy.PolicyId);
        Assert.Equal("infer", retryPolicy.NodeId);
        Assert.Matches("^[0-9a-f]{64}$", retryPolicy.ContentHash);
        Assert.Equal(500, retryBounds.MaximumBackoffMilliseconds);
        Assert.Equal(3_500, retryBounds.MaximumReachableElapsedMilliseconds);
        Assert.True(retryBounds.CurrentAdmissionStillRequired);
        Assert.Equal("invalid", rejectedRetryPreview.Status);
        Assert.Null(rejectedRetryPreview.Policy);
        var exponentialBounds = Assert.IsType<GovernedLoopRetryPolicyPreviewSnapshot>(exponentialPreview.Preview);
        Assert.Equal("valid", exponentialPreview.Status);
        Assert.Equal(4, exponentialBounds.MaximumRetries);
        Assert.Equal(52, exponentialBounds.MaximumBackoffMilliseconds);
        Assert.Equal(500, exponentialBounds.MaximumAttemptExecutionMilliseconds);
        Assert.Equal(552, exponentialBounds.MaximumReachableElapsedMilliseconds);
        Assert.Equal(20, exponentialBounds.MaximumTokens);
        Assert.Equal(2, exponentialBounds.MaximumToolCalls);
        Assert.Equal(30, exponentialBounds.MaximumCostMicrounits);
        Assert.Equal("USD", exponentialBounds.MaximumCostCurrency);
        Assert.Equal(4, exponentialBounds.MaximumResourceUnits);
        Assert.Equal("valid", noBackoffPreview.Status);
        Assert.Equal(0, Assert.IsType<GovernedLoopRetryPolicyPreviewSnapshot>(noBackoffPreview.Preview).MaximumBackoffMilliseconds);
        Assert.Equal("invalid", malformedRetryPreview.Status);
        Assert.Equal("retry-policy-authoring-invalid", malformedRetryPreview.Reason);
        Assert.Equal("invalid", nullFailureClassesPreview.Status);
        Assert.Equal("valid", dependencyPreview.Status);
        Assert.Equal("invalid", unknownBackoffPreview.Status);
        Assert.Equal("invalid", unknownJitterPreview.Status);
        Assert.Equal("committed", created.Status);
        Assert.Matches("^[0-9a-f]{64}$", created.AuthoringRequestHash);
        Assert.Matches("^[0-9a-f]{64}$", created.GraphValidationEvidenceHash);
        Assert.Equal("ready", reloaded.Status);
        Assert.Equal(candidate.GraphId, reloaded.Lifecycle?.GraphId);
        Assert.Equal(candidate.RevisionId, reloaded.Lifecycle?.DraftRevision?.RevisionId);
        Assert.Single(reloaded.Artifacts);
    }

    [Fact]
    public async Task CreateAsync_prepares_confirms_replays_and_scope_filters_one_published_web_invocation_grant()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var paths = new WorkspacePaths(workspace.RootPath);

        var invalidPreparation = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(string.Empty, string.Empty));
        var missingPreparation = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest("missing-browser-governed-graph", "revision-1"));
        var missingConfirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            "missing-browser-governed-graph",
            "revision-1",
            new string('a', 64),
            "confirm-missing-browser-governed-graph"));
        var invalidConfirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty));
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
        var candidate = BrowserGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
            role.ContentHash));
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-prepared-browser-governed-graph",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var draft = Assert.IsType<GovernedLoopGraphReadResponse>(created.Current);
        var draftHead = Assert.IsType<GovernedLoopRevisionLifecycleHead>(draft.Lifecycle);
        var draftPreparation = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var published = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "publish-prepared-browser-governed-graph",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            draftHead.Status,
            draftHead.LifecycleVersion,
            draftHead.DraftRevision,
            draftHead.PublishedRevision,
            null));
        var publishedHead = Assert.IsType<GovernedLoopRevisionLifecycleHead>(published.Current?.Lifecycle);

        var initial = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var preview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(initial.Preview);
        var confirmation = new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            preview.SemanticHash,
            "confirm-prepared-browser-governed-graph");
        var staleConfirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            new string('f', 64),
            "stale-prepared-browser-governed-graph"));
        var staleSelectorConfirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            "revision-0",
            preview.SemanticHash,
            "stale-selector-browser-governed-graph"));
        var afterStaleConfirmation = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var confirmed = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(confirmation);
        var replayed = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(confirmation);
        var duplicate = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            preview.SemanticHash,
            "duplicate-prepared-browser-governed-graph"));
        var prepared = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var stale = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, "revision-0"));
        var authorityStore = new AuthorityProfileStore(new WorkspacePaths(workspace.RootPath), new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var persistedGrant = await authorityStore.ReadAsync(Assert.IsType<AuthorityGrantReference>(confirmed.Grant).GrantId);
        var persistedProfile = await authorityStore.ReadAsync(Assert.IsType<AuthorityGrantStoreSnapshot>(persistedGrant.Snapshot).CurrentGrant.Binding!.Profile.Reference.ProfileId.Value);
        File.WriteAllText(new WorkspacePaths(workspace.RootPath).AuthorityProfilesDocumentPath, "{");
        var unavailablePreparation = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var unavailableConfirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(confirmation);
        File.WriteAllText(Path.Combine(paths.GovernedLoopRevisionsPath, "lifecycle.json"), "{");
        var malformedPublicationPreparation = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));

        Assert.Equal("committed", created.Status);
        Assert.Equal("committed", published.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Invalid, invalidPreparation.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.NotFound, missingPreparation.Status);
        Assert.Empty(missingPreparation.EligibleGrants);
        Assert.Null(missingPreparation.Preview);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, missingConfirmation.Status);
        Assert.Null(missingConfirmation.Grant);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Invalid, invalidConfirmation.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.NotFound, draftPreparation.Status);
        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Published, publishedHead.Status);
        Assert.Equal(candidate.RevisionId, publishedHead.PublishedRevision?.Revision.RevisionId);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, initial.Status);
        Assert.Empty(initial.EligibleGrants);
        Assert.Equal(candidate.GraphId, initial.Publication?.Revision.GraphId);
        Assert.Equal(candidate.RevisionId, initial.Publication?.Revision.RevisionId);
        Assert.Equal(initial.AsOfUtc, preview.AsOfUtc);
        Assert.Null(preview.ExpiresAtUtc);
        Assert.Matches("^[0-9a-f]{64}$", preview.SemanticHash);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, staleConfirmation.Status);
        Assert.Null(staleConfirmation.Grant);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, staleSelectorConfirmation.Status);
        Assert.Null(staleSelectorConfirmation.Grant);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, afterStaleConfirmation.Status);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed, confirmed.Status);
        Assert.NotNull(confirmed.Grant);
        Assert.Equal(confirmed.Grant, replayed.Grant);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed, replayed.Status);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, duplicate.Status);
        Assert.Null(duplicate.Grant);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Ready, prepared.Status);
        Assert.Null(prepared.Preview);
        var eligibleGrant = Assert.Single(prepared.EligibleGrants);
        Assert.Equal(confirmed.Grant, eligibleGrant.Grant);
        Assert.Null(eligibleGrant.ExpiresAtUtc);
        Assert.Null(prepared.ExpiresAtUtc);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Stale, stale.Status);
        Assert.Empty(stale.EligibleGrants);
        Assert.Null(stale.Preview);
        Assert.Equal(AuthorityGrantStoreReadStatus.Ready, persistedGrant.Status);
        Assert.Equal(AuthorityProfileReadStatus.Available, persistedProfile.Status);
        var currentGrant = Assert.IsType<AuthorityGrantStoreSnapshot>(persistedGrant.Snapshot).CurrentGrant;
        var currentProfile = Assert.IsType<AuthorityProfileRecord>(persistedProfile.Record).CurrentProfile;
        Assert.NotEqual(DateTimeOffset.UnixEpoch, currentProfile.IssuedAtUtc);
        Assert.Equal(currentProfile.IssuedAtUtc, currentGrant.Boundary.EffectiveAtUtc);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Unavailable, unavailablePreparation.Status);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, unavailableConfirmation.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Unavailable, malformedPublicationPreparation.Status);
        Assert.Empty(malformedPublicationPreparation.EligibleGrants);
        Assert.Null(malformedPublicationPreparation.Preview);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_published_graph_with_an_unsupported_deterministic_projection_node()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
        var candidate = BrowserUnsupportedInvocationProjectionGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
            role.ContentHash));
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-unsupported-invocation-projection-graph",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var draft = Assert.IsType<GovernedLoopGraphReadResponse>(created.Current);
        var draftHead = Assert.IsType<GovernedLoopRevisionLifecycleHead>(draft.Lifecycle);
        var published = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "publish-unsupported-invocation-projection-graph",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            draftHead.Status,
            draftHead.LifecycleVersion,
            draftHead.DraftRevision,
            draftHead.PublishedRevision,
            null));

        var preparation = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var confirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            new string('a', 64),
            "confirm-unsupported-invocation-projection-graph"));

        Assert.Equal("committed", created.Status);
        Assert.Equal("committed", published.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Ineligible, preparation.Status);
        Assert.Null(preparation.Preview);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Ineligible, confirmation.Status);
        Assert.Null(confirmation.Grant);

        File.WriteAllText(Path.Combine(new WorkspacePaths(workspace.RootPath).GovernedLoopRevisionsPath, "lifecycle.json"), "{");
        var unavailableConfirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            new string('a', 64),
            "confirm-corrupt-unsupported-invocation-projection-graph"));

        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, unavailableConfirmation.Status);
        Assert.Null(unavailableConfirmation.Grant);
    }

    [Fact]
    public async Task CreateAsync_excludes_consumed_one_shot_invocation_grants_and_fails_closed_when_completion_evidence_is_pending_or_corrupt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
        var candidate = BrowserGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
            role.ContentHash));
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-consumed-browser-governed-graph",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var draftHead = Assert.IsType<GovernedLoopRevisionLifecycleHead>(created.Current?.Lifecycle);
        var published = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "publish-consumed-browser-governed-graph",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            draftHead.Status,
            draftHead.LifecycleVersion,
            draftHead.DraftRevision,
            draftHead.PublishedRevision,
            null));
        var initial = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var initialPreview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(initial.Preview);
        var firstConfirmation = new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            initialPreview.SemanticHash,
            "confirm-consumed-browser-governed-graph");
        var firstConfirmed = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(firstConfirmation);
        var firstGrant = Assert.IsType<AuthorityGrantReference>(firstConfirmed.Grant);
        var paths = new WorkspacePaths(workspace.RootPath);
        var effectAuthorityRootPath = Path.Combine(paths.AgentPath, "loops", "effect-authority");
        var effectAuthorityPaths = new[]
        {
            effectAuthorityRootPath,
            Path.Combine(effectAuthorityRootPath, "decisions.json"),
            Path.Combine(effectAuthorityRootPath, "decisions.proved.json"),
            Path.Combine(effectAuthorityRootPath, ".mutations.lock"),
        };
        var effectAuthorityPathStateBeforePrepare = effectAuthorityPaths.Select(Path.Exists).ToArray();
        var unconsumed = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var effectAuthorityPathStateAfterPrepare = effectAuthorityPaths.Select(Path.Exists).ToArray();

        var usageStore = new GovernedLoopEffectAuthorityEvidenceStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var firstCompletion = CompletionUsage(firstGrant, "first-visible-run", "complete-first-visible-run");
        var completionPending = await usageStore.BeginCompletionAsync(firstCompletion);
        var completionCompleted = await usageStore.CompleteCompletionAsync(firstCompletion);
        var consumed = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var replacementPreview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(consumed.Preview);
        var staleReplay = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(firstConfirmation);
        var replacementConfirmation = new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            replacementPreview.SemanticHash,
            "confirm-replacement-consumed-browser-governed-graph");
        var replacementConfirmed = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(replacementConfirmation);
        var replacementGrant = Assert.IsType<AuthorityGrantReference>(replacementConfirmed.Grant);
        var replacementReady = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));

        var replacementCompletion = CompletionUsage(replacementGrant, "replacement-visible-run", "complete-replacement-visible-run");
        var pendingReplacement = await usageStore.BeginCompletionAsync(replacementCompletion);
        var authorityBeforeUnavailableRead = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.AuthorityProfilesDocumentPath)));
        var pendingEvidence = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var pendingConfirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(replacementConfirmation);
        var effectAuthorityPrimaryPath = Path.Combine(paths.AgentPath, "loops", "effect-authority", "decisions.json");
        File.WriteAllText(effectAuthorityPrimaryPath, "{");
        var corruptEvidence = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var corruptConfirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(replacementConfirmation);
        var authorityAfterUnavailableRead = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.AuthorityProfilesDocumentPath)));

        Assert.Equal("committed", created.Status);
        Assert.Equal("committed", published.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, initial.Status);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed, firstConfirmed.Status);
        Assert.All(effectAuthorityPathStateBeforePrepare, Assert.False);
        Assert.Equal(effectAuthorityPathStateBeforePrepare, effectAuthorityPathStateAfterPrepare);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Ready, unconsumed.Status);
        Assert.Equal(firstGrant, Assert.Single(unconsumed.EligibleGrants).Grant);
        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending, completionPending.Status);
        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted, completionCompleted.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, consumed.Status);
        Assert.Empty(consumed.EligibleGrants);
        Assert.Equal(initialPreview.SemanticHash, replacementPreview.SemanticHash);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, staleReplay.Status);
        Assert.Null(staleReplay.Grant);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed, replacementConfirmed.Status);
        Assert.NotEqual(firstGrant, replacementGrant);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Ready, replacementReady.Status);
        Assert.Equal(replacementGrant, Assert.Single(replacementReady.EligibleGrants).Grant);
        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending, pendingReplacement.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Unavailable, pendingEvidence.Status);
        Assert.Empty(pendingEvidence.EligibleGrants);
        Assert.Null(pendingEvidence.Preview);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, pendingConfirmation.Status);
        Assert.Null(pendingConfirmation.Grant);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Unavailable, corruptEvidence.Status);
        Assert.Empty(corruptEvidence.EligibleGrants);
        Assert.Null(corruptEvidence.Preview);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, corruptConfirmation.Status);
        Assert.Null(corruptConfirmation.Grant);
        Assert.Equal(authorityBeforeUnavailableRead, authorityAfterUnavailableRead);
    }

    [Fact]
    public async Task CreateAsync_prepares_and_confirms_the_manual_inference_validate_condition_bounded_retry_exit_fail_acceptance_shape()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = InvocationPreparationReadyModelProfile.Create();
        await InstallModelProfileAsync(paths, workspace.ServerStatePath, profile.Descriptor);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, additionalModelProfileProviders: [profile.Provider]);

        var role = BrowserInvocationAcceptanceRole(paths, profile.Descriptor.Id.Value);
        var roleRequest = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
            "create-browser-invocation-acceptance-role",
            string.Empty,
            ContextualRoleRevisionMutationKind.Create,
            role.Identity.RoleId,
            "test-author",
            role,
            null,
            DateTimeOffset.UtcNow));
        using (var roleStore = new ContextualRoleRevisionStore(paths, CapabilityWorkspaceScopeId.Create(paths.RootPath)))
        {
            Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await roleStore.MutateAsync(roleRequest)).Status);
        }

        var candidate = BrowserInvocationAcceptanceGraphCandidate(new ContextualRoleRevisionPin(role.Identity, role.ContentHash), profile.Descriptor.Id.Value);
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-browser-invocation-acceptance-shape",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var draft = Assert.IsType<GovernedLoopGraphReadResponse>(created.Current);
        var head = Assert.IsType<GovernedLoopRevisionLifecycleHead>(draft.Lifecycle);
        var published = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "publish-browser-invocation-acceptance-shape",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            head.Status,
            head.LifecycleVersion,
            head.DraftRevision,
            head.PublishedRevision,
            null));
        var prepared = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        Assert.True(prepared.Status == GovernedLoopInvocationPreparationStatus.ConfirmationRequired, prepared.Detail);
        var preview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(prepared.Preview);
        var confirmed = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            preview.SemanticHash,
            "confirm-browser-invocation-acceptance-shape"));
        var persistedGrant = await new AuthorityProfileStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath))
            .ReadAsync(Assert.IsType<AuthorityGrantReference>(confirmed.Grant).GrantId);

        Assert.True(created.Status == "committed", string.Join(Environment.NewLine, created.Errors.Select(error => $"{error.Code}: {error.Message}")));
        Assert.Equal("committed", published.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, prepared.Status);
        Assert.Empty(prepared.EligibleGrants);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed, confirmed.Status);
        var currentGrant = Assert.IsType<AuthorityGrantStoreSnapshot>(persistedGrant.Snapshot).CurrentGrant;
        Assert.Contains(currentGrant.RequestedCeiling.Capabilities, identity => identity.Id.Value == "org.embodysense/model-inference");
        Assert.Contains(currentGrant.RequestedCeiling.Capabilities, identity => identity.Id.Equals(profile.Descriptor.Id));
        Assert.Contains(currentGrant.RequestedCeiling.DataClasses, dataClass => dataClass.Value == "sensitive");
    }

    [Fact]
    public async Task CreateAsync_rejects_an_unavailable_or_missing_graph_selected_model_profile_before_authority_effects()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var profile = InvocationPreparationReadyModelProfile.Create();
        await InstallModelProfileAsync(paths, workspace.ServerStatePath, profile.Descriptor);
        GovernedLoopGraphCandidate candidate;

        await using (var readyRuntime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, additionalModelProfileProviders: [profile.Provider]))
        {
            var role = BrowserInvocationAcceptanceRole(paths, profile.Descriptor.Id.Value);
            using var roleStore = new ContextualRoleRevisionStore(paths, CapabilityWorkspaceScopeId.Create(paths.RootPath));
            var roleRequest = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
                "create-missing-profile-browser-invocation-role",
                string.Empty,
                ContextualRoleRevisionMutationKind.Create,
                role.Identity.RoleId,
                "test-author",
                role,
                null,
                DateTimeOffset.UtcNow));
            Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await roleStore.MutateAsync(roleRequest)).Status);

            candidate = BrowserInvocationAcceptanceGraphCandidate(new ContextualRoleRevisionPin(role.Identity, role.ContentHash), profile.Descriptor.Id.Value) with
            {
                GraphId = "browser-invocation-missing-profile-graph",
            };
            var created = await readyRuntime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
                "create-missing-profile-browser-invocation-graph",
                GovernedLoopGraphMutationKind.CreateDraft,
                candidate.GraphId!,
                GovernedLoopRevisionLifecycleStatus.Unknown,
                0,
                null,
                null,
                candidate));
            var draft = Assert.IsType<GovernedLoopGraphReadResponse>(created.Current);
            var head = Assert.IsType<GovernedLoopRevisionLifecycleHead>(draft.Lifecycle);
            var published = await readyRuntime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
                "publish-missing-profile-browser-invocation-graph",
                GovernedLoopGraphMutationKind.Publish,
                candidate.GraphId!,
                head.Status,
                head.LifecycleVersion,
                head.DraftRevision,
                head.PublishedRevision,
                null));
            var prepared = await readyRuntime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));

            Assert.Equal("committed", created.Status);
            Assert.Equal("committed", published.Status);
            Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, prepared.Status);
        }

        await using var missingRuntime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var missing = await missingRuntime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var confirmation = await missingRuntime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            new string('a', 64),
            "confirm-missing-profile-browser-invocation-graph"));

        Assert.Equal(GovernedLoopInvocationPreparationStatus.Unavailable, missing.Status);
        Assert.Empty(missing.EligibleGrants);
        Assert.Null(missing.Preview);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, confirmation.Status);
        Assert.Null(confirmation.Grant);
    }

    [Fact]
    public async Task CreateAsync_rejects_the_unavailable_configured_model_profile_before_authority_effects()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var paths = new WorkspacePaths(workspace.RootPath);
        var role = BrowserInvocationAcceptanceRole(paths);
        using var roleStore = new ContextualRoleRevisionStore(paths, CapabilityWorkspaceScopeId.Create(paths.RootPath));
        var roleRequest = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
            "create-unavailable-configured-profile-browser-invocation-role",
            string.Empty,
            ContextualRoleRevisionMutationKind.Create,
            role.Identity.RoleId,
            "test-author",
            role,
            null,
            DateTimeOffset.UtcNow));
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, (await roleStore.MutateAsync(roleRequest)).Status);

        var candidate = BrowserInvocationAcceptanceGraphCandidate(new ContextualRoleRevisionPin(role.Identity, role.ContentHash)) with
        {
            GraphId = "browser-invocation-unavailable-configured-profile-graph",
        };
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-unavailable-configured-profile-browser-invocation-graph",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var draft = Assert.IsType<GovernedLoopGraphReadResponse>(created.Current);
        var head = Assert.IsType<GovernedLoopRevisionLifecycleHead>(draft.Lifecycle);
        var published = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "publish-unavailable-configured-profile-browser-invocation-graph",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            head.Status,
            head.LifecycleVersion,
            head.DraftRevision,
            head.PublishedRevision,
            null));
        var prepared = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));

        Assert.Equal("committed", created.Status);
        Assert.Equal("committed", published.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Unavailable, prepared.Status);
        Assert.Empty(prepared.EligibleGrants);
        Assert.Null(prepared.Preview);
    }

    [Fact]
    public async Task CreateAsync_keeps_visible_governed_invocation_preparation_and_confirmation_unavailable_to_cli_without_authority_mutation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        GovernedLoopGraphCandidate candidate;
        GovernedLoopInvocationAuthorityPreview preview;
        await using (var web = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web))
        {
            var catalog = await web.GovernedLoopGraphAuthoring.ReadCatalogAsync();
            var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
            candidate = BrowserGraphCandidate(new ContextualRoleRevisionPin(
                new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
                role.ContentHash));
            var created = await web.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
                "create-cli-denied-browser-governed-graph",
                GovernedLoopGraphMutationKind.CreateDraft,
                candidate.GraphId!,
                GovernedLoopRevisionLifecycleStatus.Unknown,
                0,
                null,
                null,
                candidate));
            var draft = Assert.IsType<GovernedLoopGraphReadResponse>(created.Current);
            var head = Assert.IsType<GovernedLoopRevisionLifecycleHead>(draft.Lifecycle);
            var published = await web.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
                "publish-cli-denied-browser-governed-graph",
                GovernedLoopGraphMutationKind.Publish,
                candidate.GraphId!,
                head.Status,
                head.LifecycleVersion,
                head.DraftRevision,
                head.PublishedRevision,
                null));
            var prepared = await web.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));

            Assert.Equal("committed", created.Status);
            Assert.Equal("committed", published.Status);
            preview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(prepared.Preview);
        }

        var authorityBefore = new[] { paths.AuthorityProfilesDocumentPath, paths.AuthorityProfilesProofPath }
            .Select(path => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null)
            .ToArray();
        await using var cli = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Cli);
        var preparedByCli = await cli.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var confirmedByCli = await cli.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            preview.SemanticHash,
            "confirm-cli-denied-browser-governed-graph"));
        var authorityAfter = new[] { paths.AuthorityProfilesDocumentPath, paths.AuthorityProfilesProofPath }
            .Select(path => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null)
            .ToArray();

        Assert.Equal(GovernedLoopInvocationPreparationStatus.Unavailable, preparedByCli.Status);
        Assert.Empty(preparedByCli.EligibleGrants);
        Assert.Null(preparedByCli.Preview);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, confirmedByCli.Status);
        Assert.Null(confirmedByCli.Grant);
        Assert.Equal(authorityBefore, authorityAfter);
    }

    [Fact]
    public async Task CreateAsync_resumes_confirmation_after_an_exact_profile_only_crash_window()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
        var candidate = BrowserGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
            role.ContentHash));
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-recovery-browser-governed-graph",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var draftHead = Assert.IsType<GovernedLoopRevisionLifecycleHead>(created.Current?.Lifecycle);
        var published = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "publish-recovery-browser-governed-graph",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            draftHead.Status,
            draftHead.LifecycleVersion,
            draftHead.DraftRevision,
            draftHead.PublishedRevision,
            null));
        var publication = Assert.IsType<GovernedLoopRevisionPublicationPin>(published.Current?.Lifecycle?.PublishedRevision);
        var initial = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var preview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(initial.Preview);
        const string OperationId = "resume-profile-only-browser-governed-graph";

        var profile = CreateProfileOnlyRecoveryRecord(publication, preview.SemanticHash, OperationId, DateTimeOffset.UtcNow);
        var paths = new WorkspacePaths(workspace.RootPath);
        var profileStore = new AuthorityProfileStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        Assert.True(AuthorityActorId.TryParse("embodysense.web", out var actor, out _));
        Assert.True(AuthorityPurpose.TryParse("governed-loop-invocation", out var purpose, out _));
        var persistedProfile = await profileStore.MutateAsync(new AuthorityProfileMutation(
            AuthorityProfileMutationKind.Create,
            "invocation-profile-op-" + HashInvocationTestValue(OperationId),
            0,
            profile,
            null,
            null,
            actor!,
            purpose!));
        var beforeResume = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var resumed = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            preview.SemanticHash,
            OperationId));
        var ready = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));

        Assert.Equal("committed", created.Status);
        Assert.Equal("committed", published.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, initial.Status);
        Assert.Equal(AuthorityProfileMutationStatus.Applied, persistedProfile.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, beforeResume.Status);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed, resumed.Status);
        Assert.NotNull(resumed.Grant);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Ready, ready.Status);
        Assert.Equal(resumed.Grant, Assert.Single(ready.EligibleGrants).Grant);
    }

    [Fact]
    public async Task Explicit_command_provider_projects_only_safe_exact_template_identity_and_fails_closed_without_artifact_readiness()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var registration = GovernedCommandActionFactoryTests.TypedRegistration();
        var provider = new CommandActionRuntimeProvider(
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            AvailableCommandActionProcessIsolationBoundary.Instance);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, commandActionRuntimeProvider: provider);

        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        var descriptor = Assert.Single(catalog.NodeDescriptors, item => CommandActionNodeDescriptors.Matches(item.Descriptor, registration.Template));
        var command = Assert.IsType<GovernedLoopGraphCatalogCommandActionSnapshot>(descriptor.CommandAction);
        Assert.True(descriptor.IsAdvertised);
        Assert.False(descriptor.IsExecutable);
        Assert.Equal("runtime-unavailable", command.Availability);
        Assert.Equal(registration.Template.TemplateId, command.TemplateId);
        Assert.Equal(registration.Template.TemplateVersion, command.TemplateVersion);
        Assert.Equal(registration.Template.ContentHash, command.TemplateHash);
        Assert.Equal("denied", command.Network);
        Assert.Equal("artifact-root", command.WorkingDirectory);
        var input = Assert.Single(descriptor.Parameters, parameter => parameter.Id == "input");
        var identifier = Assert.Single(descriptor.Parameters, parameter => parameter.Id == "identifier");
        var literal = Assert.Single(descriptor.Parameters, parameter => parameter.Id == "literal");
        Assert.Equal("json", input.ValueKind);
        Assert.Equal(512, input.MaximumUtf8Bytes);
        Assert.Equal("capability-path", identifier.ValueKind);
        Assert.Equal(128, identifier.MaximumUtf8Bytes);
        Assert.Equal("text", literal.ValueKind);
        Assert.Equal(8, literal.MaximumUtf8Bytes);
        Assert.False(literal.AllowLeadingOption);
        Assert.False(literal.AllowResponseFileReference);
        var serialized = System.Text.Json.JsonSerializer.Serialize(command);
        Assert.DoesNotContain("command.exe", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("file:///", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pinned_runtime_refreshes_graph_catalog_after_current_capability_lifecycle_changes()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var catalogTrust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(workspace.ServerStatePath);
        var artifactVerifier = new AlwaysTrustedLifecycleArtifactVerifier();
        var registration = GovernedCommandActionFactoryTests.Registration();
        var stage = new CapabilityArtifactStageRequest(
            registration.Manifest,
            new CapabilityArtifactContent("command-artifact"u8.ToArray()),
            new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "test-server-policy", "Verified."));
        var catalog = new CapabilityCatalogService(new CapabilityCatalogStore(paths, catalogTrust));
        var catalogRevision = (await catalog.ReadAsync(null, 1)).Page!.CatalogRevision;
        catalogRevision = (await catalog.DeclareAsync(stage.Manifest.Descriptor, catalogRevision, "declare-pinned-runtime-command")).CatalogRevision!.Value;
        catalogRevision = (await catalog.InstallAsync(stage.Manifest.Descriptor.Id, catalogRevision, "install-pinned-runtime-command")).CatalogRevision!.Value;
        catalogRevision = (await catalog.VerifyAsync(stage.Manifest.Descriptor.Id, catalogRevision, "verify-pinned-runtime-command")).CatalogRevision!.Value;
        catalogRevision = (await catalog.EnableAsync(stage.Manifest.Descriptor.Id, catalogRevision, "enable-pinned-runtime-command")).CatalogRevision!.Value;
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, (await catalog.MarkHealthyAsync(stage.Manifest.Descriptor.Id, catalogRevision, "healthy-pinned-runtime-command")).Status);
        var artifacts = new CapabilityArtifactStore(paths, artifactTrust, artifactVerifier);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifacts.StageAsync(stage)).Status);
        var activation = await artifacts.ActivateAsync(new CapabilityArtifactActivationRequest(stage.Manifest, 0, "activate-pinned-runtime-command"));
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, activation.Status);
        var activeRegistration = registration with
        {
            Template = CommandActionTemplateContract.Create(
                registration.Template.SchemaVersion,
                registration.Template.Capability,
                registration.Template.Implementation,
                registration.Template.ArtifactDigest,
                activation.Activation!.Revision,
                registration.Template.TemplateId,
                registration.Template.TemplateVersion,
                registration.Template.Slots,
                registration.Template.Arguments,
                registration.Template.Environment,
                registration.Template.SecondaryGrammar,
                registration.Template.StandardInput,
                registration.Template.StandardInputSlot,
                registration.Template.Output,
                registration.Template.Isolation,
                registration.Template.RequiresCredentialChannel),
        };
        var provider = new CommandActionRuntimeProvider([activeRegistration], artifacts, AvailableCommandActionProcessIsolationBoundary.Instance);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, commandActionRuntimeProvider: provider);
        var lifecycle = CapabilityLifecycleFactory.CreateSelection(paths, catalogTrust, artifactTrust, artifactVerifier, new AuditLog(paths));

        var initial = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var command = Assert.Single(initial.NodeDescriptors, node => CommandActionNodeDescriptors.Matches(node.Descriptor, activeRegistration.Template));
        Assert.True(command.IsExecutable);

        var disablePreview = await lifecycle.PreviewAsync(new CapabilityLifecycleSelectionRequest("disable-pinned-runtime-command", CapabilityLifecycleOperationKind.Disable, activeRegistration.Manifest.Descriptor.Id));
        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, disablePreview.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await lifecycle.MutateAsync(disablePreview.Preview!)).Status);

        var disabled = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        Assert.False(Assert.Single(disabled.NodeDescriptors, node => CommandActionNodeDescriptors.Matches(node.Descriptor, activeRegistration.Template)).IsExecutable);

        var rollbackPreview = await lifecycle.PreviewAsync(new CapabilityLifecycleSelectionRequest("rollback-pinned-runtime-command", CapabilityLifecycleOperationKind.Rollback, activeRegistration.Manifest.Descriptor.Id));
        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, rollbackPreview.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await lifecycle.MutateAsync(rollbackPreview.Preview!)).Status);
        var reenabled = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        Assert.True(Assert.Single(reenabled.NodeDescriptors, node => CommandActionNodeDescriptors.Matches(node.Descriptor, activeRegistration.Template)).IsExecutable);

        var removePreview = await lifecycle.PreviewAsync(new CapabilityLifecycleSelectionRequest("remove-pinned-runtime-command", CapabilityLifecycleOperationKind.Remove, activeRegistration.Manifest.Descriptor.Id));
        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, removePreview.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await lifecycle.MutateAsync(removePreview.Preview!)).Status);
        var removed = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        Assert.False(Assert.Single(removed.NodeDescriptors, node => CommandActionNodeDescriptors.Matches(node.Descriptor, activeRegistration.Template)).IsExecutable);

        var rollbackRemovalPreview = await lifecycle.PreviewAsync(new CapabilityLifecycleSelectionRequest("rollback-pinned-runtime-command-removal", CapabilityLifecycleOperationKind.Rollback, activeRegistration.Manifest.Descriptor.Id));
        Assert.Equal(CapabilityLifecycleSelectionStatus.Ready, rollbackRemovalPreview.Status);
        Assert.Equal(CapabilityLifecycleMutationStatus.Applied, (await lifecycle.MutateAsync(rollbackRemovalPreview.Preview!)).Status);
        var restored = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        Assert.True(Assert.Single(restored.NodeDescriptors, node => CommandActionNodeDescriptors.Matches(node.Descriptor, activeRegistration.Template)).IsExecutable);
    }

    [Fact]
    public async Task Command_catalog_keeps_a_registered_template_visible_but_disabled_without_isolation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var registration = GovernedCommandActionFactoryTests.Registration(workspaceTarget: true);
        var provider = new CommandActionRuntimeProvider(
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            DenyingCommandActionProcessIsolationBoundary.Instance);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, commandActionRuntimeProvider: provider);

        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        var descriptor = Assert.Single(catalog.NodeDescriptors, item => CommandActionNodeDescriptors.Matches(item.Descriptor, registration.Template));
        var command = Assert.IsType<GovernedLoopGraphCatalogCommandActionSnapshot>(descriptor.CommandAction);
        Assert.True(descriptor.IsAdvertised);
        Assert.False(descriptor.IsExecutable);
        Assert.Equal("runtime-unavailable", command.Availability);
        var target = Assert.Single(descriptor.Parameters, parameter => parameter.Id == "target");
        Assert.Equal("workspace-relative-target", target.ValueKind);
        Assert.Equal(512, target.MaximumUtf8Bytes);
        Assert.False(target.AllowLeadingOption);
        Assert.False(target.AllowResponseFileReference);
    }

    [Fact]
    public async Task Command_catalog_omits_a_registration_that_cannot_preserve_graph_value_semantics()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var registration = GovernedCommandActionFactoryTests.Registration();
        var incompatibleTemplate = CommandActionTemplateContract.Create(
            registration.Template.SchemaVersion,
            registration.Template.Capability,
            registration.Template.Implementation,
            registration.Template.ArtifactDigest,
            registration.Template.ActivationRevision,
            registration.Template.TemplateId,
            registration.Template.TemplateVersion,
            [new CommandActionSlotDefinition("mode", CommandActionSlotKind.Enumeration, 64, null, null, ["@file"], true)],
            [new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "mode")],
            registration.Template.Environment,
            registration.Template.SecondaryGrammar,
            CommandActionStandardInputKind.Closed,
            null,
            registration.Template.Output,
            registration.Template.Isolation,
            registration.Template.RequiresCredentialChannel);
        var incompatible = registration with { Template = incompatibleTemplate };
        var provider = new CommandActionRuntimeProvider(
            [incompatible],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            AvailableCommandActionProcessIsolationBoundary.Instance);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, commandActionRuntimeProvider: provider);

        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();

        Assert.Equal("available", catalog.Status);
        Assert.DoesNotContain(catalog.NodeDescriptors, item => CommandActionNodeDescriptors.IsCommandAction(item.Descriptor));
    }

    [Fact]
    public void Graph_authoring_selects_the_exact_lifecycle_target_role_when_a_publication_has_a_successor_draft()
    {
        var draft = GovernedLoopRevisionReference.Create(1, "browser-governed-graph", "revision-2", new string('b', 64));
        var published = GovernedLoopRevisionReference.Create(1, "browser-governed-graph", "revision-1", new string('a', 64));
        var pin = new GovernedLoopRevisionPublicationPin(1, published, "publish-browser-graph", new string('c', 64));

        var disableTarget = GovernedLoopGraphAuthoringFacade.SelectTargetRevision(new GovernedLoopGraphMutationInput(
            "disable-browser-graph",
            GovernedLoopGraphMutationKind.Disable,
            published.GraphId,
            GovernedLoopRevisionLifecycleStatus.Published,
            3,
            draft,
            pin,
            null));
        var archiveTarget = GovernedLoopGraphAuthoringFacade.SelectTargetRevision(new GovernedLoopGraphMutationInput(
            "archive-browser-graph",
            GovernedLoopGraphMutationKind.Archive,
            published.GraphId,
            GovernedLoopRevisionLifecycleStatus.Disabled,
            4,
            draft,
            pin,
            null));
        var replaceTarget = GovernedLoopGraphAuthoringFacade.SelectTargetRevision(new GovernedLoopGraphMutationInput(
            "replace-browser-graph",
            GovernedLoopGraphMutationKind.ReplaceDraft,
            published.GraphId,
            GovernedLoopRevisionLifecycleStatus.Published,
            3,
            draft,
            pin,
            BrowserGraphCandidate(new ContextualRoleRevisionPin(
                new ContextualRoleRevisionIdentity("default-assistant", 1),
                new string('d', 64)))));

        Assert.Same(published, disableTarget);
        Assert.Same(published, archiveTarget);
        Assert.Same(draft, replaceTarget);
    }

    [Fact]
    public async Task Agent_runtime_cancellation_converges_a_real_published_human_input_request_through_the_public_facades()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(workspace.RootPath);
        var fixture = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext(
            "runtime-human-input-cancellation",
            workspaceId,
            DateTimeOffset.UtcNow.AddMinutes(-1).AddSeconds(-30));
        var running = fixture.RunningRun with
        {
            Events =
            [
                .. fixture.RunningRun.Events,
                new CustomLoopRunEvent(3, "runtime-human-input-running", fixture.RunningRun.UpdatedAtUtc, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null),
            ],
        };
        var waiting = fixture.Run with
        {
            Events = [.. running.Events, fixture.Run.Events[^1] with { Sequence = 4 }],
        };
        var context = fixture with { RunningRun = running, Run = waiting };
        using (var runs = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await runs.CreateAsync(context.AdmittedRun)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runs.UpdateAsync(context.RunningRun, context.AdmittedRun.LifecycleVersion)).Status);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runs.UpdateAsync(context.Run, context.RunningRun.LifecycleVersion)).Status);
        }

        var requests = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var publication = new HumanInputRequestPublicationService(
            new CustomLoopRunStore(paths),
            requests,
            new HumanInputRequestPublicationHostGrantResolver(context.Grant, DateTimeOffset.UtcNow),
            new CapabilityAuthorityTransaction(paths),
            workspaceId);
        var published = await publication.PublishAsync(new HumanInputRequestPublicationRequest(
            context.Run.Id,
            context.Checkpoint.Binding.CheckpointId,
            context.Checkpoint.CheckpointHash));

        Assert.Equal(HumanInputRequestPublicationStatus.Published, published.Status);
        var authority = new HumanInputRuntimeFacadeTestAuthorityProvider();
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: authority);
        var input = new LoopRunControlInput(context.Run.Id, context.Run.LifecycleVersion, "runtime-human-input-cancel");
        var pending = Assert.IsType<HumanInputRequestPosture>((await runtime.HumanInput.ReadAsync(context.Checkpoint.Request.RequestId)).Request);

        var cancelled = await runtime.CancelCustomLoopAsync(input);
        var page = await runtime.HumanInput.ListAsync(new HumanInputRequestPosturePageRequest(32));
        var read = await runtime.HumanInput.ReadAsync(context.Checkpoint.Request.RequestId);
        var replayed = await runtime.CancelCustomLoopAsync(input);
        var terminal = Assert.IsType<HumanInputRequestPosture>(read.Request);
        var staleResponse = await runtime.HumanInput.SubmitResponseAsync(new HumanInputResponseOperationInput(
            "runtime-human-input-stale-response",
            HumanInputResponseOperationKind.Submit,
            pending.RequestId,
            pending.LifecycleVersion,
            pending.Status,
            pending.CurrentRequest,
            "runtime-human-input-stale-response",
            new HumanInputResponseValue(HumanInputResponseKind.Confirmation, null, null, true, null, null),
            null));
        var persisted = await requests.ReadAsync(context.Checkpoint.Request.RequestId);

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Cancelled", cancelled.Run!.Status);
        Assert.Equal(HumanInputRequestPosturePageStatus.Ready, page.Status);
        Assert.DoesNotContain(page.Requests, item => item.Status == HumanInputRequestLifecycleStatus.Pending);
        Assert.Equal(HumanInputRequestPostureReadStatus.Ready, read.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, terminal.Status);
        Assert.Equal("Cancelled", replayed.Status);
        Assert.Equal(HumanInputOperationStatus.Conflict, staleResponse.Status);
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(persisted.PrimarySnapshot);
        Assert.Equal(2, snapshot.Operations.Count);
        Assert.Equal(1, snapshot.Operations.Count(item => item.Kind == HumanInputRequestLifecycleOperationKind.Create));
        Assert.Equal(1, snapshot.Operations.Count(item => item.Kind == HumanInputRequestLifecycleOperationKind.Cancel));
    }

    [Fact]
    public async Task Human_input_facade_projects_canonical_redacted_posture_with_stable_paging_and_default_mutation_unavailability()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var pending = CreateFreshHumanInputMutation(workspace.RootPath, "request-pending", "version-pending", "create-pending", HumanInputRequestStoreTestData.HashA, 0, "Display-safe prompt.");
        var cancelled = CreateFreshHumanInputMutation(workspace.RootPath, "request-cancelled", "version-cancelled", "create-cancelled", HumanInputRequestStoreTestData.HashB, 1);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(pending)).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(cancelled)).Status);
        var cancelledHead = Assert.IsType<HumanInputRequestLifecycleHead>(cancelled.PrimaryHeadToWrite);
        var cancelledRequest = Assert.IsType<HumanInputRequest>(cancelled.RequestToAppend);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(
            CreateFreshTerminalHumanInputMutation(
                HumanInputRequestLifecycleOperationKind.Cancel,
                cancelledRequest,
                cancelledHead,
                2,
                "cancel-canonical",
                HumanInputRequestStoreTestData.HashC))).Status);
        var expired = CreateFreshHumanInputMutation(workspace.RootPath, "request-expired", "version-expired", "create-expired", HumanInputRequestStoreTestData.HashA, 3);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(expired)).Status);
        var expiredHead = Assert.IsType<HumanInputRequestLifecycleHead>(expired.PrimaryHeadToWrite);
        var expiredRequest = Assert.IsType<HumanInputRequest>(expired.RequestToAppend);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(
            CreateFreshTerminalHumanInputMutation(
                HumanInputRequestLifecycleOperationKind.Expire,
                expiredRequest,
                expiredHead,
                4,
                "expire-canonical",
                HumanInputRequestStoreTestData.HashB))).Status);
        var superseded = CreateFreshHumanInputMutation(workspace.RootPath, "request-superseded", "version-superseded", "create-superseded", HumanInputRequestStoreTestData.HashC, 5);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(superseded)).Status);
        var supersededHead = Assert.IsType<HumanInputRequestLifecycleHead>(superseded.PrimaryHeadToWrite);
        var supersededRequest = Assert.IsType<HumanInputRequest>(superseded.RequestToAppend);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(
            HumanInputRequestStoreTestData.SupersedeMutation(supersededRequest, supersededHead, 6, "supersede-canonical", HumanInputRequestStoreTestData.HashA))).Status);

        await using (var noAuthorityRuntime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web))
        {
            var inspection = await noAuthorityRuntime.HumanInput.ListAsync(new HumanInputRequestPosturePageRequest(1));
            var observed = Assert.Single(inspection.Requests);
            var unavailable = await noAuthorityRuntime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
                "cancel-without-provider",
                HumanInputRequestLifecycleOperationKind.Cancel,
                observed.RequestId,
                observed.LifecycleVersion,
                observed.Status,
                observed.CurrentRequest,
                null,
                "cancel requested"));
            var unavailableResponse = await noAuthorityRuntime.HumanInput.SubmitResponseAsync(new HumanInputResponseOperationInput(
                "response-without-provider",
                HumanInputResponseOperationKind.Submit,
                observed.RequestId,
                observed.LifecycleVersion,
                observed.Status,
                observed.CurrentRequest,
                "response-without-provider",
                new HumanInputResponseValue(HumanInputResponseKind.Text, "untrusted-value", null, null, null, null),
                null));

            Assert.Equal(HumanInputRequestPosturePageStatus.Ready, inspection.Status);
            Assert.Equal(HumanInputOperationStatus.Unavailable, unavailable.Status);
            Assert.Equal(HumanInputOperationStatus.Unavailable, unavailableResponse.Status);
        }

        var provider = new HumanInputRuntimeFacadeTestAuthorityProvider();
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: provider);
        var defaultPage = await runtime.HumanInput.ListAsync();
        var first = await runtime.HumanInput.ListAsync(new HumanInputRequestPosturePageRequest(1));
        var second = await runtime.HumanInput.ListAsync(new HumanInputRequestPosturePageRequest(1, first.NextCursor));
        var nullRequest = await runtime.HumanInput.ListAsync(null);
        var malformed = await runtime.HumanInput.ListAsync(new HumanInputRequestPosturePageRequest(1, "not-a-canonical-cursor"));
        var malformedRead = await runtime.HumanInput.ReadAsync(null!);
        var missingRead = await runtime.HumanInput.ReadAsync("request-does-not-exist");
        var pendingRead = await runtime.HumanInput.ReadAsync("request-pending");
        var serialized = System.Text.Json.JsonSerializer.Serialize(pendingRead);

        Assert.Equal(HumanInputRequestPosturePageStatus.Ready, first.Status);
        Assert.Equal(HumanInputRequestPosturePageStatus.Ready, defaultPage.Status);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(HumanInputRequestPosturePageStatus.Ready, second.Status);
        Assert.NotEqual(first.Requests[0].RequestId, second.Requests[0].RequestId);
        Assert.Equal(HumanInputRequestPosturePageStatus.Invalid, nullRequest.Status);
        Assert.Equal(HumanInputRequestPosturePageStatus.Invalid, malformed.Status);
        Assert.Equal(HumanInputRequestPostureReadStatus.Invalid, malformedRead.Status);
        Assert.Equal(HumanInputRequestPostureReadStatus.NotFound, missingRead.Status);
        Assert.Contains("Display-safe prompt.", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("route-one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("role-one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace-sha256", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("user-one", serialized, StringComparison.Ordinal);

        var allPosture = await runtime.HumanInput.ListAsync(new HumanInputRequestPosturePageRequest(64));
        Assert.Contains(allPosture.Requests, request => request.Status == HumanInputRequestLifecycleStatus.Pending);
        Assert.Contains(allPosture.Requests, request => request.Status == HumanInputRequestLifecycleStatus.Cancelled);
        Assert.Contains(allPosture.Requests, request => request.Status == HumanInputRequestLifecycleStatus.Expired);
        Assert.Contains(allPosture.Requests, request => request.Status == HumanInputRequestLifecycleStatus.Superseded);

        var added = CreateFreshHumanInputMutation(workspace.RootPath, "request-stales-page", "version-stales-page", "create-stales-page", HumanInputRequestStoreTestData.HashB, 7);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(added)).Status);
        var stale = await runtime.HumanInput.ListAsync(new HumanInputRequestPosturePageRequest(1, first.NextCursor));

        Assert.Equal(HumanInputRequestPosturePageStatus.Stale, stale.Status);
    }

    [Fact]
    public async Task Human_input_facade_uses_server_authority_for_exact_response_and_lifecycle_operations_with_replay_conflict_and_cancellation_boundaries()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));
        var pending = CreateFreshHumanInputMutation(workspace.RootPath, "request-answer", "version-answer", "create-answer", HumanInputRequestStoreTestData.HashA);
        var cancellable = CreateFreshHumanInputMutation(workspace.RootPath, "request-cancel", "version-cancel", "create-cancel", HumanInputRequestStoreTestData.HashB, 1);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(pending)).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(cancellable)).Status);
        var provider = new HumanInputRuntimeFacadeTestAuthorityProvider();
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: provider);
        var answerPosture = Assert.IsType<HumanInputRequestPosture>((await runtime.HumanInput.ReadAsync("request-answer")).Request);
        var invalidLifecycle = await runtime.HumanInput.SubmitLifecycleAsync(null);
        var invalidResponse = await runtime.HumanInput.SubmitResponseAsync(null);
        var missingTarget = await runtime.HumanInput.SubmitResponseAsync(new HumanInputResponseOperationInput(
            "withdraw-missing-response",
            HumanInputResponseOperationKind.Withdraw,
            answerPosture.RequestId,
            answerPosture.LifecycleVersion,
            answerPosture.Status,
            answerPosture.CurrentRequest,
            "response-missing",
            null,
            null));
        var missingRequestReference = answerPosture.CurrentRequest with { RequestId = "request-does-not-exist" };
        var missingRequest = await runtime.HumanInput.SubmitResponseAsync(new HumanInputResponseOperationInput(
            "submit-missing-request",
            HumanInputResponseOperationKind.Submit,
            missingRequestReference.RequestId,
            answerPosture.LifecycleVersion,
            answerPosture.Status,
            missingRequestReference,
            "response-missing-request",
            new HumanInputResponseValue(HumanInputResponseKind.Text, "private-missing-request-value", null, null, null, null),
            null));
        var submit = new HumanInputResponseOperationInput(
            "submit-canonical-response",
            HumanInputResponseOperationKind.Submit,
            answerPosture.RequestId,
            answerPosture.LifecycleVersion,
            answerPosture.Status,
            answerPosture.CurrentRequest,
            "response-canonical",
            new HumanInputResponseValue(HumanInputResponseKind.Text, "private-response-value", null, null, null, null),
            "private-response-explanation");

        var invalidResponseValue = await runtime.HumanInput.SubmitResponseAsync(submit with
        {
            OperationId = "submit-oversized-response",
            Value = new HumanInputResponseValue(HumanInputResponseKind.Text, new string('x', HumanInputLimits.MaxResponseTextCharacters + 1), null, null, null, null)
        });
        var committed = await runtime.HumanInput.SubmitResponseAsync(submit);
        var mismatchedRequestId = await runtime.HumanInput.SubmitResponseAsync(submit with
        {
            OperationId = "submit-mismatched-request-id",
            RequestId = "different-request-id"
        });
        var replayed = await runtime.HumanInput.SubmitResponseAsync(submit);
        var changed = await runtime.HumanInput.SubmitResponseAsync(submit with
        {
            Value = new HumanInputResponseValue(HumanInputResponseKind.Text, "changed-private-response-value", null, null, null, null)
        });
        var persistedResponses = await ((IHumanInputResponseLifecycleStore)store).ReadAsync(answerPosture.CurrentRequest);
        var responseCommandHash = Assert.Single(
            Assert.IsType<HumanInputResponseLifecycleStoreSnapshot>(persistedResponses.Snapshot).Operations,
            operation => operation.OperationId == submit.OperationId).CommandHash;
        var stale = await runtime.HumanInput.SubmitResponseAsync(submit with { OperationId = "submit-stale-response" });
        var publicResultJson = System.Text.Json.JsonSerializer.Serialize(new[] { committed, replayed, changed, stale });

        Assert.Equal(HumanInputOperationStatus.Committed, committed.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidLifecycle.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidResponse.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidResponseValue.Status);
        Assert.Equal(HumanInputOperationStatus.NotFound, missingTarget.Status);
        Assert.Equal(HumanInputOperationStatus.NotFound, missingRequest.Status);
        Assert.Equal(HumanInputOperationStatus.Conflict, mismatchedRequestId.Status);
        Assert.NotNull(committed.Evidence);
        Assert.True(committed.Request!.IsAnswered);
        Assert.Equal(HumanInputOperationStatus.Replayed, replayed.Status);
        Assert.Equal(committed.OperationId, replayed.OperationId);
        Assert.Equal(committed.Evidence.OperationId, replayed.Evidence!.OperationId);
        Assert.Equal(HumanInputOperationStatus.Conflict, changed.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Answered, changed.Request!.Status);
        Assert.Null(changed.Request.LatestConflict);
        Assert.Equal(HumanInputOperationStatus.Conflict, stale.Status);
        Assert.NotNull(stale.Evidence);
        Assert.Equal(HumanInputRequestLifecycleStatus.Answered, stale.Request!.Status);
        Assert.Null(stale.Request.LatestConflict);
        Assert.DoesNotContain("private-response-value", publicResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("changed-private-response-value", publicResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-explanation", publicResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain(responseCommandHash, publicResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("user-one", publicResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("role-one", publicResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("route-one", publicResultJson, StringComparison.Ordinal);

        var cancelPosture = Assert.IsType<HumanInputRequestPosture>((await runtime.HumanInput.ReadAsync("request-cancel")).Request);
        var cancel = new HumanInputLifecycleOperationInput(
            "cancel-canonical-request",
            HumanInputRequestLifecycleOperationKind.Cancel,
            cancelPosture.RequestId,
            cancelPosture.LifecycleVersion,
            cancelPosture.Status,
            cancelPosture.CurrentRequest,
            null,
            "cancel requested");
        var missingLifecycleBinding = await runtime.HumanInput.SubmitLifecycleAsync(cancel with
        {
            OperationId = "cancel-missing-request",
            RequestId = "request-does-not-exist",
            ExpectedRequest = cancelPosture.CurrentRequest with { RequestId = "request-does-not-exist" }
        });
        var mismatchedLifecycleRequest = await runtime.HumanInput.SubmitLifecycleAsync(cancel with
        {
            OperationId = "cancel-mismatched-request",
            RequestId = "different-request-id"
        });
        var invalidLifecycleRequestId = await runtime.HumanInput.SubmitLifecycleAsync(cancel with
        {
            OperationId = "cancel-invalid-request-id",
            RequestId = string.Empty
        });
        var invalidLifecycleOperation = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { OperationId = string.Empty });
        var invalidLifecycleReason = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { OperationId = "cancel-invalid-reason", Reason = string.Empty });
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Denied;
        var deniedTerms = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { OperationId = "cancel-terms-denied" });
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Unavailable;
        var unavailableTerms = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { OperationId = "cancel-terms-unavailable" });
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Ready;
        provider.ThrowDuringLifecycleTerms = true;
        var thrownTerms = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { OperationId = "cancel-terms-throws" });
        provider.ThrowDuringLifecycleTerms = false;
        provider.LifecycleAuthorizationStatus = AgentRuntimeHumanInputAuthorityStatus.Denied;
        var deniedLifecycle = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { OperationId = "cancel-authorization-denied" });
        provider.LifecycleAuthorizationStatus = AgentRuntimeHumanInputAuthorityStatus.Unavailable;
        var unavailableLifecycle = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { OperationId = "cancel-authorization-unavailable" });
        provider.LifecycleAuthorizationStatus = AgentRuntimeHumanInputAuthorityStatus.Ready;
        var cancelled = await runtime.HumanInput.SubmitLifecycleAsync(cancel);
        var cancelReplay = await runtime.HumanInput.SubmitLifecycleAsync(cancel);
        var changedCancel = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { Reason = "different cancel reason" });
        var staleCancel = await runtime.HumanInput.SubmitLifecycleAsync(cancel with { OperationId = "cancel-stale-request" });
        provider.LifecycleAuthorizationStatus = AgentRuntimeHumanInputAuthorityStatus.Denied;
        var deniedReplay = await runtime.HumanInput.SubmitLifecycleAsync(cancel);
        provider.LifecycleAuthorizationStatus = AgentRuntimeHumanInputAuthorityStatus.Ready;
        provider.UseActor("user-two");
        var differentActorReplay = await runtime.HumanInput.SubmitLifecycleAsync(cancel);
        provider.UseActor("user-one");
        var cancelConflictRead = await runtime.HumanInput.ReadAsync(cancelPosture.RequestId);
        var cancelConflictPage = await runtime.HumanInput.ListAsync(new HumanInputRequestPosturePageRequest(64));

        Assert.Equal(HumanInputOperationStatus.Committed, cancelled.Status);
        Assert.Equal(HumanInputOperationStatus.NotFound, missingLifecycleBinding.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, mismatchedLifecycleRequest.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidLifecycleRequestId.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidLifecycleOperation.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalidLifecycleReason.Status);
        Assert.Equal(HumanInputOperationStatus.Denied, deniedTerms.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, unavailableTerms.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, thrownTerms.Status);
        Assert.Equal(HumanInputOperationStatus.Denied, deniedLifecycle.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, unavailableLifecycle.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, cancelled.Request!.Status);
        Assert.Equal(HumanInputOperationStatus.Replayed, cancelReplay.Status);
        Assert.Equal(HumanInputOperationStatus.Denied, deniedReplay.Status);
        Assert.Equal(HumanInputOperationStatus.Denied, differentActorReplay.Status);
        Assert.Equal(HumanInputOperationStatus.Conflict, changedCancel.Status);
        Assert.Equal(HumanInputOperationStatus.Conflict, staleCancel.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, staleCancel.Request!.Status);
        Assert.Equal(staleCancel.OperationId, staleCancel.Request.LatestConflict!.OperationId);
        Assert.Equal("Lifecycle", staleCancel.Request.LatestConflict.OperationFamily);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, cancelConflictRead.Request!.Status);
        Assert.Equal(staleCancel.OperationId, cancelConflictRead.Request.LatestConflict!.OperationId);
        var conflictFromPage = Assert.Single(cancelConflictPage.Requests, request => request.RequestId == cancelPosture.RequestId);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, conflictFromPage.Status);
        Assert.Equal(staleCancel.OperationId, conflictFromPage.LatestConflict!.OperationId);
        Assert.True(provider.ResponseAuthentications >= 2);
        Assert.True(provider.LifecycleAuthorizations >= 1);

        provider.ResponseAuthenticationStatus = AgentRuntimeHumanInputAuthorityStatus.Denied;
        var denied = await runtime.HumanInput.SubmitResponseAsync(submit with { OperationId = "submit-denied-response" });
        provider.ResponseAuthenticationStatus = AgentRuntimeHumanInputAuthorityStatus.Unavailable;
        var unavailable = await runtime.HumanInput.SubmitResponseAsync(submit with { OperationId = "submit-unavailable-response" });
        var invalid = await runtime.HumanInput.SubmitResponseAsync(submit with { OperationId = string.Empty });

        Assert.Equal(HumanInputOperationStatus.Denied, denied.Status);
        Assert.Equal(HumanInputOperationStatus.Unavailable, unavailable.Status);
        Assert.Equal(HumanInputOperationStatus.Invalid, invalid.Status);

        provider.DelayLifecycleTermsUntilCancellation = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.HumanInput.SubmitLifecycleAsync(
            cancel with { OperationId = "cancel-before-intent" },
            cancellation.Token));
        var unchanged = await runtime.HumanInput.ReadAsync("request-cancel");

        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, unchanged.Request!.Status);
    }

    [Fact]
    public async Task Human_input_facade_replays_durable_grant_and_candidate_operations_without_current_lifecycle_terms()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath));

        var remindCreate = CreateFreshHumanInputMutation(workspace.RootPath, "request-replay-remind", "version-replay-remind", "create-replay-remind", HumanInputRequestStoreTestData.HashA);
        var remindRequest = Assert.IsType<HumanInputRequest>(remindCreate.RequestToAppend);
        var remindHead = Assert.IsType<HumanInputRequestLifecycleHead>(remindCreate.PrimaryHeadToWrite);
        var remind = CreateDurableLifecycleReplayMutation(
            HumanInputRequestLifecycleOperationKind.Remind,
            remindRequest,
            remindHead,
            1,
            "replay-after-expired-grant",
            null);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(remindCreate)).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(remind)).Status);

        var amendCreate = CreateFreshHumanInputMutation(workspace.RootPath, "request-replay-amend", "version-replay-amend", "create-replay-amend", HumanInputRequestStoreTestData.HashB, 2);
        var amendRequest = Assert.IsType<HumanInputRequest>(amendCreate.RequestToAppend);
        var amendHead = Assert.IsType<HumanInputRequestLifecycleHead>(amendCreate.PrimaryHeadToWrite);
        var persistedCandidate = HumanInputRequestStoreTestData.Rehash(amendRequest with
        {
            RequestVersionId = "version-replay-amend-persisted",
            Prompt = "Private persisted replacement prompt."
        });
        var amend = CreateDurableLifecycleReplayMutation(
            HumanInputRequestLifecycleOperationKind.Amend,
            amendRequest,
            amendHead,
            3,
            "replay-after-terms-rotate",
            persistedCandidate);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(amendCreate)).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(amend)).Status);

        var supersedeCreate = CreateFreshHumanInputMutation(workspace.RootPath, "request-replay-supersede", "version-replay-supersede", "create-replay-supersede", HumanInputRequestStoreTestData.HashC, 4);
        var supersedeRequest = Assert.IsType<HumanInputRequest>(supersedeCreate.RequestToAppend);
        var supersedeHead = Assert.IsType<HumanInputRequestLifecycleHead>(supersedeCreate.PrimaryHeadToWrite);
        var persistedSupersedeCandidate = HumanInputRequestStoreTestData.Request(
            "request-replay-supersede-related",
            "version-replay-supersede-related",
            supersedeHead.UpdatedAtUtc.AddSeconds(1),
            supersedeRequest.Binding,
            prompt: "Private persisted supersede prompt.");
        var supersede = CreateDurableLifecycleReplayMutation(
            HumanInputRequestLifecycleOperationKind.Supersede,
            supersedeRequest,
            supersedeHead,
            5,
            "replay-supersede-related-candidate",
            persistedSupersedeCandidate);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(supersedeCreate)).Status);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(supersede)).Status);

        var provider = new HumanInputRuntimeFacadeTestAuthorityProvider
        {
            LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Unavailable
        };
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, humanInputAuthorityProvider: provider);
        var replayReason = "Replay one exact lifecycle operation.";
        var remindInput = new HumanInputLifecycleOperationInput(
            "replay-after-expired-grant",
            HumanInputRequestLifecycleOperationKind.Remind,
            remindRequest.RequestId,
            remindHead.LifecycleVersion,
            remindHead.Status,
            HumanInputRequestStoreTestData.Reference(remindRequest),
            null,
            replayReason);

        var grantReplay = await runtime.HumanInput.SubmitLifecycleAsync(remindInput);
        provider.LifecycleAuthorizationStatus = AgentRuntimeHumanInputAuthorityStatus.Denied;
        var deniedReplay = await runtime.HumanInput.SubmitLifecycleAsync(remindInput);
        provider.LifecycleAuthorizationStatus = AgentRuntimeHumanInputAuthorityStatus.Ready;
        provider.UseActor("user-two");
        var differentActorReplay = await runtime.HumanInput.SubmitLifecycleAsync(remindInput);
        provider.UseActor("user-one");

        var rotatedCandidate = HumanInputRequestStoreTestData.Rehash(amendRequest with
        {
            RequestVersionId = "version-replay-amend-rotated",
            Prompt = "Private rotated replacement prompt."
        });
        provider.LifecycleTermsStatus = AgentRuntimeHumanInputAuthorityStatus.Ready;
        provider.LifecycleCandidateRequest = rotatedCandidate;
        provider.LifecycleGrantReference = CreateReplayGrantReference("grant-rotated");
        var candidateReplay = await runtime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
            "replay-after-terms-rotate",
            HumanInputRequestLifecycleOperationKind.Amend,
            amendRequest.RequestId,
            amendHead.LifecycleVersion,
            amendHead.Status,
            HumanInputRequestStoreTestData.Reference(amendRequest),
            "rotated-selector",
            replayReason));
        var supersedeReplay = await runtime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
            "replay-supersede-related-candidate",
            HumanInputRequestLifecycleOperationKind.Supersede,
            supersedeRequest.RequestId,
            supersedeHead.LifecycleVersion,
            supersedeHead.Status,
            HumanInputRequestStoreTestData.Reference(supersedeRequest),
            "rotated-selector",
            replayReason));

        Assert.Equal(HumanInputOperationStatus.Replayed, grantReplay.Status);
        Assert.Equal(HumanInputOperationStatus.Denied, deniedReplay.Status);
        Assert.Equal(HumanInputOperationStatus.Denied, differentActorReplay.Status);
        Assert.Equal(HumanInputOperationStatus.Replayed, candidateReplay.Status);
        Assert.Equal(persistedCandidate.RequestVersionId, candidateReplay.Request!.CurrentRequest.RequestVersionId);
        Assert.Equal(HumanInputOperationStatus.Replayed, supersedeReplay.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Superseded, supersedeReplay.Request!.Status);
        Assert.Equal(0, provider.LifecycleTermsResolutions);
        Assert.Equal(5, provider.LifecycleAuthorizations);
    }

    [Fact]
    public async Task Human_input_facade_preserves_closed_catalog_dispositions_before_lifecycle_binding_resolution()
    {
        using (var unavailableWorkspace = new TestWorkspace())
        {
            await WorkspaceInitializer.ForFileCapabilityTrustRoot(unavailableWorkspace.ServerStatePath).InitializeAsync(unavailableWorkspace.RootPath);
            var paths = new WorkspacePaths(unavailableWorkspace.RootPath);
            var store = new HumanInputRequestStore(paths, new FileCapabilityCatalogTrustProvider(unavailableWorkspace.ServerStatePath));
            var mutation = CreateFreshHumanInputMutation(unavailableWorkspace.RootPath, "unavailable-catalog-request", "unavailable-catalog-version", "unavailable-catalog-create", HumanInputRequestStoreTestData.HashA);
            Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Committed, (await store.CommitAsync(mutation)).Status);
            await File.WriteAllTextAsync(Path.Combine(paths.AgentPath, "human-input", "requests", "lifecycle.json"), "not an authenticated document");
            await File.WriteAllTextAsync(Path.Combine(paths.AgentPath, "human-input", "requests", "lifecycle.proved.json"), "not an authenticated document");
            var request = Assert.IsType<HumanInputRequest>(mutation.RequestToAppend);
            var head = Assert.IsType<HumanInputRequestLifecycleHead>(mutation.PrimaryHeadToWrite);
            await using var runtime = await CreateRuntimeAsync(
                unavailableWorkspace,
                AgentRuntimeSurface.Web,
                humanInputAuthorityProvider: new HumanInputRuntimeFacadeTestAuthorityProvider());

            var result = await runtime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
                "unavailable-catalog-operation",
                HumanInputRequestLifecycleOperationKind.Cancel,
                request.RequestId,
                head.LifecycleVersion,
                head.Status,
                HumanInputRequestStoreTestData.Reference(request),
                null,
                "cancel request"));
            var page = await runtime.HumanInput.ListAsync();
            var read = await runtime.HumanInput.ReadAsync(request.RequestId);
            var response = await runtime.HumanInput.SubmitResponseAsync(new HumanInputResponseOperationInput(
                "unavailable-catalog-response",
                HumanInputResponseOperationKind.Submit,
                request.RequestId,
                head.LifecycleVersion,
                head.Status,
                HumanInputRequestStoreTestData.Reference(request),
                "unavailable-catalog-response",
                new HumanInputResponseValue(HumanInputResponseKind.Text, "private-response", null, null, null, null),
                null));

            Assert.Equal(HumanInputOperationStatus.Unavailable, result.Status);
            Assert.Equal(HumanInputRequestPosturePageStatus.Unavailable, page.Status);
            Assert.Equal(HumanInputRequestPostureReadStatus.Unavailable, read.Status);
            Assert.Equal(HumanInputOperationStatus.Unavailable, response.Status);
        }

        using var ambiguousWorkspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(ambiguousWorkspace.ServerStatePath).InitializeAsync(ambiguousWorkspace.RootPath);
        var ambiguousPaths = new WorkspacePaths(ambiguousWorkspace.RootPath);
        var interruptedStore = new HumanInputRequestStore(
            ambiguousPaths,
            new FileCapabilityCatalogTrustProvider(ambiguousWorkspace.ServerStatePath),
            new HumanInputRequestStoreOptions
            {
                DurableBoundaryObserver = (boundary, _) => boundary == HumanInputRequestPersistenceBoundary.PrimaryPublished
                    ? ValueTask.FromException(new IOException("Simulated interruption after primary publication."))
                    : ValueTask.CompletedTask,
            });
        var interrupted = CreateFreshHumanInputMutation(ambiguousWorkspace.RootPath, "ambiguous-catalog-request", "ambiguous-catalog-version", "ambiguous-catalog-create", HumanInputRequestStoreTestData.HashB);
        Assert.Equal(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous, (await interruptedStore.CommitAsync(interrupted)).Status);
        var interruptedRequest = Assert.IsType<HumanInputRequest>(interrupted.RequestToAppend);
        var interruptedHead = Assert.IsType<HumanInputRequestLifecycleHead>(interrupted.PrimaryHeadToWrite);
        await using var ambiguousRuntime = await CreateRuntimeAsync(
            ambiguousWorkspace,
            AgentRuntimeSurface.Web,
            humanInputAuthorityProvider: new HumanInputRuntimeFacadeTestAuthorityProvider());

        var ambiguous = await ambiguousRuntime.HumanInput.SubmitLifecycleAsync(new HumanInputLifecycleOperationInput(
            "ambiguous-catalog-operation",
            HumanInputRequestLifecycleOperationKind.Cancel,
            interruptedRequest.RequestId,
            interruptedHead.LifecycleVersion,
            interruptedHead.Status,
            HumanInputRequestStoreTestData.Reference(interruptedRequest),
            null,
            "cancel request"));
        var ambiguousPage = await ambiguousRuntime.HumanInput.ListAsync();
        var ambiguousRead = await ambiguousRuntime.HumanInput.ReadAsync(interruptedRequest.RequestId);
        var ambiguousResponse = await ambiguousRuntime.HumanInput.SubmitResponseAsync(new HumanInputResponseOperationInput(
            "ambiguous-catalog-response",
            HumanInputResponseOperationKind.Submit,
            interruptedRequest.RequestId,
            interruptedHead.LifecycleVersion,
            interruptedHead.Status,
            HumanInputRequestStoreTestData.Reference(interruptedRequest),
            "ambiguous-catalog-response",
            new HumanInputResponseValue(HumanInputResponseKind.Text, "private-response", null, null, null, null),
            null));

        Assert.Equal(HumanInputOperationStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(HumanInputRequestPosturePageStatus.Ambiguous, ambiguousPage.Status);
        Assert.Equal(HumanInputRequestPostureReadStatus.Ambiguous, ambiguousRead.Status);
        Assert.Equal(HumanInputOperationStatus.Ambiguous, ambiguousResponse.Status);
    }

    [Fact]
    public async Task CreateAsync_starts_with_fresh_transcript_without_exposing_runtime_internals()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await new ConversationMemoryStore(paths).AppendMessageAsync(LlmMessage.User("old transcript"));

        await using var runtime = await CreateRuntimeWithLiveDiscoveryAsync(workspace);

        Assert.Equal(string.Empty, await File.ReadAllTextAsync(paths.CurrentConversationPath));
        Assert.NotEmpty(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
        Assert.True(File.Exists(paths.ConversationTurnLockPath));
        Assert.Equal(CodexRuntimeCompatibility.Compatible, runtime.CodexRuntimeStatus.Compatibility);
        Assert.Equal("codex-cli 999.0.0-test", runtime.CodexRuntimeStatus.Version);
        Assert.Equal("explicit --codex-path", runtime.CodexRuntimeStatus.Source);
    }

    [Fact]
    public async Task CreateAsync_surfaces_actionable_cleanup_without_rewriting_a_superseded_identityless_transcript()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var legacyEntry = """{"schemaVersion":1,"conversationId":"current","sequence":1,"timestampUtc":"2026-07-31T00:00:00Z","role":"user","content":"legacy prompt"}""";
        await File.WriteAllTextAsync(paths.CurrentConversationPath, legacyEntry);

        var exception = await Assert.ThrowsAsync<ConversationTranscriptCleanupRequiredException>(() => CreateRuntimeAsync(workspace));

        Assert.Equal(paths.CurrentConversationPath, exception.TranscriptPath);
        Assert.Contains("start EmbodySense again", exception.Message, StringComparison.Ordinal);
        Assert.Equal(legacyEntry, await File.ReadAllTextAsync(paths.CurrentConversationPath));
    }

    [Fact]
    public async Task Trigger_worker_created_by_runtime_rereads_current_authority_and_cannot_capture_a_prior_grant()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidencePath = Path.Combine(workspace.RootPath, "current-trigger-authority.txt");
        await File.WriteAllTextAsync(evidencePath, "Authorized");
        var authorizer = new FileCurrentTriggerEvidenceAuthorizer(evidencePath);
        var worker = runtime.CreateTriggerWorkerRuntime(authorizer, new FixedTriggerTimeProvider(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4)));
        var envelope = TriggerWorkerTestData.Envelope();
        var store = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime);
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, envelope.Adapter, true, envelope.ActorContext, envelope.Authority, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(3), out var delivery, out _));
        var admission = await new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(store), store).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(delivery!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal));
        var generation = (await store.GetSnapshotAsync(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4))).Generation;
        await File.WriteAllTextAsync(evidencePath, "Rejected");

        var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput("worker-1", generation, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4), TimeSpan.FromSeconds(30), [], 2));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admission.Status);
        Assert.Equal(1, authorizer.Reads);
        Assert.Equal("DispatchRejected", result.Entry!.State);
        Assert.Equal("Rejected", result.Entry.DispatchOutcome);
        var durable = Assert.Single((await worker.GetSnapshotAsync(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4))).Entries);
        Assert.Equal(result.Entry.DeliveryId, durable.DeliveryId);
        Assert.Equal("DispatchRejected", durable.State);
    }

    [Fact]
    public async Task Trigger_worker_uses_runtime_owned_custom_loop_gate_for_proved_not_found_rejection()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var evidencePath = Path.Combine(workspace.RootPath, "current-trigger-authority.txt");
        await File.WriteAllTextAsync(evidencePath, "Authorized");
        var authorizer = new FileCurrentTriggerEvidenceAuthorizer(evidencePath);
        var worker = runtime.CreateTriggerWorkerRuntime(authorizer, new FixedTriggerTimeProvider(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4)));
        var envelope = TriggerWorkerTestData.Envelope();
        var store = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime);
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, envelope.Adapter, true, envelope.ActorContext, envelope.Authority, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(3), out var delivery, out _));
        await new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(store), store).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(delivery!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal));
        var generation = (await store.GetSnapshotAsync(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4))).Generation;

        var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput("worker-1", generation, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4), TimeSpan.FromSeconds(30), [], 2));

        Assert.Equal(1, authorizer.Reads);
        Assert.Equal("DispatchRejected", result.Entry!.State);
        Assert.Equal("Rejected", result.Entry.DispatchOutcome);
        Assert.Contains("does not exist", result.Entry.DispatchDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trigger_worker_retains_exact_revalidated_identity_but_refuses_ambient_default_role_authority()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var definition = CustomLoopDefinition.CreateSeed("loop-trigger-identity", "operator", "step-trigger-identity", "create-trigger-identity", TriggerWorkerTestData.CreatedAtUtc);
        var definitionStore = new CustomLoopDefinitionStore(paths);
        var created = await definitionStore.CreateAsync(definition);
        var audited = await definitionStore.MarkOperationOutcomeAuditedAsync(definition.LastMutationOperationId);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var systemDefinitionStore = new LoopDefinitionStore(paths);
        var systemDefinition = await systemDefinitionStore.LoadAsync(BuiltInLoopIds.DefaultConversation);
        await systemDefinitionStore.SaveAsync(systemDefinition! with { RoleId = definition.RoleId });
        var evidencePath = Path.Combine(workspace.RootPath, "current-trigger-authority.txt");
        await File.WriteAllTextAsync(evidencePath, "Authorized");
        var authorizer = new FileCurrentTriggerEvidenceAuthorizer(evidencePath);
        var worker = runtime.CreateTriggerWorkerRuntime(authorizer, new FixedTriggerTimeProvider(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4)));
        Assert.True(TriggerDeliveryFactory.TryCreateLoopReference(definition.Id, definition.DefinitionVersion, definition.ContentHash, out var loop, out _));
        Assert.True(AuthorityActorId.TryParse("trigger-owner", out var triggerActor, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(triggerActor, "webhook", "workspace-1", definition.RoleId, out var triggerActorContext, out _));
        var exactTriggerActorContext = triggerActorContext!;
        var envelope = TriggerWorkerTestData.Envelope(loop: loop, actorContext: exactTriggerActorContext);
        var store = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime);
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(envelope, envelope.Loop, envelope.Adapter, true, envelope.ActorContext, envelope.Authority, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(3), out var delivery, out _));
        var admission = await new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(store), store).AdmitAsync(TriggerQueueAdmissionRequestFactory.Create(delivery!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal));
        var generation = (await store.GetSnapshotAsync(TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4))).Generation;

        var result = await worker.RunOnceAsync(new TriggerWorkerSelectionInput("worker-1", generation, TriggerWorkerTestData.CreatedAtUtc.AddSeconds(4), TimeSpan.FromSeconds(30), [], 2));
        var entry = Assert.IsType<TriggerWorkerEntrySnapshot>(result.Entry);

        Assert.Equal(CustomLoopDefinitionStoreStatus.Created, created.Status);
        Assert.Equal(CustomLoopOperationAuditMarkStatus.Marked, audited);
        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admission.Status);
        Assert.Equal("NeedsReview", entry.State);
        Assert.Equal("NeedsReview", entry.DispatchOutcome);
        Assert.Contains("ProviderDispatched=False", entry.DispatchDetail, StringComparison.Ordinal);
        Assert.Null(entry.GovernedRunId);
        var run = await new CustomLoopRunStore(paths).GetByAdmissionOperationAsync(entry.DispatchOperationId!);
        Assert.NotNull(run);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, run.Status);
        Assert.DoesNotContain(run.Events, runEvent => runEvent.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
        Assert.DoesNotContain(run.Events, runEvent => runEvent.Kind is CustomLoopRunEventKind.ToolRequestReserved
            or CustomLoopRunEventKind.ToolGovernanceDecided
            or CustomLoopRunEventKind.ToolOutcomeObserved);
        Assert.Equal(exactTriggerActorContext.ActorId.Value, run!.AdmissionActor);
        Assert.Equal(exactTriggerActorContext.SurfaceId, run.Surface);
        Assert.Equal(exactTriggerActorContext.RoleId, run.AdmittedDefinition.RoleId);
        Assert.Equal(exactTriggerActorContext.ActorId.Value, authorizer.LastInput!.ActorId);
        Assert.Equal(exactTriggerActorContext.SurfaceId, authorizer.LastInput.SurfaceId);
        Assert.Equal(exactTriggerActorContext.RoleId, authorizer.LastInput.RoleId);
        Assert.Equal(envelope.Loop, authorizer.LastInput.Loop);
        Assert.NotEqual(WorkspaceActors.Cli, run.AdmissionActor);
        Assert.NotEqual(AgentRuntimeSurface.Cli.Id, run.Surface);
        Assert.NotEqual("default-assistant", run.AdmittedDefinition.RoleId);
    }

    [Fact]
    public async Task Restarted_trigger_origin_resume_without_canonical_handoff_fails_closed_before_provider_dispatch()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.True(TriggerDeliveryId.TryParse("delivery-trigger-resume-restart", out var deliveryId));
        var operationId = TriggerWorkerRequestHash.ComputeOperationId(deliveryId!, 1);
        var interrupted = TriggerRunningRun("run-trigger-resume-restart", operationId);
        await PersistRunningRunAsync(new CustomLoopRunStore(paths), interrupted);
        var providerMarkerPath = workspace.File("trigger-resume-provider.marker");
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnStartMarkerPath: providerMarkerPath);

        await using var restarted = await CreateRuntimeAsync(workspace, codexPath: codexPath);
        var recovered = Assert.IsType<LoopRunSnapshot>(await restarted.GetCustomLoopRunAsync(interrupted.Id));
        var resumed = await restarted.ResumeCustomLoopAsync(new LoopRunControlInput(recovered.Id, recovered.LifecycleVersion, "resume-trigger-origin-after-restart"));
        var durable = Assert.IsType<CustomLoopRunRecord>(await new CustomLoopRunStore(paths).GetAsync(interrupted.Id));

        Assert.Equal("Paused", recovered.Status);
        Assert.Equal("NeedsReview", resumed.Status);
        Assert.Equal("NeedsReview", resumed.Run!.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, durable.Status);
        Assert.Equal(operationId, durable.AdmissionOperationId);
        Assert.Contains("TriggerOriginCanonicalHandoffRequiredException", resumed.Detail, StringComparison.Ordinal);
        Assert.Contains("TriggerOriginCanonicalHandoffRequiredException", durable.Events[^1].Detail, StringComparison.Ordinal);
        Assert.Null(durable.SequentialAdapterBinding);
        Assert.Null(durable.SequentialInvocationSnapshot);
        Assert.False(File.Exists(providerMarkerPath));
        Assert.DoesNotContain(durable.Events, runEvent => runEvent.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
        Assert.DoesNotContain(durable.Events, runEvent => runEvent.Kind is CustomLoopRunEventKind.ToolRequestReserved
            or CustomLoopRunEventKind.ToolGovernanceDecided
            or CustomLoopRunEventKind.ToolOutcomeObserved);
    }

    [Fact]
    public async Task Restarted_human_legacy_resume_without_canonical_handoff_remains_functional()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var interrupted = await AdmitLegacyRunAsync(workspace, "invoke-human-resume-restart");
        var providerMarkerPath = workspace.File("human-resume-provider.marker");
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnStartMarkerPath: providerMarkerPath);

        await using var restarted = await CreateRuntimeAsync(workspace, codexPath: codexPath);
        var recovered = Assert.IsType<LoopRunSnapshot>(await restarted.GetCustomLoopRunAsync(interrupted.Id));
        var resumed = await restarted.ResumeCustomLoopAsync(new LoopRunControlInput(recovered.Id, recovered.LifecycleVersion, "resume-human-origin-after-restart"));

        Assert.Equal("Paused", recovered.Status);
        Assert.Equal("Completed", resumed.Status);
        Assert.Equal("Completed", resumed.Run!.Status);
        Assert.True(File.Exists(providerMarkerPath));
    }

    [Fact]
    public async Task Human_invocation_rejects_the_reserved_trigger_operation_identity_before_admission()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        Assert.True(TriggerDeliveryId.TryParse("delivery-human-reserved-operation", out var deliveryId));
        var operationId = TriggerWorkerRequestHash.ComputeOperationId(deliveryId!, 1);
        await using var runtime = await CreateRuntimeAsync(workspace);

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput("missing-loop", 1, new string('a', CustomLoopLimits.Sha256HexCharacters), operationId, "must not admit"));

        Assert.Equal("Invalid", response.AdmissionStatus);
        Assert.False(response.WasDispatched);
        Assert.Null(response.Run);
        Assert.Contains("reserved", response.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await new CustomLoopInvocationOperationStore(new WorkspacePaths(workspace.RootPath)).GetAsync(operationId));
        Assert.Null(await new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath)).GetByAdmissionOperationAsync(operationId));
    }

    [Fact]
    public void Agent_runtime_surface_requires_explicit_safe_identifier()
    {
        var web = AgentRuntimeSurface.Create(" web ");
        var custom = AgentRuntimeSurface.Create("editor-panel");

        Assert.Equal("web", web.Id);
        Assert.Equal("web", web.SurfaceId.Id);
        Assert.Equal("editor-panel", custom.Id);
        Assert.Equal("cli", AgentRuntimeSurface.Cli.Id);
        Assert.Throws<ArgumentException>(() => AgentRuntimeSurface.Create(" "));
        Assert.Throws<ArgumentException>(() => AgentRuntimeSurface.Create("web/ui"));
    }

    [Fact]
    public void Workspace_actor_uses_the_canonical_runtime_surface_id()
    {
        Assert.Equal("embodysense.editor-panel", WorkspaceActors.ForSurface(AgentRuntimeSurface.Create(" Editor-Panel ").SurfaceId));
        Assert.Throws<ArgumentNullException>(() => WorkspaceActors.ForSurface(null!));
    }

    [Fact]
    public async Task CreateAsync_requires_explicit_runtime_surface()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var fakeCodex = await CreateFakeCodexExecutableAsync(workspace);

        await Assert.ThrowsAsync<ArgumentNullException>(() => new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            "test-model",
            workspace.RootPath,
            fakeCodex,
            "read-only",
            null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_rejects_missing_models_before_runtime_probing(string? model)
    {
        using var workspace = new TestWorkspace();
        var factory = new AgentRuntimeFactory(new RejectingApprovalPrompt());
        var unavailableExecutable = workspace.File("must-not-be-probed.cmd");

        var freshConversationException = await Assert.ThrowsAnyAsync<ArgumentException>(() => factory.CreateAsync(
            model!,
            workspace.RootPath,
            unavailableExecutable,
            "read-only",
            AgentRuntimeSurface.Cli));
        var preservedConversationException = await Assert.ThrowsAnyAsync<ArgumentException>(() => factory.CreateAsync(
            model!,
            workspace.RootPath,
            unavailableExecutable,
            "read-only",
            AgentRuntimeSurface.Cli,
            preserveCurrentConversation: true));

        Assert.Equal("model", freshConversationException.ParamName);
        Assert.Equal("model", preservedConversationException.ParamName);
    }

    [Fact]
    public async Task CreateAsync_rejects_pre_resolved_status_for_a_different_model_or_executable_request()
    {
        using var workspace = new TestWorkspace();
        var requestedExecutable = workspace.File("requested-codex.cmd");
        var status = new CodexRuntimeStatus(
            CodexRuntimeCompatibility.Compatible,
            requestedExecutable,
            workspace.File("resolved-codex.cmd"),
            "codex-cli compatible-test",
            "gpt-test",
            "explicit --codex-path",
            "Compatible test runtime.");
        var factory = new AgentRuntimeFactory(new RejectingApprovalPrompt(), status);

        var modelException = await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateAsync(
            "different-model",
            workspace.RootPath,
            requestedExecutable,
            "read-only",
            AgentRuntimeSurface.Cli));
        var pathException = await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateAsync(
            "gpt-test",
            workspace.RootPath,
            workspace.File("different-codex.cmd"),
            "read-only",
            AgentRuntimeSurface.Cli));

        Assert.Contains("different configured model", modelException.Message, StringComparison.Ordinal);
        Assert.Contains("different explicit executable", pathException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurnAsync_uses_startup_context_and_streams_response_through_public_runtime()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, ".agent", "ROLE.md"), "runtime guide");
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var chunks = new List<string>();

        Assert.Equal(AgentRuntimeSurface.Web, runtime.Surface);
        var response = await runtime.RunTurnAsync("hello", (chunk, _) =>
        {
            chunks.Add(chunk);
            return Task.CompletedTask;
        });

        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, response.Status);
        Assert.Equal("runtime guide observed: hello", response.Output);
        Assert.NotNull(response.RunIdentity);
        Assert.Equal("default-conversation", response.RunIdentity.LoopId);
        Assert.Equal("default-assistant", response.RunIdentity.RoleId);
        var assistantEvent = Assert.Single(response.Events);
        Assert.Equal(AgentRuntimeTurnEventKind.AssistantMessage, assistantEvent.Kind);
        Assert.Equal(response.Output, assistantEvent.Text);
        Assert.Equal(response.RunIdentity, assistantEvent.RunIdentity);
        Assert.Equal(["runtime guide observed: hello"], chunks);
        Assert.Collection(
            runtime.GetActiveConversationTranscript(),
            message =>
            {
                Assert.Equal("User", message.Role);
                Assert.Equal("hello", message.Content);
            },
            message =>
            {
                Assert.Equal("Assistant", message.Role);
                Assert.Equal("runtime guide observed: hello", message.Content);
            });
    }

    [Fact]
    public async Task RunTurnAsync_projects_empty_and_unknown_default_conversation_review_requests()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var empty = await runtime.RunTurnAsync("/review");
        var unknown = await runtime.RunTurnAsync("/review resolve missing-review-turn");

        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, empty.Status);
        Assert.Equal("No unresolved default-conversation reviews were found.", empty.Output);
        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, unknown.Status);
        Assert.Contains("missing-review-turn", unknown.Output, StringComparison.Ordinal);
        Assert.Contains("was not found", unknown.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_keeps_ordinary_chat_available_when_another_process_owns_custom_loop_hosting()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopRunsPath);
        var conversationMemory = new ConversationMemoryStore(paths);
        await conversationMemory.AppendMessageAsync(LlmMessage.User("preserved external-host transcript"));
        var replayInput = new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-replayed", "prompt");
        await PersistCompletedMissingInvocationAsync(paths, replayInput);
        var replayResumeInput = new LoopRunControlInput("run-resume-replayed", 4, "resume-replayed");
        var replayCancelInput = new LoopRunControlInput("run-cancel-replayed", 7, "cancel-replayed");
        await PersistCompletedControlAsync(paths, CustomLoopControlKind.Resume, replayResumeInput, CustomLoopControlStatus.Paused, "Resume was already completed and parked safely.");
        await PersistCompletedControlAsync(paths, CustomLoopControlKind.Cancel, replayCancelInput, CustomLoopControlStatus.Cancelled, "Cancellation was already completed durably.");
        using var ownership = new WindowsFileLock(paths.CustomLoopHostLockPath);

        await using var runtime = await CreateRuntimeAsync(workspace);

        var preserved = await conversationMemory.LoadCurrentConversationAsync();
        var customLoop = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-one", "prompt"));
        var replay = await runtime.InvokeCustomLoopAsync(replayInput);
        var replayedResume = await runtime.ResumeCustomLoopAsync(replayResumeInput);
        var replayedCancel = await runtime.CancelCustomLoopAsync(replayCancelInput);
        var blockedResume = await runtime.ResumeCustomLoopAsync(new LoopRunControlInput("run-one", 1, "resume-one"));
        var blockedCancel = await runtime.CancelCustomLoopAsync(new LoopRunControlInput("run-one", 1, "cancel-one"));
        var turn = await runtime.RunTurnAsync("hello");
        await conversationMemory.AppendMessageAsync(LlmMessage.Assistant("externally published custom-loop output"));
        ownership.Dispose();
        var afterRelease = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-two", "prompt"));
        var transcriptAfterReacquisition = runtime.GetActiveConversationTranscript();
        await using var recreatedRuntime = await CreateRuntimeAsync(workspace);
        var afterRecreate = await recreatedRuntime.InvokeCustomLoopAsync(new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-three", "prompt"));

        Assert.Collection(preserved, message => Assert.Equal("preserved external-host transcript", message.Content));
        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, turn.Status);
        Assert.Equal("WorkspaceHostUnavailable", customLoop.AdmissionStatus);
        Assert.False(customLoop.WasDispatched);
        Assert.Equal("NotFound", replay.AdmissionStatus);
        Assert.Contains("The loop definition does not exist.", replay.Detail, StringComparison.Ordinal);
        Assert.Contains("replayed", replay.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Paused", replayedResume.Status);
        Assert.Equal("Resume was already completed and parked safely.", replayedResume.Detail);
        Assert.Equal("Cancelled", replayedCancel.Status);
        Assert.Equal("Cancellation was already completed durably.", replayedCancel.Detail);
        Assert.Equal("WorkspaceHostUnavailable", blockedResume.Status);
        Assert.Equal("resume-one", blockedResume.OperationId);
        Assert.Equal("NotFound", blockedCancel.Status);
        Assert.Equal("cancel-one", blockedCancel.OperationId);
        Assert.Equal(CustomLoopControlOperationState.Complete, (await new CustomLoopControlOperationStore(paths).GetAsync(blockedCancel.OperationId))!.State);
        Assert.Equal("NotFound", afterRelease.AdmissionStatus);
        Assert.Contains(transcriptAfterReacquisition, message => message.Content == "preserved external-host transcript");
        Assert.Contains(transcriptAfterReacquisition, message => message.Content == "hello");
        Assert.Contains(transcriptAfterReacquisition, message => message.Content == "externally published custom-loop output");
        Assert.Equal("NotFound", afterRecreate.AdmissionStatus);
    }

    [Fact]
    public async Task Pending_cancel_reacquires_hosting_and_recovers_after_the_external_owner_exits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var runStore = new CustomLoopRunStore(paths);
        var running = RunningRun("run-owner-exit-recovery");
        await PersistRunningRunAsync(runStore, running);
        using var ownership = new WindowsFileLock(paths.CustomLoopHostLockPath);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var input = new LoopRunControlInput(running.Id, running.LifecycleVersion, "cancel-owner-exit-recovery");

        var unavailable = await runtime.CancelCustomLoopAsync(input);
        ownership.Dispose();
        var recovered = await runtime.CancelCustomLoopAsync(input);
        var receipt = await new CustomLoopControlOperationStore(paths).GetAsync(input.OperationId);

        Assert.Equal("Failed", unavailable.Status);
        Assert.Contains("remains pending", unavailable.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Cancelled", recovered.Status);
        Assert.Equal("Cancelled", recovered.Run!.Status);
        Assert.Equal(CustomLoopControlOperationState.Complete, receipt!.State);
        Assert.Equal(CustomLoopControlStatus.Cancelled, receipt.Outcome);
    }

    [Fact]
    public async Task CreateAsync_keeps_ordinary_chat_available_while_an_in_process_custom_loop_owns_execution()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
        using var activeExecution = gate.TryAcquire("active-custom-loop", new string('a', CustomLoopLimits.Sha256HexCharacters)).Lease!;

        await using var runtime = await CreateRuntimeAsync(workspace);

        var turn = await runtime.RunTurnAsync("hello");
        var customLoop = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput("loop-one", 1, new string('b', CustomLoopLimits.Sha256HexCharacters), "invoke-while-busy", "prompt"));

        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, turn.Status);
        Assert.Equal("WorkspaceHostUnavailable", customLoop.AdmissionStatus);
        Assert.False(customLoop.WasDispatched);
    }

    [Fact]
    public async Task CreateAsync_keeps_ordinary_chat_available_when_custom_loop_recovery_cannot_read_persisted_state()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var conversationMemory = new ConversationMemoryStore(paths);
        await conversationMemory.AppendMessageAsync(LlmMessage.User("preserved recovery-failure transcript"));
        var runDirectory = Path.Combine(paths.CustomLoopRunsPath, "loop-one");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "run-one.json"), "{ malformed");

        await using var runtime = await CreateRuntimeAsync(workspace);

        var preserved = await conversationMemory.LoadCurrentConversationAsync();
        var turn = await runtime.RunTurnAsync("hello");
        var activation = await runtime.StartGovernedWaitBackgroundAsync();
        File.Delete(Path.Combine(runDirectory, "run-one.json"));
        var customLoop = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput("loop-one", 1, new string('a', CustomLoopLimits.Sha256HexCharacters), "invoke-after-recovery-failure", "prompt"));

        Assert.Collection(preserved, message => Assert.Equal("preserved recovery-failure transcript", message.Content));
        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, turn.Status);
        Assert.False(activation.Available);
        Assert.False(activation.RetryAllowed);
        Assert.Equal("Failed", customLoop.AdmissionStatus);
        Assert.Contains("custom_loop_recovery_failed", customLoop.Detail, StringComparison.Ordinal);
        Assert.False(customLoop.WasDispatched);
    }

    [Fact]
    public void Authenticated_event_wait_verifier_is_an_explicit_immutable_factory_configuration()
    {
        var factory = new AgentRuntimeFactory(new RejectingApprovalPrompt());
        var verifier = new RecordingAuthenticatedWakeVerifier();

        var configured = factory.WithAuthenticatedWakeVerifier(verifier);

        Assert.NotSame(factory, configured);
        Assert.Throws<ArgumentNullException>(() => factory.WithAuthenticatedWakeVerifier(null!));
    }

    [Fact]
    public async Task Command_action_provider_configuration_is_immutable_and_composes_only_the_server_derived_surface_actor()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var provider = new CommandActionRuntimeProvider(
            [GovernedCommandActionFactoryTests.TypedRegistration()],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            AvailableCommandActionProcessIsolationBoundary.Instance);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            new RejectingApprovalPrompt(),
            workspace.ServerStatePath,
            CreateCompatibleRuntimeStatus(executablePath));

        var configured = factory.WithCommandActionRuntimeProvider(provider);

        Assert.NotSame(factory, configured);
        Assert.Throws<ArgumentNullException>(() => factory.WithCommandActionRuntimeProvider(null!));
        await using var runtime = await configured.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Create("automation"));
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var preparation = await runtime.PrepareGovernedLoopInvocationAsync(new GovernedLoopInvocationPreparationRequest("missing-graph", "revision-1"));

        Assert.Contains(catalog.NodeDescriptors, node => node.CommandAction is not null);
        Assert.NotNull(runtime.GovernedLoopInvocationPreparation);
        Assert.NotNull(runtime.ModelProfiles);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Unavailable, preparation.Status);
    }

    [Fact]
    public void Public_factory_constructors_validate_pre_resolved_runtime_statuses()
    {
        using var workspace = new TestWorkspace();
        var prompt = new RejectingApprovalPrompt();
        var observer = new NoopConversationPublicationObserver();
        var compatible = CreateCompatibleRuntimeStatus(workspace.File("compatible-codex.cmd"));
        var incompatible = compatible with { Compatibility = CodexRuntimeCompatibility.ExecutableNotFound };
        var missingExecutable = compatible with { ResolvedExecutablePath = " " };

        Assert.NotNull(new AgentRuntimeFactory(prompt, compatible));
        Assert.NotNull(new AgentRuntimeFactory(prompt, observer));
        Assert.NotNull(new AgentRuntimeFactory(prompt, observer, compatible));
        Assert.Throws<ArgumentException>(() => new AgentRuntimeFactory(prompt, incompatible));
        Assert.Throws<ArgumentException>(() => new AgentRuntimeFactory(prompt, missingExecutable));
        Assert.Throws<ArgumentException>(() => AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            prompt,
            workspace.ServerStatePath,
            additionalModelProfileProviders: [null!]));
    }

    [Fact]
    public void Invocation_preparation_facade_rejects_noncanonical_server_derived_workspace_and_actor_inputs()
    {
        Assert.Throws<ArgumentException>(() => new GovernedLoopInvocationPreparationFacade(
            "workspace-not-canonical",
            "embodysense.web",
            true,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!));
        Assert.Throws<ArgumentException>(() => new GovernedLoopInvocationPreparationFacade(
            "workspace-sha256:" + new string('a', 64),
            "not canonical",
            true,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!));
    }

    [Fact]
    public async Task Authenticated_event_delivery_flows_through_the_configured_surface_verifier()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = AuthenticatedEventCheckpoint(DateTimeOffset.Parse("2026-08-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var persisted = await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, new string('4', 64));
        Assert.Equal(checkpoint.ContentHash, persisted!.Checkpoint!.ContentHash);
        var verifier = new RecordingAuthenticatedWakeVerifier();
        await using var runtime = await CreateRuntimeAsync(workspace, verifier: verifier);
        var authenticationEvidenceHash = new string('5', 64);

        var result = await runtime.DeliverAuthenticatedWakeAsync(new AgentRuntimeAuthenticatedWakeDeliveryInput(
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            authenticationEvidenceHash));

        Assert.Equal(AgentRuntimeAuthenticatedWakeDeliveryStatus.NotFound, result.Status);
        Assert.Null(result.WakeId);
        Assert.Null(result.EvidenceHash);
        Assert.False(result.ContinuationInvoked);
        Assert.Equal(1, verifier.VerifyCount);
        Assert.Equal(checkpoint.CheckpointId, verifier.LastRequest!.CheckpointId);
        Assert.Equal(checkpoint.AuthenticatedEventReference, verifier.LastRequest.AuthenticatedEventReference);
        Assert.Equal(authenticationEvidenceHash, verifier.LastRequest.AuthenticationEvidenceHash);
    }

    [Theory]
    [InlineData(AgentRuntimeAuthenticatedWakeVerificationStatus.Rejected, AgentRuntimeAuthenticatedWakeDeliveryStatus.Invalid)]
    [InlineData(AgentRuntimeAuthenticatedWakeVerificationStatus.Conflict, AgentRuntimeAuthenticatedWakeDeliveryStatus.Conflict)]
    [InlineData(AgentRuntimeAuthenticatedWakeVerificationStatus.Unavailable, AgentRuntimeAuthenticatedWakeDeliveryStatus.Unavailable)]
    public async Task Authenticated_event_delivery_projects_terminal_verification_posture(
        AgentRuntimeAuthenticatedWakeVerificationStatus verificationStatus,
        AgentRuntimeAuthenticatedWakeDeliveryStatus expectedStatus)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = AuthenticatedEventCheckpoint(DateTimeOffset.Parse("2026-08-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, new string('4', 64));
        var verifier = new RecordingAuthenticatedWakeVerifier(verificationStatus);
        await using var runtime = await CreateRuntimeAsync(workspace, verifier: verifier);

        var result = await runtime.DeliverAuthenticatedWakeAsync(new AgentRuntimeAuthenticatedWakeDeliveryInput(
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            new string('5', 64)));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(1, verifier.VerifyCount);
        Assert.False(result.ContinuationInvoked);
    }

    [Fact]
    public async Task Startup_recovery_preserves_unsupported_discovery_index_guidance_and_retries_after_cleanup()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var runStore = new CustomLoopRunStore(paths);
        await PersistRunningRunAsync(runStore, RunningRun("run-unsupported-startup-recovery"));
        const string UnsupportedIndex = "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}";
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        await File.WriteAllTextAsync(indexPath, UnsupportedIndex);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var input = new LoopRunInvocationInput("loop-missing", 1, new string('a', CustomLoopLimits.Sha256HexCharacters), "invoke-after-unsupported-startup-recovery", "retry after cleanup");

        Assert.True(runtime.CustomLoopRecoveryRequired);
        var exception = await Assert.ThrowsAsync<LoopRunEvidenceUnsupportedSchemaException>(() => runtime.InvokeCustomLoopAsync(input));

        Assert.Contains("Delete `.custom-loop-run-index.json`", exception.Message, StringComparison.Ordinal);
        Assert.Equal(UnsupportedIndex, await File.ReadAllTextAsync(indexPath));

        File.Delete(indexPath);
        var retry = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("NotFound", retry.AdmissionStatus);
        Assert.False(runtime.CustomLoopRecoveryRequired);
    }

    [Fact]
    public async Task Lifecycle_control_preserves_unsupported_discovery_index_guidance_and_retries_the_same_receipt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var runStore = new CustomLoopRunStore(paths);
        var running = RunningRun("run-unsupported-control");
        await PersistRunningRunAsync(runStore, running);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var recovered = Assert.IsType<LoopRunSnapshot>(await runtime.GetCustomLoopRunAsync(running.Id));
        Assert.Equal("Paused", recovered.Status);
        const string UnsupportedIndex = "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}";
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        await File.WriteAllTextAsync(indexPath, UnsupportedIndex);
        var input = new LoopRunControlInput(recovered.Id, recovered.LifecycleVersion, "cancel-after-unsupported-index");

        var exception = await Assert.ThrowsAsync<LoopRunEvidenceUnsupportedSchemaException>(() => runtime.CancelCustomLoopAsync(input));

        Assert.Contains("Delete `.custom-loop-run-index.json`", exception.Message, StringComparison.Ordinal);
        Assert.Equal(CustomLoopControlOperationState.Pending, (await new CustomLoopControlOperationStore(paths).GetAsync(input.OperationId))!.State);

        File.Delete(indexPath);
        var retry = await runtime.CancelCustomLoopAsync(input);

        Assert.Equal("Cancelled", retry.Status);
        Assert.Equal(CustomLoopControlOperationState.Complete, (await new CustomLoopControlOperationStore(paths).GetAsync(input.OperationId))!.State);
        Assert.False(runtime.CustomLoopRecoveryRequired);
    }

    [Fact]
    public async Task RunTurnAsync_closes_a_conclusive_terminal_provider_failure_without_review_or_quarantine()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var fakeCodex = await CreateFakeCodexExecutableAsync(workspace, "provider exploded");
        AgentRuntimeTurnResult response;
        await using (var runtime = await CreateRuntimeAsync(workspace, codexPath: fakeCodex))
        {
            response = await runtime.RunTurnAsync("hello");
            Assert.Empty(await runtime.ListDefaultConversationReviewsAsync());
            Assert.Collection(
                runtime.GetActiveConversationTranscript(),
                message => Assert.Equal(("User", "hello"), (message.Role, message.Content)));
        }

        Assert.Equal(AgentRuntimeTurnStatus.MessageFailed, response.Status);
        Assert.Contains("Codex app-server turn failed: provider exploded", response.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(response.FailureDetail, response.Output);
        var failureEvent = Assert.Single(response.Events);
        Assert.Equal(AgentRuntimeTurnEventKind.Failure, failureEvent.Kind);
        Assert.Equal(response.FailureDetail, failureEvent.Text);
        Assert.Equal(response.RunIdentity, failureEvent.RunIdentity);
        Assert.NotNull(response.RunIdentity);
        Assert.Equal("default-conversation", response.RunIdentity.LoopId);
        Assert.Equal("default-assistant", response.RunIdentity.RoleId);

        await using var restarted = await CreateRuntimeAsync(workspace, codexPath: fakeCodex);
        Assert.Empty(restarted.GetActiveConversationTranscript());
        Assert.Empty(await restarted.ListDefaultConversationReviewsAsync());
    }

    [Fact]
    public void MessageFailed_preserves_prior_assistant_events_before_failure()
    {
        var runIdentity = new AgentRuntimeRunIdentity("default-conversation", "run-1", "default-assistant");

        var result = AgentRuntimeTurnResult.MessageFailed(
            "terminal persistence failed",
            runIdentity,
            [AgentRuntimeTurnEvent.AssistantMessage("accepted response", runIdentity)]);

        Assert.Equal(AgentRuntimeTurnStatus.MessageFailed, result.Status);
        Assert.Collection(
            result.Events,
            turnEvent =>
            {
                Assert.Equal(AgentRuntimeTurnEventKind.AssistantMessage, turnEvent.Kind);
                Assert.Equal("accepted response", turnEvent.Text);
                Assert.Equal(runIdentity, turnEvent.RunIdentity);
            },
            turnEvent =>
            {
                Assert.Equal(AgentRuntimeTurnEventKind.Failure, turnEvent.Kind);
                Assert.Equal("terminal persistence failed", turnEvent.Text);
                Assert.Equal(runIdentity, turnEvent.RunIdentity);
            });
    }

    private static async Task PersistCompletedMissingInvocationAsync(WorkspacePaths paths, LoopRunInvocationInput input)
    {
        var now = DateTimeOffset.UtcNow;
        var prompt = input.InvocationPrompt ?? string.Empty;
        var requestHash = CustomLoopInvocationRequestHash.Compute(input.OperationId, input.LoopId, input.ExpectedDefinitionVersion, input.ExpectedDefinitionHash, WorkspaceActors.Cli, AgentRuntimeSurface.Cli.Id, "default-assistant", prompt, LlmInferenceSurface.OpenAiCodex.ToString(), "test-model");
        var pending = new CustomLoopInvocationOperation(
            CustomLoopInvocationOperation.CurrentSchemaVersion,
            input.OperationId,
            requestHash,
            input.LoopId,
            input.ExpectedDefinitionVersion,
            input.ExpectedDefinitionHash,
            WorkspaceActors.Cli,
            AgentRuntimeSurface.Cli.Id,
            "default-assistant",
            CustomLoopInvocationRequestHash.ComputePromptHash(prompt),
            LlmInferenceSurface.OpenAiCodex.ToString(),
            "test-model",
            CustomLoopInvocationBindingState.Unbound,
            null,
            null,
            now,
            now,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            [],
            "The invocation is pending.");
        var store = new CustomLoopInvocationOperationStore(paths);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        pending = pending with
        {
            BindingState = CustomLoopInvocationBindingState.ConversationNotFound,
            InvokingConversationId = (await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync()).Version
        };
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Bound, (await store.BindAsync(pending)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(pending with
        {
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = "NotFound",
            Detail = "The loop definition does not exist."
        })).Status);
    }

    private static CustomLoopRunRecord RunningRun(string runId)
    {
        var now = DateTimeOffset.Parse("2026-07-26T12:00:00+00:00");
        var definition = CustomLoopDefinitionContentHash.Apply(CustomLoopDefinition.CreateSeed("loop-owner-exit-recovery", "role-workspace", "step-only", "create-loop-owner-exit-recovery", now) with { ContentHash = string.Empty });
        CustomLoopRunEvent[] events =
        [
            new(1, $"admitted-{runId}", now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null),
            new(2, $"admission-audit-{runId}", now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null),
            new(3, $"running-{runId}", now.AddSeconds(1), CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null)
        ];
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            runId,
            definition.Id,
            events.Length,
            CustomLoopRunStatus.Running,
            now,
            now.AddSeconds(1),
            null,
            "cli",
            new CustomLoopModelSnapshot("provider", "model"),
            $"admit-{runId}",
            WorkspaceActors.Cli,
            string.Empty,
            definition,
            "prompt",
            null,
            CustomLoopContextSnapshot.CreateEmpty(now),
            new CustomLoopExecutionClock(0, now.AddSeconds(1)),
            CustomLoopRunCheckpoint.Start(),
            events,
            null,
            null,
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, now)
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopRunRecord TriggerRunningRun(string runId, string operationId)
    {
        var candidate = RunningRun(runId) with
        {
            Surface = "webhook",
            ModelSnapshot = new CustomLoopModelSnapshot(LlmInferenceSurface.OpenAiCodex.ToString(), "test-model"),
            AdmissionOperationId = operationId,
            AdmissionActor = "trigger-owner",
            AdmissionRequestHash = string.Empty
        };
        return CustomLoopAdmissionRequestHash.Apply(candidate);
    }

    private static async Task<CustomLoopRunRecord> AdmitLegacyRunAsync(TestWorkspace workspace, string operationId)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var authoring = new LoopAuthoringFacade(workspace.RootPath, new CustomLoopRunStore(paths), WorkspaceActors.Cli);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await authoring.CreateAsync("create-human-resume-restart")).Definition);
        var definitionStore = new CustomLoopDefinitionStore(paths);
        var definition = Assert.IsType<CustomLoopDefinition>(await definitionStore.GetAsync(created.Id));
        var now = DateTimeOffset.UtcNow;
        var context = CustomLoopContextSnapshot.CreateEmpty(now);
        var trustProvider = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var admission = await new CustomLoopAdmissionService(
            definitionStore,
            new CustomLoopRunStore(paths),
            new AuditLog(paths),
            new CustomLoopToolAuthorityProvider(new LoopDefinitionStore(paths)),
            CapabilityAdmissionFactory.Create(paths, trustProvider)).AdmitAsync(
                new CustomLoopAdmissionRequest(
                    definition.Id,
                    definition.DefinitionVersion,
                    definition.ContentHash,
                    operationId,
                    WorkspaceActors.Cli,
                    AgentRuntimeSurface.Cli.Id,
                    definition.RoleId,
                    "resume after an interrupted human admission",
                    new CustomLoopModelSnapshot(LlmInferenceSurface.OpenAiCodex.ToString(), "test-model"),
                    null,
                    context));

        Assert.Equal(CustomLoopAdmissionStatus.Admitted, admission.Status);
        return Assert.IsType<CustomLoopRunRecord>(admission.Run);
    }

    private static async Task PersistRunningRunAsync(CustomLoopRunStore store, CustomLoopRunRecord running)
    {
        var admitted = running with
        {
            LifecycleVersion = 1,
            Status = CustomLoopRunStatus.Admitted,
            UpdatedAtUtc = running.CreatedAtUtc,
            ExecutionClock = CustomLoopExecutionClock.NotStarted(),
            Events = [running.Events[0]]
        };
        var audited = admitted with
        {
            LifecycleVersion = 2,
            Events = [.. running.Events[..2]]
        };

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(audited, admitted.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, audited.LifecycleVersion)).Status);
    }

    private static async Task PersistCompletedControlAsync(WorkspacePaths paths, CustomLoopControlKind kind, LoopRunControlInput input, CustomLoopControlStatus outcome, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new CustomLoopControlOperation(
            CustomLoopControlOperation.CurrentSchemaVersion,
            input.OperationId,
            CustomLoopControlRequestHash.Compute(kind, input.RunId, input.ExpectedLifecycleVersion, input.OperationId, WorkspaceActors.Cli),
            kind,
            input.RunId,
            input.ExpectedLifecycleVersion,
            WorkspaceActors.Cli,
            now,
            now,
            CustomLoopControlOperationState.Pending,
            CustomLoopControlStatus.Unknown,
            null,
            null,
            false,
            "The control operation is pending.");
        var store = new CustomLoopControlOperationStore(paths);
        var created = await store.BeginAsync(pending);
        using var lease = Assert.IsAssignableFrom<ICustomLoopControlOperationLease>(created.Lease);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, created.Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(created.Operation! with
        {
            State = CustomLoopControlOperationState.Complete,
            Outcome = outcome,
            ResultLifecycleVersion = input.ExpectedLifecycleVersion,
            ResultRunStatus = outcome == CustomLoopControlStatus.Paused ? CustomLoopRunStatus.Paused : CustomLoopRunStatus.Cancelled,
            OutcomeAuditRecorded = true,
            Detail = detail
        })).Status);
    }

    [Fact]
    public async Task RunTurnAsync_returns_failed_runtime_result_when_default_loop_is_disabled()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await new LoopDefinitionStore(paths).SaveAsync(LoopDefinition.CreateDefaultConversation() with { State = LoopState.Disabled });
        await using var runtime = await CreateRuntimeAsync(workspace);

        var response = await runtime.RunTurnAsync("hello");
        var history = await runtime.RunTurnAsync("/history");

        Assert.Equal(AgentRuntimeTurnStatus.MessageFailed, response.Status);
        Assert.Equal("Loop `default-conversation` is not enabled.", response.FailureDetail);
        Assert.NotNull(response.RunIdentity);
        Assert.Equal("default-conversation", response.RunIdentity.LoopId);
        Assert.Equal("No stored conversations were found.", history.Output);
    }

    [Fact]
    public async Task RunTurnAsync_emits_visible_context_when_verbose_is_enabled()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, ".agent", "ROLE.md"), "runtime guide");
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var contexts = new List<string>();

        var verboseResult = runtime.SetVerbose(true);
        var response = await runtime.RunTurnAsync("hello", verboseContextHandler: (context, _) =>
        {
            contexts.Add(context);
            return Task.CompletedTask;
        });

        Assert.Contains("Verbose mode enabled", verboseResult.Output, StringComparison.Ordinal);
        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, response.Status);
        Assert.Equal("runtime guide observed: hello", response.Output);
        var context = Assert.Single(contexts);
        Assert.Contains("[verbose] Visible inference context follows.", context, StringComparison.Ordinal);
        Assert.Contains("This is not private model reasoning", context, StringComparison.Ordinal);
        Assert.Contains("runtime guide", context, StringComparison.Ordinal);
        Assert.Contains("hello", context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurnAsync_handles_commands_and_routes_unknown_slash_text_to_model()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);

        Assert.True(AgentRuntime.TryHandleStaticRuntimeCommand("/help", out var staticResult));
        Assert.Contains("Runtime commands:", staticResult.Output, StringComparison.Ordinal);
        var staticEvent = Assert.Single(staticResult.Events);
        Assert.Equal(AgentRuntimeTurnEventKind.CommandOutput, staticEvent.Kind);
        Assert.Contains("/help, /commands", staticEvent.Text, StringComparison.Ordinal);

        var help = await runtime.RunTurnAsync("/commands");
        var unknown = await runtime.RunTurnAsync("/not-a-command");
        var exit = await runtime.RunTurnAsync("/quit");

        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, help.Status);
        Assert.Contains("/new, /new-session", help.Output, StringComparison.Ordinal);
        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, unknown.Status);
        Assert.Equal("runtime guide missing: /not-a-command", unknown.Output);
        Assert.Equal(AgentRuntimeTurnStatus.ExitRequested, exit.Status);
        Assert.True(exit.ExitRequested);
    }

    [Fact]
    public async Task RunTurnAsync_loads_pending_history_selection()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("saved prompt"));
        await store.AppendMessageAsync(LlmMessage.Assistant("saved answer"));
        await using var runtime = await CreateRuntimeAsync(workspace);

        var history = await runtime.RunTurnAsync("/history");
        var loaded = await runtime.RunTurnAsync("1");

        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, history.Status);
        Assert.Contains("Stored conversations:", history.Output, StringComparison.Ordinal);
        Assert.Contains("saved prompt", history.Output, StringComparison.Ordinal);
        Assert.Contains("Send conversation number to load", history.Prompt, StringComparison.Ordinal);
        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, loaded.Status);
        Assert.Contains("Loaded conversation `archive/", loaded.Output, StringComparison.Ordinal);
        Assert.True(loaded.ReplaceTranscript);
        Assert.Collection(
            loaded.Events,
            turnEvent => Assert.Equal(AgentRuntimeTurnEventKind.TranscriptReplacement, turnEvent.Kind),
            turnEvent => Assert.Equal(AgentRuntimeTurnEventKind.CommandOutput, turnEvent.Kind));
        Assert.Collection(
            loaded.RestoredMessages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("saved prompt", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("saved answer", message.Content);
            });
        var currentMessages = await store.LoadCurrentConversationAsync();
        Assert.Collection(
            currentMessages,
            message => Assert.Equal("saved prompt", message.Content),
            message => Assert.Equal("saved answer", message.Content));
    }

    [Fact]
    public async Task RunTurnAsync_handles_pending_history_cancel_and_invalid_selection()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var store = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        await store.AppendMessageAsync(LlmMessage.User("saved prompt"));
        await using var runtime = await CreateRuntimeAsync(workspace);

        _ = await runtime.RunTurnAsync("/history");
        var cancelled = await runtime.RunTurnAsync("/cancel");
        _ = await runtime.RunTurnAsync("/history");
        var invalid = await runtime.RunTurnAsync("99");
        _ = await runtime.RunTurnAsync("/history");
        var blankCancelled = await runtime.RunTurnAsync("");

        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, cancelled.Status);
        Assert.Equal("Conversation load cancelled.", cancelled.Output);
        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, invalid.Status);
        Assert.Equal("Invalid conversation selection.", invalid.Output);
        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, blankCancelled.Status);
        Assert.Equal("Conversation load cancelled.", blankCancelled.Output);
    }

    [Fact]
    public async Task RunTurnAsync_requires_history_before_model_turn_and_new_resets_state()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);

        _ = await runtime.RunTurnAsync("hello");
        var historyAfterTurn = await runtime.RunTurnAsync("/history");
        var fresh = await runtime.RunTurnAsync("/new");
        var historyAfterNew = await runtime.RunTurnAsync("/history");

        Assert.Contains("before sending the first prompt", historyAfterTurn.Output, StringComparison.Ordinal);
        Assert.Equal("Started a new conversation.", fresh.Output);
        Assert.Contains("Stored conversations:", historyAfterNew.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_commands_project_a_transcript_conflict_as_blocked_without_mutating_retained_evidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var record = await PersistTranscriptConflictReviewAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var artifactPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json");
        var retainedArtifact = await File.ReadAllTextAsync(artifactPath);
        await using var runtime = await CreateRuntimeAsync(workspace);

        var review = Assert.Single(await runtime.ListDefaultConversationReviewsAsync());
        Assert.Equal(DefaultConversationTurnReviewClassification.TranscriptConflict, review.Classification);
        Assert.Contains("remains blocked", review.AllowedAction, StringComparison.Ordinal);
        Assert.DoesNotContain("/review resolve", review.AllowedAction, StringComparison.Ordinal);

        var listed = await runtime.RunTurnAsync("/review");
        var rejected = await runtime.RunTurnAsync($"/review resolve {record.TurnId}");

        Assert.Contains(nameof(DefaultConversationTurnReviewClassification.TranscriptConflict), listed.Output, StringComparison.Ordinal);
        Assert.Contains("Allowed action", listed.Output, StringComparison.Ordinal);
        Assert.Contains(nameof(DefaultConversationTurnReviewClassification.TranscriptConflict), rejected.Output, StringComparison.Ordinal);
        Assert.Contains("cannot be abandoned", rejected.Output, StringComparison.Ordinal);
        Assert.Equal(retainedArtifact, await File.ReadAllTextAsync(artifactPath));
        var reread = await turns.LoadAsync(record.TurnId);
        Assert.NotNull(reread);
        Assert.Equal(record.LifecycleVersion, reread.LifecycleVersion);
        Assert.Equal(record.ProviderOutcome, reread.ProviderOutcome);
        Assert.Equal(record.ReviewDetail, reread.ReviewDetail);
        Assert.Null(reread.ReviewResolution);
        Assert.Single(await runtime.ListDefaultConversationReviewsAsync());
    }

    private static async Task<DefaultConversationTurnRecord> PersistTranscriptConflictReviewAsync(TestWorkspace workspace)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var memory = new ConversationMemoryStore(paths);
        var turns = new DefaultConversationTurnStore(paths);
        var startedAtUtc = DateTimeOffset.UtcNow;
        const string RequestId = "transcript-conflict-review";
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(RequestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Cli, LoopTrigger.HumanMessage, startedAtUtc);
        var record = DefaultConversationTurnProtocol.Admit(run, await memory.LoadCurrentConversationSnapshotAsync(), LlmMessage.User("hello"), startedAtUtc, RequestId, TestCapabilityAdmissionFactory.Create(LoopDefinition.CreateDefaultConversation().CapabilityRequirements, startedAtUtc));
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(record)).Status);

        foreach (var checkpoint in new[]
        {
            DefaultConversationTurnCheckpoint.RunStarted,
            DefaultConversationTurnCheckpoint.UserMessageAccepted,
            DefaultConversationTurnCheckpoint.UserPublicationPrepared,
            DefaultConversationTurnCheckpoint.UserPublished,
            DefaultConversationTurnCheckpoint.ProviderDispatchPrepared
        })
        {
            record = record.Advance(checkpoint, startedAtUtc.AddSeconds(record.LifecycleVersion), checkpoint.ToString());
            Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(record, record.LifecycleVersion - 1)).Status);
        }

        record = record.Advance(DefaultConversationTurnCheckpoint.ProviderDispatchStarted, startedAtUtc.AddSeconds(record.LifecycleVersion), "Provider entered.", providerOutcome: DefaultConversationProviderOutcome.OutcomeUnknown);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(record, record.LifecycleVersion - 1)).Status);
        var assistant = new DefaultConversationTurnMessage(record.TurnId + ":message:assistant", LlmMessageRole.Assistant, "observed answer");
        record = record.Advance(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved, startedAtUtc.AddSeconds(record.LifecycleVersion), "Provider outcome observed.", providerOutcome: DefaultConversationProviderOutcome.Observed, assistantMessage: assistant, providerResponseId: "provider-response-1");
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(record, record.LifecycleVersion - 1)).Status);
        record = record.Advance(DefaultConversationTurnCheckpoint.AssistantPublicationPrepared, startedAtUtc.AddSeconds(record.LifecycleVersion), "Assistant publication prepared.");
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(record, record.LifecycleVersion - 1)).Status);
        const string Detail = "Transcript publication conflicts with retained turn evidence.";
        var needsReview = record.Run.NeedsReview(startedAtUtc.AddSeconds(record.LifecycleVersion), Detail);
        record = record.Advance(DefaultConversationTurnCheckpoint.TerminalPrepared, startedAtUtc.AddSeconds(record.LifecycleVersion), "Terminal review prepared.", run: needsReview, reviewDetail: Detail);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(record, record.LifecycleVersion - 1)).Status);
        record = record.Advance(DefaultConversationTurnCheckpoint.Terminal, startedAtUtc.AddSeconds(record.LifecycleVersion), "Terminal review committed.", run: needsReview, runProjectionSynchronized: true);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(record, record.LifecycleVersion - 1)).Status);
        return record;
    }

    private static async Task<string> CreateFakeCodexExecutableAsync(
        TestWorkspace workspace,
        string? turnFailureMessage = null,
        string? turnStartMarkerPath = null,
        string? turnReleaseMarkerPath = null)
    {
        var scriptPath = workspace.File("fake-codex.js");
        var commandPath = workspace.File(OperatingSystem.IsWindows() ? "fake-codex.cmd" : "fake-codex");
        var serializedTurnFailureMessage = System.Text.Json.JsonSerializer.Serialize(turnFailureMessage);
        var serializedTurnStartMarkerPath = System.Text.Json.JsonSerializer.Serialize(turnStartMarkerPath);
        var serializedTurnReleaseMarkerPath = System.Text.Json.JsonSerializer.Serialize(turnReleaseMarkerPath);
        await File.WriteAllTextAsync(scriptPath, $$"""
            const fs = require("node:fs");
            const readline = require("node:readline");

            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli 999.0.0-test\n");
              process.exit(0);
            }

            const threadId = "thread-test";
            const turnFailureMessage = {{serializedTurnFailureMessage}};
            const turnStartMarkerPath = {{serializedTurnStartMarkerPath}};
            const turnReleaseMarkerPath = {{serializedTurnReleaseMarkerPath}};
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            let developerInstructions = "";

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            input.on("line", async line => {
              const message = JSON.parse(line);
              switch (message.method) {
                case "initialize":
                  write({ id: message.id, result: {} });
                  break;
                case "initialized":
                  break;
                case "model/list":
                  write({
                    id: message.id,
                    result: {
                      data: [
                        { id: "test-model", model: "test-model" },
                        { id: "gpt-test", model: "gpt-test" }
                      ]
                    }
                  });
                  break;
                case "thread/start":
                  developerInstructions = String(message.params?.developerInstructions ?? "");
                  const model = String(message.params?.model ?? "");
                  const modelProvider = String(message.params?.modelProvider ?? "");
                  write({ id: message.id, result: { model, modelProvider, thread: { id: threadId, modelProvider } } });
                  break;
                case "turn/start": {
                  if (turnStartMarkerPath) {
                    fs.appendFileSync(turnStartMarkerPath, "started\n");
                  }
                  if (turnReleaseMarkerPath) {
                    while (!fs.existsSync(turnReleaseMarkerPath)) {
                      await new Promise(resolve => setTimeout(resolve, 25));
                    }
                    fs.rmSync(turnReleaseMarkerPath);
                  }
                  const turnId = "turn-test";
                  let userText = String(message.params?.input?.[0]?.text ?? "");
                  const prefix = developerInstructions.includes("runtime guide") || userText.includes("runtime guide")
                    ? "runtime guide observed"
                    : "runtime guide missing";
                  const currentUserMarker = "Current user message:";
                  const currentUserIndex = userText.indexOf(currentUserMarker);
                  if (currentUserIndex >= 0) {
                    userText = userText.slice(currentUserIndex + currentUserMarker.length).trim();
                  }
                  const text = `${prefix}: ${userText}`;

                  write({ id: message.id, result: { turn: { id: turnId } } });
                  if (turnFailureMessage) {
                    write({
                      method: "turn/completed",
                      params: {
                        threadId,
                        turnId,
                        turn: {
                          id: turnId,
                          status: "failed",
                          error: { message: turnFailureMessage },
                          items: []
                        }
                      }
                    });
                    break;
                  }

                  write({ method: "item/agentMessage/delta", params: { threadId, turnId, delta: text } });
                  write({
                    method: "turn/completed",
                    params: {
                      threadId,
                      turnId,
                      turn: {
                        id: turnId,
                        status: "completed",
                        items: [{ type: "agentMessage", phase: "final_answer", text }]
                      }
                    }
                  });
                  break;
                }
                default:
                  break;
              }
            });
            """);
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(commandPath, """
                @echo off
                node "%~dp0fake-codex.js" %*
                """);
        }
        else
        {
            var quotedScriptPath = scriptPath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal);
            await File.WriteAllTextAsync(commandPath, $"#!/bin/sh\nexec node \"{quotedScriptPath}\" \"$@\"\n");
            File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return commandPath;
    }

    private static async Task<TriggerQueueEntry> WaitForDispatchRejectedAsync(TriggerQueueStore queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = await queue.GetSnapshotAsync(DateTimeOffset.UtcNow, timeout.Token);
            var entry = Assert.Single(snapshot.Entries);
            if (entry.State == TriggerQueueEntryState.DispatchRejected)
            {
                return entry;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static async Task WaitForHeldAttemptAsync(string path, Task<LoopRunInvocationResponse> invocation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            var delay = Task.Delay(50);
            if (await Task.WhenAny(delay, invocation) == invocation)
            {
                await ThrowInvocationCompletionBeforeHeldAttemptAsync(invocation);
            }
        }

        if (File.Exists(path))
        {
            return;
        }

        if (invocation.IsCompleted)
        {
            await ThrowInvocationCompletionBeforeHeldAttemptAsync(invocation);
        }

        throw new Xunit.Sdk.XunitException("The fake Codex provider did not reach the held custom-loop attempt before the existing test deadline while the invocation was still active.");
    }

    private static async Task ThrowInvocationCompletionBeforeHeldAttemptAsync(Task<LoopRunInvocationResponse> invocation)
    {
        try
        {
            var completed = await invocation;
            throw new Xunit.Sdk.XunitException($"The held custom-loop invocation completed before the fake Codex provider reached its entry marker. AdmissionStatus={completed.AdmissionStatus}; ExecutionStatus={completed.ExecutionStatus}; WasDispatched={completed.WasDispatched}; RunStatus={completed.Run?.Status}; Detail={completed.Detail}");
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException($"The held custom-loop invocation faulted before the fake Codex provider reached its entry marker. Fault={exception.GetType().Name}; Detail={exception.Message}");
        }
    }

    private static async Task<GovernedLoopGraphCatalogResponse> WaitForHumanInputExecutableAsync(AgentRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync(timeout.Token);
            if (HumanInputDescriptor(catalog).IsExecutable)
            {
                return catalog;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static async Task<AgentRuntimeGovernedLoopBackgroundStatus> WaitForBackgroundReadinessAsync(
        AgentRuntime runtime,
        AgentRuntimeGovernedLoopBackgroundReadiness readiness)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var status = await runtime.ReadGovernedLoopLocalBackgroundStatusAsync(timeout.Token);
            if (status.Readiness == readiness)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static GovernedLoopGraphCatalogNodeSnapshot HumanInputDescriptor(GovernedLoopGraphCatalogResponse catalog)
        => Assert.Single(catalog.NodeDescriptors, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanInput);

    private static HumanInputRequestLifecycleStoreMutation CreateFreshHumanInputMutation(
        string workspacePath,
        string requestId,
        string requestVersionId,
        string operationId,
        string requestHash,
        long generation = 0,
        string prompt = "Private prompt one.")
    {
        var requestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToUniversalTime();
        var binding = new HumanInputRequestBinding(
            CapabilityWorkspaceScopeId.Create(workspacePath),
            "governed-loop",
            "loop-revision-one",
            "node-one",
            "run-one",
            "checkpoint-one");
        var request = HumanInputRequestStoreTestData.Request(requestId, requestVersionId, requestedAtUtc, binding, prompt: prompt);
        var head = HumanInputRequestStoreTestData.Head(
            request,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            0,
            null,
            null,
            operationId,
            requestedAtUtc);
        var evidence = HumanInputRequestStoreTestData.Evidence(
            HumanInputRequestLifecycleOperationKind.Create,
            requestId,
            operationId,
            requestHash,
            requestedAtUtc,
            null,
            head,
            request);
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, request, head, null);
    }

    private static HumanInputRequestLifecycleStoreMutation CreateDurableLifecycleReplayMutation(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequest previousRequest,
        HumanInputRequestLifecycleHead previousHead,
        long generation,
        string operationId,
        HumanInputRequest? candidate)
    {
        var supersede = kind == HumanInputRequestLifecycleOperationKind.Supersede;
        var resultRequest = candidate ?? previousRequest;
        var recordedAtUtc = previousHead.UpdatedAtUtc.AddSeconds(1);
        var resultHead = HumanInputRequestStoreTestData.Head(
            supersede ? previousRequest : resultRequest,
            previousHead.LifecycleVersion + 1,
            supersede ? HumanInputRequestLifecycleStatus.Superseded : HumanInputRequestLifecycleStatus.Pending,
            kind == HumanInputRequestLifecycleOperationKind.Remind ? previousHead.ReminderCount + 1 : previousHead.ReminderCount,
            previousHead.SupersedesRequestId,
            supersede ? candidate!.RequestId : previousHead.SupersededByRequestId,
            operationId,
            recordedAtUtc);
        var relatedResultHead = supersede
            ? HumanInputRequestStoreTestData.Head(
                candidate!,
                1,
                HumanInputRequestLifecycleStatus.Pending,
                0,
                previousRequest.RequestId,
                null,
                operationId,
                recordedAtUtc)
            : null;
        Assert.True(AuthorityActorId.TryParse("user-one", out var actor, out _));
        Assert.True(AuthorityPurpose.TryParse("Replay one exact lifecycle operation.", out var reason, out _));
        var grant = CreateReplayGrantReference("grant-replay");
        var command = HumanInputRequestLifecycleCommandHash.Apply(new HumanInputRequestLifecycleCommand(
            HumanInputRequestLifecycleCommand.CurrentSchemaVersion,
            operationId,
            kind,
            previousRequest.RequestId,
            previousHead.LifecycleVersion,
            previousHead.Status,
            previousHead.CurrentRequest,
            previousRequest.Binding,
            candidate,
            grant,
            reason!,
            string.Empty));
        var evidence = new HumanInputRequestLifecycleOperationEvidence(
            1,
            operationId,
            command.RequestHash,
            kind,
            HumanInputRequestLifecycleOperationOutcome.Committed,
            HumanInputRequestLifecycleOperationFailureCode.None,
            previousRequest.RequestId,
            previousHead.LifecycleVersion,
            previousHead.Status,
            previousHead.CurrentRequest,
            previousRequest.Binding,
            previousHead,
            resultHead,
            supersede ? candidate!.RequestId : null,
            null,
            relatedResultHead,
            candidate is null ? null : HumanInputRequestStoreTestData.Reference(candidate),
            actor!,
            reason!,
            grant,
            HumanInputRequestStoreTestData.HashA,
            HumanInputRequestStoreTestData.HashB,
            recordedAtUtc);
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, candidate, resultHead, relatedResultHead);
    }

    private static AuthorityGrantReference CreateReplayGrantReference(string grantId)
    {
        Assert.True(AuthorityGrantId.TryParse(grantId, out var parsedGrantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var revision, out _));
        return new AuthorityGrantReference(parsedGrantId!, revision!, "sha256:" + HumanInputRequestStoreTestData.HashA);
    }

    private static HumanInputRequestLifecycleStoreMutation CreateFreshTerminalHumanInputMutation(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequest request,
        HumanInputRequestLifecycleHead previousHead,
        long generation,
        string operationId,
        string requestHash)
    {
        var status = kind switch
        {
            HumanInputRequestLifecycleOperationKind.Cancel => HumanInputRequestLifecycleStatus.Cancelled,
            HumanInputRequestLifecycleOperationKind.Expire => HumanInputRequestLifecycleStatus.Expired,
            HumanInputRequestLifecycleOperationKind.Reject => HumanInputRequestLifecycleStatus.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var recordedAtUtc = kind == HumanInputRequestLifecycleOperationKind.Expire
            ? request.Timing.ExpiresAtUtc.AddTicks(1)
            : previousHead.UpdatedAtUtc.AddSeconds(1);
        var resultHead = HumanInputRequestStoreTestData.Head(
            request,
            previousHead.LifecycleVersion + 1,
            status,
            previousHead.ReminderCount,
            previousHead.SupersedesRequestId,
            previousHead.SupersededByRequestId,
            operationId,
            recordedAtUtc);
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actor, out _));
        Assert.True(AuthorityPurpose.TryParse("manage human input", out var reason, out _));
        var evidence = new HumanInputRequestLifecycleOperationEvidence(
            1,
            operationId,
            requestHash,
            kind,
            HumanInputRequestLifecycleOperationOutcome.Committed,
            HumanInputRequestLifecycleOperationFailureCode.None,
            request.RequestId,
            previousHead.LifecycleVersion,
            previousHead.Status,
            previousHead.CurrentRequest,
            request.Binding,
            previousHead,
            resultHead,
            null,
            null,
            null,
            null,
            actor!,
            reason!,
            null,
            HumanInputRequestStoreTestData.HashB,
            null,
            recordedAtUtc);
        return new HumanInputRequestLifecycleStoreMutation(generation, evidence, null, resultHead, null);
    }

    private static async Task<AgentRuntime> CreateRuntimeAsync(
        TestWorkspace workspace,
        AgentRuntimeSurface? runtimeSurface = null,
        string? codexPath = null,
        IAgentRuntimeAuthenticatedWakeVerifier? verifier = null,
        IAgentRuntimeHumanInputAuthorityProvider? humanInputAuthorityProvider = null,
        CommandActionRuntimeProvider? commandActionRuntimeProvider = null,
        IReadOnlyList<ModelProfileRuntimeProvider>? additionalModelProfileProviders = null,
        IAgentRuntimeGovernedLoopCoordinatorRepairAuthorityProvider? coordinatorRepairAuthorityProvider = null)
    {
        var executablePath = codexPath ?? await CreateFakeCodexExecutableAsync(workspace);
        var status = CreateCompatibleRuntimeStatus(executablePath);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            new RejectingApprovalPrompt(),
            workspace.ServerStatePath,
            status,
            additionalModelProfileProviders: additionalModelProfileProviders,
            commandActionRuntimeProvider: commandActionRuntimeProvider);
        if (verifier is not null)
        {
            factory = factory.WithAuthenticatedWakeVerifier(verifier);
        }

        if (humanInputAuthorityProvider is not null)
        {
            factory = factory.WithHumanInputAuthorityProvider(humanInputAuthorityProvider);
        }

        if (coordinatorRepairAuthorityProvider is not null)
        {
            factory = factory.WithGovernedLoopCoordinatorRepairAuthorityProvider(coordinatorRepairAuthorityProvider);
        }

        return await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            runtimeSurface ?? AgentRuntimeSurface.Cli);
    }

    private static GovernedLoopCoordinatorAcquisitionRequest ExpiredPeerAcquisition()
    {
        var observedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2).ToUniversalTime();
        var ownership = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(
            GovernedLoopCoordinatorOwnership.CurrentSchemaVersion,
            "local-background",
            "expired-peer",
            1,
            observedAtUtc,
            string.Empty));
        var lifecycle = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(
            GovernedLoopCoordinatorLifecycle.CurrentSchemaVersion,
            1,
            ownership,
            GovernedLoopCoordinatorStatus.Starting,
            observedAtUtc,
            null,
            string.Empty));
        var heartbeat = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(
            GovernedLoopCoordinatorHeartbeat.CurrentSchemaVersion,
            1,
            ownership,
            observedAtUtc,
            observedAtUtc.AddMinutes(1),
            string.Empty));
        return new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound,
            null,
            null,
            ownership,
            lifecycle,
            heartbeat);
    }

    private static async Task InstallModelProfileAsync(WorkspacePaths paths, string trustRootPath, CapabilityDescriptor descriptor)
    {
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, new FileCapabilityCatalogTrustProvider(trustRootPath)));
        var read = await service.ReadAsync(null, 1);
        Assert.Equal(CapabilityCatalogReadStatus.Available, read.Status);
        var revision = Assert.IsType<long>(read.Page?.CatalogRevision);
        revision = RequireApplied(await service.DeclareAsync(descriptor, revision, "declare-invocation-preparation-ready-profile"));
        revision = RequireApplied(await service.InstallAsync(descriptor.Id, revision, "install-invocation-preparation-ready-profile"));
        revision = RequireApplied(await service.VerifyAsync(descriptor.Id, revision, "verify-invocation-preparation-ready-profile"));
        revision = RequireApplied(await service.EnableAsync(descriptor.Id, revision, "enable-invocation-preparation-ready-profile"));
        _ = RequireApplied(await service.MarkHealthyAsync(descriptor.Id, revision, "healthy-invocation-preparation-ready-profile"));

        static long RequireApplied(CapabilityCatalogMutationResult result)
        {
            Assert.Equal(CapabilityCatalogMutationStatus.Applied, result.Status);
            return Assert.IsType<long>(result.CatalogRevision);
        }
    }

    private static GovernedLoopGraphCandidate BrowserGraphCandidate(ContextualRoleRevisionPin role)
    {
        const string ConversationTurnCapability = "org.embodysense/conversation-turn";
        var trigger = new GovernedLoopNodeDefinition(
            "trigger",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
            [
                new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                new GovernedLoopPortDefinition("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context, "text", true),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        var exit = new GovernedLoopNodeDefinition(
            "exit",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
            [
                new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
            ],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapability]),
            new Dictionary<string, string>());
        return new GovernedLoopGraphCandidate(
            1,
            "browser-governed-graph",
            "revision-1",
            "Publish one exact invocation value through the governed graph runtime.",
            role,
            trigger.Id,
            [exit.Id],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapability]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            [trigger, exit],
            [new GovernedLoopControlEdgeDefinition("trigger-to-exit", trigger.Id, exit.Id, GovernedLoopControlCondition.Always)],
            [new GovernedLoopBindingDefinition("request-to-result", GovernedLoopBindingKind.Data, trigger.Id, "request", exit.Id, "result")],
            new GovernedLoopOutputContract(
                "Return the exact invocation value.",
                [new GovernedLoopOutputDefinition("result", "text", exit.Id, "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Browser governed graph",
                "Exact durable Web authoring fixture.",
                [
                    new GovernedLoopNodeDisplayMetadata(trigger.Id, "Trigger", "Start.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata(exit.Id, "Exit", "Publish.", 200, 0),
                ]),
            EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
    }

    private static GovernedLoopGraphCandidate BrowserUnsupportedInvocationProjectionGraphCandidate(ContextualRoleRevisionPin role)
    {
        var source = BrowserGraphCandidate(role);
        var trigger = source.Nodes![0]!;
        var exit = source.Nodes[1]!;
        var transform = new GovernedLoopNodeDefinition(
            "identity",
            GovernedLoopSequentialNodeDescriptors.IdentityTransform,
            [
                new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        return source with
        {
            GraphId = "browser-unsupported-invocation-projection-graph",
            Nodes = [trigger, transform, exit],
            ControlEdges =
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-identity", trigger.Id, transform.Id, GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("identity-to-exit", transform.Id, exit.Id, GovernedLoopControlCondition.Success),
            ],
            Bindings =
            [
                new GovernedLoopBindingDefinition("request-to-identity", GovernedLoopBindingKind.Data, trigger.Id, "request", transform.Id, GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("identity-to-result", GovernedLoopBindingKind.Data, transform.Id, GovernedLoopPureNodeVocabulary.OutputPort, exit.Id, "result"),
            ],
            DisplayMetadata = new GovernedLoopDisplayMetadata(
                "Unsupported invocation projection graph",
                "A valid deterministic transform that this least-authority invocation slice deliberately excludes.",
                [
                    new GovernedLoopNodeDisplayMetadata(trigger.Id, "Trigger", "Start.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata(transform.Id, "Transform", "Identity.", 100, 0),
                    new GovernedLoopNodeDisplayMetadata(exit.Id, "Exit", "Publish.", 200, 0),
                ]),
        };
    }

    private static ContextualRoleRevision BrowserInvocationAcceptanceRole(WorkspacePaths paths, string modelProfileCapabilityId = BuiltInCapabilityCatalog.CodexModelProfileCapabilityId)
    {
        var revision = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity("browser-invocation-acceptance", 1),
            string.Empty,
            "Browser invocation acceptance",
            "Own one bounded visible governed invocation acceptance graph.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("test-author", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new ContextualRoleWorkspaceApplicability([CapabilityWorkspaceScopeId.Create(paths.RootPath)]),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown,
                "role",
                ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima([
                "org.embodysense/conversation-turn",
                "org.embodysense/model-inference",
                modelProfileCapabilityId,
            ]));
        return ContextualRoleRevisionContentHash.Apply(revision);
    }

    private static GovernedLoopGraphCandidate BrowserInvocationAcceptanceGraphCandidate(ContextualRoleRevisionPin role, string modelProfileCapabilityId = BuiltInCapabilityCatalog.CodexModelProfileCapabilityId)
    {
        const string ConversationTurnCapability = "org.embodysense/conversation-turn";
        const string ModelInferenceCapability = "org.embodysense/model-inference";
        var trigger = new GovernedLoopNodeDefinition(
            "trigger",
            GovernedLoopSequentialNodeDescriptors.ManualTrigger,
            [
                new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                new GovernedLoopPortDefinition("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context, "text", true),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        var inference = new GovernedLoopNodeDefinition(
            "inference",
            GovernedLoopSequentialNodeDescriptors.ProviderInference,
            [
                new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                new GovernedLoopPortDefinition("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context, "text", true),
                new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
            ],
            GovernedLoopAuthorityCeiling.Create([ModelInferenceCapability, modelProfileCapabilityId]),
            new Dictionary<string, string>
            {
                ["instruction"] = "Answer the bounded visible invocation.",
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "2",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "5000",
            },
            RetryPolicy: GovernedLoopRetryContract.CreatePolicy(
                "browser-invocation-bounded-retry",
                "inference",
                [GovernedLoopFailureClass.DispatchProvedNotStarted],
                ["provider-dispatch-not-started"],
                2,
                1_000,
                5_000,
                GovernedLoopRetryBackoffStrategy.Fixed,
                100,
                100,
                GovernedLoopRetryJitterStrategy.None,
                0,
                maximumResourceUnits: 2));
        var validate = new GovernedLoopNodeDefinition(
            "validate",
            GovernedLoopSequentialNodeDescriptors.SchemaConformance,
            [
                new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "boolean", true),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>
            {
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "2",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "5000",
            });
        var condition = new GovernedLoopNodeDefinition(
            "condition",
            GovernedLoopSequentialNodeDescriptors.BooleanCondition,
            [new GovernedLoopPortDefinition(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "boolean", true)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>
            {
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "2",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "5000",
            });
        var exit = new GovernedLoopNodeDefinition(
            "exit",
            GovernedLoopSequentialNodeDescriptors.SuccessExit,
            [
                new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
            ],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapability]),
            new Dictionary<string, string>());
        var fail = new GovernedLoopNodeDefinition(
            "fail",
            GovernedLoopSequentialNodeDescriptors.FailTerminal,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>
            {
                [GovernedLoopFailNodeVocabulary.CodeParameter] = "validation-rejected",
                [GovernedLoopFailNodeVocabulary.ExplanationParameter] = "The bounded validation rejected the result.",
            });
        return new GovernedLoopGraphCandidate(
            1,
            "browser-invocation-acceptance-graph",
            "revision-1",
            "Execute one bounded visible graph with deterministic validation and retry.",
            role,
            trigger.Id,
            [exit.Id, fail.Id],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapability, ModelInferenceCapability, modelProfileCapabilityId]),
            [
                new GovernedLoopValueSchemaDefinition("boolean", GovernedLoopValueKind.Boolean, false),
                new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false),
            ],
            [trigger, inference, validate, condition, exit, fail],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-inference", trigger.Id, inference.Id, GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("inference-to-validate", inference.Id, validate.Id, GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("inference-failure-to-fail", inference.Id, fail.Id, GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("validate-to-condition", validate.Id, condition.Id, GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("condition-true-to-exit", condition.Id, exit.Id, GovernedLoopControlCondition.True),
                new GovernedLoopControlEdgeDefinition("condition-false-to-inference", condition.Id, inference.Id, GovernedLoopControlCondition.False),
            ],
            [
                new GovernedLoopBindingDefinition("request-to-inference", GovernedLoopBindingKind.Data, trigger.Id, "request", inference.Id, "request"),
                new GovernedLoopBindingDefinition("context-to-inference", GovernedLoopBindingKind.Context, trigger.Id, "invocation-context", inference.Id, "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-validate", GovernedLoopBindingKind.Data, inference.Id, "result", validate.Id, GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("validation-to-condition", GovernedLoopBindingKind.Data, validate.Id, GovernedLoopPureNodeVocabulary.ResultPort, condition.Id, GovernedLoopTopologyNodeVocabulary.ValuePort),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, inference.Id, "result", exit.Id, "result"),
            ],
            new GovernedLoopOutputContract("Return the validated inference result.", [new GovernedLoopOutputDefinition("result", "text", exit.Id, "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Browser invocation acceptance graph",
                "Manual inference, validation, condition, bounded retry, and explicit terminals.",
                [
                    new GovernedLoopNodeDisplayMetadata(trigger.Id, "Trigger", "Start.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata(inference.Id, "Inference", "Infer with bounded retry.", 100, 0),
                    new GovernedLoopNodeDisplayMetadata(validate.Id, "Validate", "Validate the inference result.", 200, 0),
                    new GovernedLoopNodeDisplayMetadata(condition.Id, "Condition", "Choose the terminal path.", 300, 0),
                    new GovernedLoopNodeDisplayMetadata(exit.Id, "Exit", "Publish.", 400, 0),
                    new GovernedLoopNodeDisplayMetadata(fail.Id, "Fail", "Stop safely.", 400, 100),
                ]),
            BrowserInvocationRoutingPolicy(modelProfileCapabilityId));
    }

    private static GovernedModelRoutingPolicy BrowserInvocationRoutingPolicy(string modelProfileCapabilityId)
    {
        Assert.True(CapabilityId.TryParse(modelProfileCapabilityId, out var exactProfileId, out _));
        Assert.True(CapabilityDataClass.TryParse("sensitive", out var sensitiveData, out _));
        var privacy = GovernedModelPrivacyRequirement.Create(
            1,
            localOnly: true,
            CapabilityEgressMode.None,
            [],
            [sensitiveData!],
            ["local"],
            GovernedModelRetentionPosture.None,
            GovernedModelTrainingPosture.Prohibited);
        var unbounded = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Unbounded);
        return GovernedModelRoutingPolicy.Create(
            1,
            GovernedModelRoutingSelector.Exact(exactProfileId!),
            [],
            GovernedModelProfileRequirements.Create(
                1,
                [GovernedModelModality.Text],
                [],
                1,
                1,
                privacy,
                GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded)));
    }

    private static AuthorityProfile CreateProfileOnlyRecoveryRecord(
        GovernedLoopRevisionPublicationPin publication,
        string semanticHash,
        string operationId,
        DateTimeOffset issuedAtUtc)
    {
        var basis = string.Join(
            "\n",
            "governed-loop-invocation-v1",
            operationId,
            semanticHash,
            publication.Revision.GraphId,
            publication.Revision.RevisionId,
            publication.Revision.ExecutableHash,
            publication.PublicationOperationId,
            publication.ValidationEvidenceHash);
        var digest = HashInvocationTestValue(basis);
        Assert.True(AuthorityProfileId.TryParse("invocation-profile-" + digest, out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var revision, out _));
        var descriptor = Assert.Single(BuiltInCapabilityCatalog.Descriptors, item => string.Equals(item.Id.Value, "org.embodysense/conversation-turn", StringComparison.Ordinal));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        Assert.True(AuthorityActorId.TryParse("embodysense.web", out var actor, out _));
        Assert.True(AuthorityPurpose.TryParse("governed-loop-invocation", out var purpose, out _));
        return new AuthorityProfile(
            AuthorityProfile.CurrentSchemaVersion,
            profileId!,
            revision!,
            AuthorityProfileStatus.Active,
            purpose!,
            new AuthorityProvenance(actor!, AuthorityProvenanceKind.UserDeclaration),
            issuedAtUtc,
            null,
            new AuthorityCeiling([identity!], [], 0, CapabilitySideEffectClass.None, false, false, false),
            []);
    }

    private static GovernedLoopEffectAuthorityCompletionUsageRequest CompletionUsage(
        AuthorityGrantReference grant,
        string runId,
        string operationId)
        => new(
            GovernedLoopEffectAuthorityCompletionUsageRequest.CurrentSchemaVersion,
            grant,
            new string('a', 64),
            runId,
            1,
            operationId,
            DateTimeOffset.UtcNow);

    private static string HashInvocationTestValue(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<AgentRuntime> CreateRuntimeWithLiveDiscoveryAsync(TestWorkspace workspace)
    {
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        return await AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath).CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Cli);
    }

    private static CodexRuntimeStatus CreateCompatibleRuntimeStatus(string executablePath)
    {
        return new CodexRuntimeStatus(
            CodexRuntimeCompatibility.Compatible,
            executablePath,
            Path.GetFullPath(executablePath),
            "codex-cli 999.0.0-test",
            "test-model",
            "controlled test",
            "The isolated fake provider is pre-admitted for this runtime behavior test.");
    }

    private static GovernedLoopSleepCheckpoint AuthenticatedEventCheckpoint(DateTimeOffset publishedAtUtc)
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph-authenticated-wake", "revision-authenticated-wake", new string('1', 64));
        var execution = GovernedLoopExecutionBinding.Create(1, "run-authenticated-wake", revision, 1);
        var publication = new GovernedLoopRevisionPublicationPin(
            1,
            revision,
            "publication-authenticated-wake",
            new string('2', 64));
        var binding = new GovernedLoopSleepBinding(
            execution,
            publication,
            1,
            new string('3', 64),
            1,
            null,
            null,
            "wait-authenticated-wake",
            1,
            1,
            "wait-operation-authenticated-wake");
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            GovernedLoopSleepCheckpoint.CurrentSchemaVersion,
            string.Empty,
            binding,
            GovernedLoopWakeMode.AuthenticatedEvent,
            null,
            "event-subscription-authenticated-wake",
            publishedAtUtc,
            string.Empty));
    }

    private sealed class RejectingApprovalPrompt : IAgentToolApprovalPrompt
    {
        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((false, "test", "No approval needed during runtime construction."));
        }
    }

    private sealed class NoopConversationPublicationObserver : IAgentRuntimeConversationPublicationObserver
    {
        public Task PublicationCommittedAsync(AgentRuntimeConversationPublication publication, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(publication);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAuthenticatedWakeVerifier(
        AgentRuntimeAuthenticatedWakeVerificationStatus status = AgentRuntimeAuthenticatedWakeVerificationStatus.NotFound) : IAgentRuntimeAuthenticatedWakeVerifier
    {
        private readonly AgentRuntimeAuthenticatedWakeVerificationStatus _status = status;

        internal int VerifyCount { get; private set; }

        internal AgentRuntimeAuthenticatedWakeVerificationRequest? LastRequest { get; private set; }

        public Task<AgentRuntimeAuthenticatedWakeVerificationResult?> VerifyAsync(
            AgentRuntimeAuthenticatedWakeVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCount++;
            LastRequest = request;
            return Task.FromResult<AgentRuntimeAuthenticatedWakeVerificationResult?>(
                new AgentRuntimeAuthenticatedWakeVerificationResult(
                    _status));
        }
    }

    private sealed class FixedTriggerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FileCurrentTriggerEvidenceAuthorizer(string path) : ITriggerWorkerCurrentEvidenceAuthorizer
    {
        internal int Reads { get; private set; }

        internal TriggerWorkerCurrentEvidenceInput? LastInput { get; private set; }

        public async Task<TriggerWorkerAuthorizationResponse> AuthorizeAsync(TriggerWorkerCurrentEvidenceInput input, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
        {
            Reads++;
            LastInput = input;
            var status = await File.ReadAllTextAsync(path, cancellationToken);
            return new TriggerWorkerAuthorizationResponse(status, new string('a', 64), $"Current evidence reread for {input.DeliveryId} at {evaluatedAtUtc:O}.");
        }
    }

}
