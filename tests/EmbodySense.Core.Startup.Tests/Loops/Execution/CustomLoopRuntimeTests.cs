using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class CustomLoopRuntimeTests
{
    [Fact]
    public async Task Context_capture_bounds_selected_conversation_entries_and_aggregates_all_omissions_once()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: true, "create-runtime-entry-cap", "update-runtime-entry-cap");
        await using var runtime = await CreateRuntimeAsync(workspace);
        for (var index = 0; index < (CustomLoopLimits.MaxInvokingConversationEntries / 2) + 1; index++)
        {
            _ = await runtime.RunTurnAsync("x");
        }

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-entry-cap", "entry cap test"));
        var conversation = response.Run!.Context.SourceManifest.Where(source => source.SourceType == "InvokingConversation").ToArray();

        Assert.InRange(conversation.Count(source => source.OmissionReason is null), 1, CustomLoopLimits.MaxInvokingConversationEntries);
        var omission = Assert.Single(conversation, source => source.OmissionReason is not null);
        Assert.Equal("invoking-conversation-omitted", omission.SourceId);
        Assert.Contains("message(s) were omitted", omission.OmissionReason, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, response.Run.Context.SourceManifest.Count), response.Run.Context.SourceManifest.Select(source => source.Order));
    }

    [Fact]
    public async Task Public_runtime_admits_executes_publishes_and_exposes_inspectable_artifacts_without_changing_default_turns()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File(".agent", "AGENT.md"), "role context evidence");
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-success", "update-runtime-success");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var prior = await runtime.RunTurnAsync("prior logical prompt");
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-success", "custom task");

        var response = await runtime.InvokeCustomLoopAsync(input);
        var fetched = await runtime.GetCustomLoopRunAsync(response.Run!.Id);
        var listed = await runtime.ListCustomLoopRunsAsync();
        var replay = await runtime.InvokeCustomLoopAsync(input);
        var persistedConversationAfterReplay = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationAsync();
        var ordinaryTurnAfterCustomRun = await runtime.RunTurnAsync("ordinary turn still works");

        Assert.Equal("MessageCompleted", prior.Status.ToString());
        Assert.Equal("Admitted", response.AdmissionStatus);
        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.True(response.WasDispatched);
        Assert.Equal("Completed", response.Run.Status);
        Assert.Equal("OpenAiCodex", response.Run.Model.Provider);
        Assert.Equal("test-model", response.Run.Model.Model);
        Assert.Equal(definition.ContentHash, response.Run.AdmittedDefinition.ContentHash);
        Assert.Equal("current", response.Run.InvokingConversation!.ConversationId);
        Assert.Empty(response.Run.Context.InvokingConversationMessages);
        var agentSource = Assert.Single(response.Run.Context.SourceManifest, source => source.SourceId == "agent");
        Assert.Equal("RoleInstruction", agentSource.SourceType);
        Assert.Equal("TrustedInstruction", agentSource.TrustClass);
        Assert.Contains("role context evidence", agentSource.Content, StringComparison.Ordinal);
        var startedAttempt = Assert.Single(response.Run.Events, runEvent => runEvent.Kind == "NodeAttemptStarted");
        Assert.NotEmpty(startedAttempt.ContextBlocks);
        Assert.NotNull(startedAttempt.ToolAuthority);
        Assert.Empty(startedAttempt.ToolAuthority.EffectiveAssignments);
        Assert.Null(startedAttempt.ToolEvidence);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == true);
        Assert.NotNull(response.Run.FinalOutput);
        AssertInspectableProjection(response.Run, Assert.Single(listed, summary => summary.Id == response.Run.Id));
        Assert.Equal(response.Run.Id, fetched!.Id);
        Assert.Equal(response.Run.Status, fetched.Status);
        Assert.Equal(response.Run.Events.Count, fetched.Events.Count);
        Assert.Equal(response.Run.Context.ManifestHash, fetched.Context.ManifestHash);
        Assert.Contains(listed, summary => summary.Id == response.Run.Id && summary.Status == "Completed");

        Assert.Equal("Admitted", replay.AdmissionStatus);
        Assert.Equal("Completed", replay.ExecutionStatus);
        Assert.False(replay.WasDispatched);
        Assert.Equal(response.Run.Id, replay.Run!.Id);
        Assert.Equal(response.Run.Events.Count, replay.Run.Events.Count);
        Assert.Equal(3, persistedConversationAfterReplay.Count);
        Assert.Equal(response.Run.FinalOutput, persistedConversationAfterReplay[^1].Content);
        Assert.Equal("MessageCompleted", ordinaryTurnAfterCustomRun.Status.ToString());
        Assert.Equal("default-conversation", ordinaryTurnAfterCustomRun.RunIdentity!.LoopId);
    }

    [Fact]
    public async Task Runtime_publishes_multiple_node_and_Exit_outputs_against_the_admission_prefix_plus_the_exact_durable_run_suffix()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var facade = new LoopAuthoringFacade(workspace.RootPath, WorkspaceActors.Cli);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-runtime-sequential-publication")).Definition);
        var publishInference = new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, new LoopContextPolicy(created.ContextDefaults.Inference.ContextIn, new LoopContextOutputPolicy(true, true)));
        var publishExit = new LoopNodeContextPolicy(LoopContextPolicyMode.Custom, new LoopContextPolicy(created.ContextDefaults.Exit.ContextIn, new LoopContextOutputPolicy(false, true)));
        var input = new LoopDefinitionInput(
            "Sequential publication loop",
            "Publishes two inference outputs and the terminal result.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [
                new LoopInferenceStep(created.InferenceSteps.Single().Id, "First", "Produce the first result.", publishInference),
                new LoopInferenceStep(null, "Second", "Produce the second result.", publishInference)
            ],
            [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, publishExit));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, "update-runtime-sequential-publication", input);
        var definition = Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
        await using var runtime = await CreateRuntimeAsync(workspace);

        var response = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-sequential-publication", "publish sequentially"));
        var persistedConversation = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationAsync();
        var publications = response.Run!.Events.Where(item => item.Kind == "ConversationPublished" && item.PublishedToInvokingConversation == true).ToArray();

        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.Equal(3, publications.Length);
        Assert.Equal(3, persistedConversation.Count);
        Assert.Equal(publications.Select(item => item.CanonicalOutput), persistedConversation.Select(item => item.Content));
    }

    [Fact]
    public async Task Admission_captures_bounded_labeled_role_sources_and_a_versioned_newest_conversation_snapshot()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var roleSource = new string('R', 12_050);
        await File.WriteAllTextAsync(workspace.File(".agent", "AGENT.md"), roleSource);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: true, "create-runtime-context", "update-runtime-context");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var oversizedPrompt = "prompt-head-" + new string('x', CustomLoopLimits.MaxInvokingConversationCharacters + 500) + "-prompt-tail";
        _ = await runtime.RunTurnAsync(oversizedPrompt);

        var first = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-context-1", "first custom task"));
        var second = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-context-2", "second custom task"));

        var manifest = first.Run!.Context.SourceManifest;
        Assert.Equal(["nearest-agents", "agent", "soul", "personality", "context", "memory", "models"], manifest.Take(7).Select(source => source.SourceId));
        Assert.Equal(Enumerable.Range(1, manifest.Count), manifest.Select(source => source.Order));
        var missingAgents = manifest[0];
        Assert.False(string.IsNullOrWhiteSpace(missingAgents.OmissionReason));
        Assert.Equal(string.Empty, missingAgents.Content);
        Assert.Equal(0, missingAgents.UsedCharacterCount);
        Assert.False(missingAgents.Truncated);
        var agentManifestSource = Assert.Single(manifest, source => source.SourceId == "agent");
        Assert.Equal("RoleInstruction", agentManifestSource.SourceType);
        Assert.Equal("WorkspaceRoleFile", agentManifestSource.Provenance);
        Assert.Equal("TrustedInstruction", agentManifestSource.TrustClass);
        Assert.Equal("system", agentManifestSource.Role);
        Assert.EndsWith(".agent/AGENT.md", agentManifestSource.SourcePath.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Equal(CustomLoopLimits.MaxInstructionCharacters, agentManifestSource.UsedCharacterCount);
        Assert.Equal(agentManifestSource.UsedCharacterCount, agentManifestSource.Content.Length);
        Assert.True(agentManifestSource.OriginalCharacterCount > agentManifestSource.UsedCharacterCount);
        Assert.True(agentManifestSource.Truncated);
        Assert.NotNull(agentManifestSource.TruncationReason);
        Assert.Null(agentManifestSource.OmissionReason);
        Assert.Contains("[source truncated to fit the 12000-character admitted source limit]", agentManifestSource.Content, StringComparison.Ordinal);
        var contextualState = Assert.Single(manifest, source => source.SourceId == "memory");
        Assert.Equal("ContextualState", contextualState.SourceType);
        Assert.Equal("WorkspaceContextFile", contextualState.Provenance);
        Assert.Equal("UntrustedData", contextualState.TrustClass);
        Assert.Equal("user", contextualState.Role);
        var conversationMessage = Assert.Single(manifest, source => source.SourceType == "InvokingConversation" && source.OmissionReason is null);
        Assert.Equal("LogicalConversation", conversationMessage.Provenance);
        Assert.Equal("UntrustedData", conversationMessage.TrustClass);
        Assert.Equal("user", conversationMessage.Role);
        Assert.Equal(CustomLoopLimits.MaxInvokingConversationCharacters, conversationMessage.UsedCharacterCount);
        Assert.True(conversationMessage.OriginalCharacterCount > conversationMessage.UsedCharacterCount);
        Assert.True(conversationMessage.Truncated);
        Assert.NotNull(conversationMessage.TruncationReason);
        Assert.Contains("[truncated to 24000 characters for invoking-conversation admission]", conversationMessage.Content, StringComparison.Ordinal);
        Assert.Contains("fake response: prompt-head-", conversationMessage.Content, StringComparison.Ordinal);
        Assert.EndsWith("-prompt-tail", conversationMessage.Content, StringComparison.Ordinal);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, first.Run.InvokingConversation!.CapturedVersion.Length);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, first.Run.Context.ManifestHash.Length);
        Assert.NotEqual(first.Run.InvokingConversation.CapturedVersion, second.Run!.InvokingConversation!.CapturedVersion);
        Assert.True(second.Run.Context.CapturedAtUtc >= first.Run.Context.CapturedAtUtc);
    }

    [Fact]
    public async Task Replay_of_a_valid_historical_run_without_an_invoking_conversation_reaches_admission_without_throwing_or_dispatching()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definitionSnapshot = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-null-conversation", "update-runtime-null-conversation");
        var paths = new WorkspacePaths(workspace.RootPath);
        var definition = Assert.IsType<CustomLoopDefinition>(await new CustomLoopDefinitionStore(paths).GetAsync(definitionSnapshot.Id));
        var now = DateTimeOffset.UtcNow;
        var context = CustomLoopContextSnapshot.CreateEmpty(now);
        var admittedEvent = new CustomLoopRunEvent(
            1,
            "event-legacy-no-conversation",
            now,
            CustomLoopRunEventKind.Admitted,
            null,
            null,
            null,
            "Historical admission without a conversation destination.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            "OpenAiCodex",
            "test-model",
            null,
            null);
        var admissionAuditCompleted = new CustomLoopRunEvent(
            2,
            "event-legacy-no-conversation-audit",
            now,
            CustomLoopRunEventKind.AdmissionAuditCompleted,
            null,
            null,
            null,
            "Historical admission audit completed.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-legacy-no-conversation",
            definition.Id,
            2,
            CustomLoopRunStatus.Admitted,
            now,
            now,
            null,
            "cli",
            new CustomLoopModelSnapshot("OpenAiCodex", "test-model"),
            "invoke-legacy-no-conversation",
            new string('0', CustomLoopLimits.Sha256HexCharacters),
            definition,
            "legacy prompt",
            null,
            context,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admittedEvent, admissionAuditCompleted],
            null,
            null,
            null);
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        var runStore = new CustomLoopRunStore(paths);
        var pendingAdmission = run with { LifecycleVersion = 1, Events = [admittedEvent] };
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await runStore.CreateAsync(pendingAdmission)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await runStore.UpdateAsync(run, expectedLifecycleVersion: 1)).Status);
        await using var runtime = await CreateRuntimeAsync(workspace);

        var replay = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, run.AdmissionOperationId, run.TriggerPrompt));

        Assert.Equal("Admitted", replay.AdmissionStatus);
        Assert.Equal("Paused", replay.ExecutionStatus);
        Assert.False(replay.WasDispatched);
        Assert.Null(replay.Run!.InvokingConversation);
        Assert.Equal(run.Id, replay.Run.Id);
    }

    [Fact]
    public async Task Conversation_publication_recognizes_the_exact_expected_prefix_plus_one_output_as_already_published()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-idempotent-publish", "update-runtime-idempotent-publish");
        const string prompt = "delayed idempotent task";
        var expectedOutput = "fake response: [EmbodySense untrusted trigger prompt data]" + Environment.NewLine + prompt;
        await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).AppendMessageAsync(LlmMessage.Assistant(expectedOutput));
        await using var runtime = await CreateRuntimeAsync(workspace);
        var invocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-idempotent-publish", prompt));
        await WaitForAttemptStartAsync(workspace);

        var history = await runtime.RunTurnAsync("/history");
        var loaded = await runtime.RunTurnAsync("1");
        var response = await invocation;
        var persistedConversation = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationAsync();

        Assert.Equal("CommandHandled", history.Status.ToString());
        Assert.Equal("CommandHandled", loaded.Status.ToString());
        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.Equal(expectedOutput, response.Run!.FinalOutput);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.Detail.Contains("already committed", StringComparison.Ordinal));
        Assert.Collection(persistedConversation, message => Assert.Equal(expectedOutput, message.Content));
    }

    [Fact]
    public async Task Conversation_publication_definitely_fails_when_the_logical_conversation_changes_after_admission()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-publication-conflict", "update-runtime-publication-conflict");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var invocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-publication-conflict", "delayed custom task"));
        await WaitForAttemptStartAsync(workspace);

        var interleavingTurn = await runtime.RunTurnAsync("interleaving ordinary turn");
        var response = await invocation;

        Assert.Equal("MessageCompleted", interleavingTurn.Status.ToString());
        Assert.Equal("Failed", response.ExecutionStatus);
        Assert.Equal("Failed", response.Run!.Status);
        Assert.Equal("conversation_publication_failed", response.Run.FailureCode);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == false);
    }

    [Fact]
    public async Task Conversation_append_exception_is_reconciled_as_definitely_failed_when_no_append_occurred()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-append-failure", "update-runtime-append-failure");
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var invocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-append-failure", "delayed append failure"));
        await WaitForAttemptStartAsync(workspace);
        File.SetAttributes(paths.CurrentConversationPath, FileAttributes.ReadOnly);

        LoopRunInvocationResponse response;
        try
        {
            response = await invocation;
        }
        finally
        {
            File.SetAttributes(paths.CurrentConversationPath, FileAttributes.Normal);
        }

        Assert.Equal("Failed", response.ExecutionStatus);
        Assert.Equal("conversation_publication_failed", response.Run!.FailureCode);
        Assert.Contains(response.Run.Events, runEvent => runEvent.Kind == "ConversationPublished" && runEvent.PublishedToInvokingConversation == false);
        Assert.Empty(await new ConversationMemoryStore(paths).LoadCurrentConversationAsync());
    }

    [Fact]
    public async Task Concurrent_different_loop_is_durably_rejected_as_workspace_busy_without_context_capture_or_hidden_queueing()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var firstDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-no-queue-1", "update-runtime-no-queue-1");
        var secondDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-no-queue-2", "update-runtime-no-queue-2");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var first = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(firstDefinition.Id, firstDefinition.DefinitionVersion, firstDefinition.ContentHash, "invoke-runtime-no-queue-1", "delayed queue owner"));
        await WaitForAttemptStartAsync(workspace);

        var secondInput = new LoopRunInvocationInput(secondDefinition.Id, secondDefinition.DefinitionVersion, secondDefinition.ContentHash, "invoke-runtime-no-queue-2", "second invocation");
        var second = runtime.InvokeCustomLoopAsync(secondInput);
        var firstCompletion = await Task.WhenAny(first, second);
        var rejected = await second;
        var completed = await first;
        var busyReplay = await runtime.InvokeCustomLoopAsync(secondInput);
        var changedContent = await runtime.InvokeCustomLoopAsync(secondInput with { InvocationPrompt = "changed invocation" });
        var runsBeforeFreshOperation = await runtime.ListCustomLoopRunsAsync();
        var admittedAfterRelease = await runtime.InvokeCustomLoopAsync(secondInput with { OperationId = "invoke-runtime-no-queue-3" });

        Assert.Same(second, firstCompletion);
        Assert.Equal("WorkspaceExecutionBusy", rejected.AdmissionStatus);
        Assert.False(rejected.WasDispatched);
        Assert.Null(rejected.Run);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal("WorkspaceExecutionBusy", busyReplay.AdmissionStatus);
        Assert.False(busyReplay.WasDispatched);
        Assert.Equal("Conflict", changedContent.AdmissionStatus);
        Assert.DoesNotContain(runsBeforeFreshOperation, run => run.LoopId == secondDefinition.Id);
        Assert.Equal("Completed", admittedAfterRelease.ExecutionStatus);
        var receiptPath = Path.Combine(new WorkspacePaths(workspace.RootPath).CustomLoopInvocationOperationsPath, secondInput.OperationId + ".json");
        Assert.True(File.Exists(receiptPath));
        Assert.Contains("workspaceExecutionBusy", await File.ReadAllTextAsync(receiptPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_same_operation_has_one_owner_and_replays_its_admitted_run_without_redispatch()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var definition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-same-operation", "update-runtime-same-operation");
        await using var runtime = await CreateRuntimeAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-runtime-same-operation", "delayed same operation owner");

        var first = runtime.InvokeCustomLoopAsync(input);
        await WaitForAttemptStartAsync(workspace);
        var concurrent = await runtime.InvokeCustomLoopAsync(input);
        var completed = await first;
        var replay = await runtime.InvokeCustomLoopAsync(input);

        Assert.Equal("Admitted", concurrent.AdmissionStatus);
        Assert.False(concurrent.WasDispatched);
        Assert.NotNull(concurrent.Run);
        Assert.Equal("Completed", completed.ExecutionStatus);
        Assert.Equal(completed.Run!.Id, concurrent.Run!.Id);
        Assert.Equal("Admitted", replay.AdmissionStatus);
        Assert.False(replay.WasDispatched);
        Assert.Equal(completed.Run.Id, replay.Run!.Id);
        Assert.Equal(completed.Run.Events.Count, replay.Run.Events.Count);
    }

    [Fact]
    public async Task Paused_run_releases_workspace_ownership_and_resume_busy_is_replayed_without_mutation_or_dispatch()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var pausedDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-paused-owner", "update-runtime-paused-owner");
        var competingDefinition = await CreateInvocationLoopAsync(workspace, includeInvokingConversation: false, "create-runtime-resume-busy", "update-runtime-resume-busy");
        await using var runtime = await CreateRuntimeAsync(workspace);

        var pausingInvocation = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(pausedDefinition.Id, pausedDefinition.DefinitionVersion, pausedDefinition.ContentHash, "invoke-runtime-paused-owner", "delayed pause owner"));
        await WaitForAttemptStartAsync(workspace);
        var running = Assert.Single(await runtime.ListCustomLoopRunsAsync(), run => run.LoopId == pausedDefinition.Id);
        var pause = await runtime.PauseCustomLoopAsync(new LoopRunControlInput(running.Id, (await runtime.GetCustomLoopRunAsync(running.Id))!.LifecycleVersion, "pause-runtime-owner"));
        var paused = await pausingInvocation;

        Assert.Equal("PauseRequested", pause.Status);
        Assert.Equal("Paused", paused.ExecutionStatus);
        Assert.Equal("Paused", paused.Run!.Status);

        File.Delete(workspace.File("custom-attempt-started.marker"));
        var competitor = runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(competingDefinition.Id, competingDefinition.DefinitionVersion, competingDefinition.ContentHash, "invoke-runtime-resume-competitor", "delayed resume competitor"));
        await WaitForAttemptStartAsync(workspace);
        var resumeInput = new LoopRunControlInput(paused.Run.Id, paused.Run.LifecycleVersion, "resume-runtime-busy");
        var busy = await runtime.ResumeCustomLoopAsync(resumeInput);
        var busyReplay = await runtime.ResumeCustomLoopAsync(resumeInput);
        var pausedAfterBusy = await runtime.GetCustomLoopRunAsync(paused.Run.Id);

        Assert.Equal("WorkspaceExecutionBusy", busy.Status);
        Assert.Equal("WorkspaceExecutionBusy", busyReplay.Status);
        Assert.Equal(paused.Run.LifecycleVersion, pausedAfterBusy!.LifecycleVersion);
        Assert.Equal("Paused", pausedAfterBusy.Status);
        Assert.Equal(paused.Run.ExecutionClock, pausedAfterBusy.ExecutionClock);
        Assert.Equal(paused.Run.Checkpoint.LastCommittedSequence, pausedAfterBusy.Checkpoint.LastCommittedSequence);
        Assert.Equal(paused.Run.Events.Count, pausedAfterBusy.Events.Count);

        var competingRun = Assert.Single(await runtime.ListCustomLoopRunsAsync(), run => run.LoopId == competingDefinition.Id);
        var competingDetail = (await runtime.GetCustomLoopRunAsync(competingRun.Id))!;
        _ = await runtime.CancelCustomLoopAsync(new LoopRunControlInput(competingRun.Id, competingDetail.LifecycleVersion, "cancel-runtime-resume-competitor"));
        var competitorOutcome = await competitor;
        Assert.Contains(competitorOutcome.Run!.Status, new[] { "Cancelled", "NeedsReview" });

        var resumed = await runtime.ResumeCustomLoopAsync(resumeInput with { OperationId = "resume-runtime-after-release" });
        Assert.Equal("Completed", resumed.Status);
        Assert.Equal("Completed", resumed.Run!.Status);
    }

    private static void AssertInspectableProjection(LoopRunSnapshot run, LoopRunSummarySnapshot summary)
    {
        Assert.Equal(CustomLoopRunRecord.CurrentSchemaVersion, run.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(run.Id));
        Assert.False(string.IsNullOrWhiteSpace(run.LoopId));
        Assert.True(run.LifecycleVersion > 1);
        Assert.Equal("Completed", run.Status);
        Assert.True(run.CreatedAtUtc <= run.UpdatedAtUtc);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Equal("cli", run.Surface);
        Assert.False(string.IsNullOrWhiteSpace(run.Model.Provider));
        Assert.Equal("test-model", run.Model.Model);
        Assert.False(string.IsNullOrWhiteSpace(run.AdmissionOperationId));
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, run.AdmissionRequestHash.Length);
        Assert.Equal(run.LoopId, run.AdmittedDefinition.Id);
        Assert.False(string.IsNullOrWhiteSpace(run.TriggerPrompt));
        Assert.NotNull(run.InvokingConversation);
        Assert.Equal(run.Context.CapturedAtUtc, run.InvokingConversation!.CapturedAtUtc);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, run.Context.ManifestHash.Length);
        Assert.NotEmpty(run.Context.DirectoryRoleMessages);
        Assert.Empty(run.Context.InvokingConversationMessages);
        Assert.True(run.ExecutionClock.AccumulatedRunningMilliseconds >= 0);
        Assert.Null(run.ExecutionClock.ActiveSinceUtc);
        Assert.Equal(1, run.Checkpoint.Iteration);
        Assert.Equal(1, run.Checkpoint.NextStepIndex);
        Assert.Equal(0, run.Checkpoint.AcceptedRepeatCount);
        Assert.False(run.Checkpoint.PendingExitDecision);
        Assert.NotEmpty(run.Checkpoint.EarlierRetainedOutputs);
        Assert.Null(run.Checkpoint.PreviousIterationResult);
        var retained = Assert.IsType<LoopRunRetainedOutputSnapshot>(run.Checkpoint.CurrentIterationResult);
        Assert.False(string.IsNullOrWhiteSpace(retained.StepId));
        Assert.Equal(1, retained.Iteration);
        Assert.Equal(run.FinalOutput, retained.Content);
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, retained.ContentHash.Length);
        Assert.Equal(0, run.Checkpoint.ToolRequestsUsed);
        Assert.True(run.Checkpoint.LastCommittedSequence > 0);
        Assert.Null(run.FailureCode);
        Assert.Null(run.FailureDetail);

        var attempt = Assert.Single(run.Events, runEvent => runEvent.Kind == "NodeAttemptStarted");
        Assert.True(attempt.Sequence > 0);
        Assert.False(string.IsNullOrWhiteSpace(attempt.EventId));
        Assert.True(attempt.TimestampUtc >= run.CreatedAtUtc);
        Assert.Equal(1, attempt.Iteration);
        Assert.False(string.IsNullOrWhiteSpace(attempt.StepId));
        Assert.Equal(1, attempt.Attempt);
        Assert.False(string.IsNullOrWhiteSpace(attempt.Detail));
        Assert.Null(attempt.CanonicalOutput);
        Assert.Null(attempt.OriginalOutputCharacterCount);
        Assert.Null(attempt.CanonicalOutputTruncated);
        Assert.Null(attempt.RetainedForLoopReasoning);
        Assert.Null(attempt.PublishedToInvokingConversation);
        Assert.Null(attempt.ConversationPublicationId);
        Assert.Equal("OpenAiCodex", attempt.Provider);
        Assert.Equal("test-model", attempt.Model);
        Assert.False(string.IsNullOrWhiteSpace(attempt.ProviderResponseId));
        Assert.Null(attempt.ExitDecision);
        var block = Assert.Single(attempt.ContextBlocks, contextBlock => contextBlock.Source == "HarnessGovernance");
        Assert.Equal("harness-governance", block.SourceId);
        Assert.Equal("system", block.Role);
        Assert.True(block.Included);
        Assert.Null(block.OmissionReason);
        Assert.False(string.IsNullOrWhiteSpace(block.Content));
        Assert.Equal(CustomLoopLimits.Sha256HexCharacters, block.ContentHash.Length);
        Assert.Equal(block.Content.Length, block.CharacterCount);
        Assert.False(block.Truncated);
        Assert.Equal(EmbodySenseDeveloperInstructions.CurrentVersion, block.SourceVersion);
        Assert.Equal(EmbodySenseDeveloperInstructions.Capture().Content, block.Content);

        Assert.Equal(run.Id, summary.Id);
        Assert.Equal(run.LoopId, summary.LoopId);
        Assert.Equal(run.AdmissionOperationId, summary.AdmissionOperationId);
        Assert.Equal(run.AdmittedDefinition.DefinitionVersion, summary.DefinitionVersion);
        Assert.Equal(run.Status, summary.Status);
        Assert.Equal(run.CreatedAtUtc, summary.CreatedAtUtc);
        Assert.Equal(run.UpdatedAtUtc, summary.UpdatedAtUtc);
        Assert.Equal(run.CompletedAtUtc, summary.CompletedAtUtc);
        Assert.Equal(run.Checkpoint.Iteration, summary.Iteration);
        Assert.Equal(run.Checkpoint.NextStepIndex, summary.NextStepIndex);
        Assert.Null(summary.FailureCode);
        Assert.False(summary.IsDeleted);
    }

    private static async Task<LoopDefinitionSnapshot> CreateInvocationLoopAsync(TestWorkspace workspace, bool includeInvokingConversation, string createOperationId, string updateOperationId)
    {
        var facade = new LoopAuthoringFacade(workspace.RootPath, WorkspaceActors.Cli);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync(createOperationId)).Definition);
        var input = new LoopDefinitionInput(
            "Runtime test loop",
            "Executes one governed inference step.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, includeInvokingConversation),
            [new LoopInferenceStep(created.InferenceSteps.Single().Id, "Respond", "Return a concise response to the admitted trigger prompt.", new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null))],
            [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, updateOperationId, input);

        Assert.Equal("Updated", updated.Status);
        return Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
    }

    private static async Task<AgentRuntime> CreateRuntimeAsync(TestWorkspace workspace)
    {
        return await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            "test-model",
            workspace.RootPath,
            await CreateFakeCodexExecutableAsync(workspace),
            "read-only",
            AgentRuntimeSurface.Cli);
    }

    private static async Task WaitForAttemptStartAsync(TestWorkspace workspace)
    {
        var markerPath = workspace.File("custom-attempt-started.marker");
        for (var attempt = 0; attempt < 100 && !File.Exists(markerPath); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(markerPath), "The delayed custom-loop provider attempt did not start within five seconds.");
    }

    private static async Task<string> CreateFakeCodexExecutableAsync(TestWorkspace workspace)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The fake Codex app-server executable is currently implemented as a Windows command script.");
        }

        var scriptPath = workspace.File("fake-custom-loop-codex.ps1");
        var commandPath = workspace.File("fake-custom-loop-codex.cmd");
        await File.WriteAllTextAsync(scriptPath, """
            $threadId = "thread-test"

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

                    "thread/start" {
                        Write-ProtocolJson @{ id = $message.id; result = @{ thread = @{ id = $threadId } } }
                    }

                    "turn/start" {
                        $turnId = "turn-test"
                        $userText = [string]$message.params.input[0].text
                        if ($userText.Contains("delayed")) {
                            [IO.File]::WriteAllText((Join-Path $PSScriptRoot "custom-attempt-started.marker"), "started")
                            Start-Sleep -Milliseconds 1500
                        }
                        $triggerMatch = [regex]::Match($userText, '(?s)(\[EmbodySense untrusted trigger prompt data\]\r?\n.*?)\r?\n\[/restored user message\]')
                        if ($triggerMatch.Success) {
                            $userText = $triggerMatch.Groups[1].Value
                        }
                        else {
                            $currentUserMarker = "Current user message:"
                            $currentUserIndex = $userText.IndexOf($currentUserMarker)
                            if ($currentUserIndex -ge 0) {
                                $userText = $userText.Substring($currentUserIndex + $currentUserMarker.Length).Trim()
                            }
                        }
                        $text = "fake response: $userText"

                        Write-ProtocolJson @{ id = $message.id; result = @{ turn = @{ id = $turnId } } }
                        Write-ProtocolJson @{ method = "item/agentMessage/delta"; params = @{ threadId = $threadId; turnId = $turnId; delta = $text } }
                        Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = $turnId; turn = @{ id = $turnId; status = "completed"; items = @(@{ type = "agentMessage"; phase = "final_answer"; text = $text }) } } }
                    }
                }
            }
            """);
        await File.WriteAllTextAsync(commandPath, """
            @echo off
            powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-custom-loop-codex.ps1" %*
            """);

        return commandPath;
    }

    private sealed class RejectingApprovalPrompt : IAgentToolApprovalPrompt
    {
        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((false, "test", "No tool authority is assigned in these runtime tests."));
        }
    }
}
