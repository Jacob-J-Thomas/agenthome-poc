using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Loops;
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
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.GraphAuthoring;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Loops.Posture.Models;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Startup.Triggers;
using EmbodySense.Core.Startup.Triggers.Models;
using EmbodySense.Core.Startup.Tests.Triggers;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed class AgentRuntimeFactoryTests
{
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
    public async Task CreateAsync_exposes_role_bound_graph_authoring_catalog_create_and_exact_reload()
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
        Assert.Equal("committed", created.Status);
        Assert.Matches("^[0-9a-f]{64}$", created.AuthoringRequestHash);
        Assert.Matches("^[0-9a-f]{64}$", created.GraphValidationEvidenceHash);
        Assert.Equal("ready", reloaded.Status);
        Assert.Equal(candidate.GraphId, reloaded.Lifecycle?.GraphId);
        Assert.Equal(candidate.RevisionId, reloaded.Lifecycle?.DraftRevision?.RevisionId);
        Assert.Single(reloaded.Artifacts);
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
        var authoring = new LoopAuthoringFacade(workspace.RootPath, WorkspaceActors.Cli);
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

    private static async Task<string> CreateFakeCodexExecutableAsync(TestWorkspace workspace, string? turnFailureMessage = null, string? turnStartMarkerPath = null)
    {
        var scriptPath = workspace.File("fake-codex.js");
        var commandPath = workspace.File(OperatingSystem.IsWindows() ? "fake-codex.cmd" : "fake-codex");
        var serializedTurnFailureMessage = System.Text.Json.JsonSerializer.Serialize(turnFailureMessage);
        var serializedTurnStartMarkerPath = System.Text.Json.JsonSerializer.Serialize(turnStartMarkerPath);
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
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            let developerInstructions = "";

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            input.on("line", line => {
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

    private static async Task<AgentRuntime> CreateRuntimeAsync(
        TestWorkspace workspace,
        AgentRuntimeSurface? runtimeSurface = null,
        string? codexPath = null,
        IAgentRuntimeAuthenticatedWakeVerifier? verifier = null)
    {
        var executablePath = codexPath ?? await CreateFakeCodexExecutableAsync(workspace);
        var status = CreateCompatibleRuntimeStatus(executablePath);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, status);
        if (verifier is not null)
        {
            factory = factory.WithAuthenticatedWakeVerifier(verifier);
        }

        return await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            runtimeSurface ?? AgentRuntimeSurface.Cli);
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

    private sealed class RecordingAuthenticatedWakeVerifier : IAgentRuntimeAuthenticatedWakeVerifier
    {
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
                    AgentRuntimeAuthenticatedWakeVerificationStatus.NotFound));
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
