using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task CreateAsync_starts_with_fresh_transcript_without_exposing_runtime_internals()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await new ConversationMemoryStore(paths).AppendMessageAsync(LlmMessage.User("old transcript"));

        await using var runtime = await CreateRuntimeAsync(workspace);

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
        File.Delete(Path.Combine(runDirectory, "run-one.json"));
        var customLoop = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput("loop-one", 1, new string('a', CustomLoopLimits.Sha256HexCharacters), "invoke-after-recovery-failure", "prompt"));

        Assert.Collection(preserved, message => Assert.Equal("preserved recovery-failure transcript", message.Content));
        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, turn.Status);
        Assert.Equal("Failed", customLoop.AdmissionStatus);
        Assert.Contains("custom_loop_recovery_failed", customLoop.Detail, StringComparison.Ordinal);
        Assert.False(customLoop.WasDispatched);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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

    private static async Task<string> CreateFakeCodexExecutableAsync(TestWorkspace workspace, string? turnFailureMessage = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The fake Codex app-server executable is currently implemented as a Windows command script.");
        }

        var scriptPath = workspace.File("fake-codex.ps1");
        var commandPath = workspace.File("fake-codex.cmd");
        await File.WriteAllTextAsync(scriptPath, $$"""
            if ($args -contains "--version") {
                Write-Output "codex-cli 999.0.0-test"
                exit 0
            }

            $threadId = "thread-test"
            $developerInstructions = ""
            $turnFailureMessage = {{FormatPowerShellStringLiteral(turnFailureMessage)}}

            function Write-ProtocolJson($value) {
                $value | ConvertTo-Json -Compress -Depth 20
                [Console]::Out.Flush()
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                $message = $line | ConvertFrom-Json

                switch ($message.method) {
                    "initialize" {
                        Write-ProtocolJson @{ id = $message.id; result = @{} }
                    }

                    "initialized" {
                    }

                    "model/list" {
                        Write-ProtocolJson @{ id = $message.id; result = @{ data = @(@{ id = "test-model"; model = "test-model" }, @{ id = "gpt-test"; model = "gpt-test" }) } }
                    }

                    "thread/start" {
                        $developerInstructions = [string]$message.params.developerInstructions
                        Write-ProtocolJson @{ id = $message.id; result = @{ thread = @{ id = $threadId } } }
                    }

                    "turn/start" {
                        $turnId = "turn-test"
                        $userText = [string]$message.params.input[0].text
                        $prefix = if ($developerInstructions.Contains("runtime guide") -or $userText.Contains("runtime guide")) { "runtime guide observed" } else { "runtime guide missing" }
                        $currentUserMarker = "Current user message:"
                        $currentUserIndex = $userText.IndexOf($currentUserMarker)
                        if ($currentUserIndex -ge 0) {
                            $userText = $userText.Substring($currentUserIndex + $currentUserMarker.Length).Trim()
                        }
                        $text = "${prefix}: $userText"

                        Write-ProtocolJson @{ id = $message.id; result = @{ turn = @{ id = $turnId } } }
                        if ($turnFailureMessage) {
                            Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = $turnId; turn = @{ id = $turnId; status = "failed"; error = @{ message = $turnFailureMessage }; items = @() } } }
                            break
                        }

                        Write-ProtocolJson @{ method = "item/agentMessage/delta"; params = @{ threadId = $threadId; turnId = $turnId; delta = $text } }
                        Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = $turnId; turn = @{ id = $turnId; status = "completed"; items = @(@{ type = "agentMessage"; phase = "final_answer"; text = $text }) } } }
                    }
                }
            }
            """);
        await File.WriteAllTextAsync(commandPath, """
            @echo off
            powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-codex.ps1" %*
            """);

        return commandPath;
    }

    private static string FormatPowerShellStringLiteral(string? value)
    {
        return value is null ? "$null" : "'" + value.Replace("'", "''") + "'";
    }

    private static async Task<AgentRuntime> CreateRuntimeAsync(TestWorkspace workspace, AgentRuntimeSurface? runtimeSurface = null, string? codexPath = null)
    {
        return await AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath).CreateAsync(
            "test-model",
            workspace.RootPath,
            codexPath ?? await CreateFakeCodexExecutableAsync(workspace),
            "read-only",
            runtimeSurface ?? AgentRuntimeSurface.Cli);
    }

    private sealed class RejectingApprovalPrompt : IAgentToolApprovalPrompt
    {
        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((false, "test", "No approval needed during runtime construction."));
        }
    }

}
