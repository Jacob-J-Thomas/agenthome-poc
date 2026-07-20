using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Governance;
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(paths.CurrentConversationPath, "old transcript" + Environment.NewLine);

        await using var runtime = await CreateRuntimeAsync(workspace);

        Assert.Equal(string.Empty, await File.ReadAllTextAsync(paths.CurrentConversationPath));
        Assert.NotEmpty(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
        Assert.True(File.Exists(paths.ConversationTurnLockPath));
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
    public async Task CreateAsync_requires_explicit_runtime_surface()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var fakeCodex = await CreateFakeCodexExecutableAsync(workspace);

        await Assert.ThrowsAsync<ArgumentNullException>(() => new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            null,
            workspace.RootPath,
            fakeCodex,
            "read-only",
            null!));
    }

    [Fact]
    public async Task RunTurnAsync_uses_startup_context_and_streams_response_through_public_runtime()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, ".agent", "AGENT.md"), "runtime guide");
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
    }

    [Fact]
    public async Task CreateAsync_keeps_ordinary_chat_available_when_another_process_owns_custom_loop_hosting()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        using var ownership = new FileStream(paths.CustomLoopHostLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        await using var runtime = await CreateRuntimeAsync(workspace);

        var preserved = await conversationMemory.LoadCurrentConversationAsync();
        var customLoop = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-one", "prompt"));
        var replay = await runtime.InvokeCustomLoopAsync(replayInput);
        var replayedResume = await runtime.ResumeCustomLoopAsync(replayResumeInput);
        var replayedCancel = await runtime.CancelCustomLoopAsync(replayCancelInput);
        var blockedResume = await runtime.ResumeCustomLoopAsync(new LoopRunControlInput("run-one", 1, "resume-one"));
        var blockedCancel = await runtime.CancelCustomLoopAsync(new LoopRunControlInput("run-one", 1, "cancel-one"));
        var turn = await runtime.RunTurnAsync("hello");
        ownership.Dispose();
        var afterRelease = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-two", "prompt"));
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
        Assert.Equal("WorkspaceHostUnavailable", blockedCancel.Status);
        Assert.Equal("cancel-one", blockedCancel.OperationId);
        Assert.Null(await new CustomLoopControlOperationStore(paths).GetAsync(blockedCancel.OperationId));
        Assert.Equal("WorkspaceHostUnavailable", afterRelease.AdmissionStatus);
        Assert.Equal("NotFound", afterRecreate.AdmissionStatus);
    }

    [Fact]
    public async Task CreateAsync_keeps_ordinary_chat_available_while_an_in_process_custom_loop_owns_execution()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
    public async Task RunTurnAsync_returns_failed_runtime_result_with_loop_identity_when_provider_fails()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var fakeCodex = await CreateFakeCodexExecutableAsync(workspace, "provider exploded");
        await using var runtime = await CreateRuntimeAsync(workspace, codexPath: fakeCodex);

        var response = await runtime.RunTurnAsync("hello");
        var history = await runtime.RunTurnAsync("/history");

        Assert.Equal(AgentRuntimeTurnStatus.MessageFailed, response.Status);
        Assert.Equal("Codex app-server turn failed: provider exploded", response.FailureDetail);
        Assert.Equal(response.FailureDetail, response.Output);
        var failureEvent = Assert.Single(response.Events);
        Assert.Equal(AgentRuntimeTurnEventKind.Failure, failureEvent.Kind);
        Assert.Equal(response.FailureDetail, failureEvent.Text);
        Assert.Equal(response.RunIdentity, failureEvent.RunIdentity);
        Assert.NotNull(response.RunIdentity);
        Assert.Equal("default-conversation", response.RunIdentity.LoopId);
        Assert.Equal("default-assistant", response.RunIdentity.RoleId);
        Assert.Contains("before sending the first prompt", history.Output, StringComparison.Ordinal);
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
        var requestHash = CustomLoopInvocationRequestHash.Compute(input.OperationId, input.LoopId, input.ExpectedDefinitionVersion, input.ExpectedDefinitionHash, WorkspaceActors.Cli, AgentRuntimeSurface.Cli.Id, "default-assistant", prompt, LlmInferenceSurface.OpenAiCodex.ToString(), null);
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
            null,
            now,
            now,
            CustomLoopInvocationOperationState.Pending,
            CustomLoopInvocationOutcome.Unknown,
            string.Empty,
            null,
            "The invocation is pending.");
        var store = new CustomLoopInvocationOperationStore(paths);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        Assert.Equal(CustomLoopInvocationOperationStoreStatus.Completed, (await store.CompleteAsync(pending with
        {
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = CustomLoopInvocationOutcome.Rejected,
            AdmissionStatus = "NotFound",
            Detail = "The loop definition does not exist."
        })).Status);
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
        Assert.Equal(CustomLoopControlOperationStoreStatus.Created, (await store.BeginAsync(pending)).Status);
        Assert.Equal(CustomLoopControlOperationStoreStatus.Completed, (await store.CompleteAsync(pending with
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, ".agent", "AGENT.md"), "runtime guide");
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
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
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);

        _ = await runtime.RunTurnAsync("hello");
        var historyAfterTurn = await runtime.RunTurnAsync("/history");
        var fresh = await runtime.RunTurnAsync("/new");
        var historyAfterNew = await runtime.RunTurnAsync("/history");

        Assert.Contains("before sending the first prompt", historyAfterTurn.Output, StringComparison.Ordinal);
        Assert.Equal("Started a new conversation.", fresh.Output);
        Assert.Contains("Stored conversations:", historyAfterNew.Output, StringComparison.Ordinal);
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
        return await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            null,
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
