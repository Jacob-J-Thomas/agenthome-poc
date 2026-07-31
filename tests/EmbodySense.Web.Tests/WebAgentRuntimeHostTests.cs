using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Web;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

public sealed class WebAgentRuntimeHostTests
{
    [Fact]
    public void Constructor_rejects_help_only_options_without_a_configured_model()
    {
        var options = WebRunOptions.FromArguments(["--help"]);

        var exception = Assert.Throws<ArgumentException>(() => new WebAgentRuntimeHost(options, new WebApprovalCoordinator()));

        Assert.Contains("nonblank configured model", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeWorkspaceAsync_initializes_workspace_with_web_audit_actor()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        var before = host.GetStatus();
        var after = await host.InitializeWorkspaceAsync();

        Assert.False(before.Initialized);
        Assert.True(after.Initialized);
        Assert.True(File.Exists(workspace.File(".agent", "permissions.json")));
        Assert.Contains("embodysense.web", await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson")));
    }

    [Fact]
    public async Task SendMessageAsync_requires_initialized_workspace()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            return host.SendMessageAsync("hello", (_, _) => Task.CompletedTask);
        });

        Assert.Contains("Workspace is not initialized", exception.Message);
    }

    [Fact]
    public async Task GetConfigurationAsync_returns_read_only_workspace_configuration()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath, "gpt-test");
        await host.InitializeWorkspaceAsync();

        var configuration = await host.GetConfigurationAsync();

        Assert.True(configuration.Status.Initialized);
        Assert.Equal("web", configuration.Runtime.Surface);
        Assert.Equal("gpt-test", configuration.Runtime.Model);
        Assert.NotNull(configuration.Runtime.CodexRuntime);
        Assert.Equal(CodexRuntimeCompatibility.Compatible, configuration.Runtime.CodexRuntime.Compatibility);
        Assert.Equal(Path.GetFullPath(codexPath), configuration.Runtime.CodexRuntime.ResolvedExecutablePath);
        Assert.Equal("codex-cli 999.0.0-test", configuration.Runtime.CodexRuntime.Version);
        Assert.Equal("gpt-test", configuration.Runtime.CodexRuntime.ConfiguredModel);
        Assert.True(configuration.Permissions.Parsed);
        Assert.Contains(configuration.Paths, path => path.Name == "Agent home" && path.Exists);
        Assert.Contains(configuration.Documents, document => document.Name == "Role guide" && document.Exists);
    }

    [Fact]
    public async Task Incompatible_model_is_visible_before_a_turn_is_accepted()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, advertiseConfiguredModels: false);
        await using var host = CreateHost(workspace.RootPath, codexPath, "gpt-test");
        await host.InitializeWorkspaceAsync();
        await WriteCurrentTranscriptAsync(workspace, "restored while unavailable", "durable answer");
        var transcript = Assert.IsAssignableFrom<IReadOnlyList<WebTranscriptMessage>>(await host.GetCurrentTranscriptAsync());
        var configuration = await host.GetConfigurationAsync();
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("hello", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        Assert.Equal(CodexRuntimeCompatibility.ModelUnavailable, configuration.Runtime.CodexRuntime!.Compatibility);
        Assert.Equal(["restored while unavailable", "durable answer"], transcript.Select(message => message.Content));
        var failure = Assert.Single(events);
        Assert.Equal("error", failure.Type);
        Assert.Contains(Path.GetFullPath(codexPath), failure.Error, StringComparison.Ordinal);
        Assert.Contains("gpt-test", failure.Error, StringComparison.Ordinal);
        Assert.Contains("Update Codex", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_handles_help_command_without_initialized_workspace()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("/help", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        var streamEvent = Assert.Single(events);
        Assert.Equal("assistant_final", streamEvent.Type);
        Assert.Contains("Runtime commands:", streamEvent.Text);
        Assert.Contains("/history, /conversations, /load", streamEvent.Text);
    }

    [Fact]
    public async Task SendMessageAsync_loads_history_selection_before_first_model_turn()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        await WriteCurrentTranscriptAsync(workspace, "web archived prompt", "web archived answer");
        await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).StartFreshConversationAsync();
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("/history", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });
        var historyEvent = Assert.Single(events);
        Assert.Equal("assistant_final", historyEvent.Type);
        Assert.Contains("Stored conversations:", historyEvent.Text);
        Assert.Contains("web archived prompt", historyEvent.Text);
        Assert.Contains("Send conversation number to load", historyEvent.Text);

        events.Clear();
        await host.SendMessageAsync("1", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        Assert.Collection(
            events,
            loadedEvent =>
            {
                Assert.Equal("history_loaded", loadedEvent.Type);
                Assert.Collection(
                    loadedEvent.Messages,
                    message =>
                    {
                        Assert.Equal("user", message.Role);
                        Assert.Equal("web archived prompt", message.Content);
                    },
                    message =>
                    {
                        Assert.Equal("assistant", message.Role);
                        Assert.Equal("web archived answer", message.Content);
                    });
            },
            confirmationEvent =>
            {
                Assert.Equal("assistant_final", confirmationEvent.Type);
                Assert.Contains("Loaded conversation `archive/", confirmationEvent.Text);
            });
        Assert.Contains("web archived prompt", await File.ReadAllTextAsync(CurrentTranscriptPath(workspace)));
    }

    [Fact]
    public async Task SendMessageAsync_emits_verbose_context_when_web_verbose_mode_is_enabled()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var events = new List<WebStreamEvent>();

        await host.SetVerboseModeAsync(true, (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });
        await host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        Assert.Collection(
            events,
            statusEvent =>
            {
                Assert.Equal("system", statusEvent.Type);
                Assert.Contains("Verbose mode enabled", statusEvent.Text);
            },
            contextEvent =>
            {
                Assert.Equal("verbose_context", contextEvent.Type);
                Assert.Contains("[verbose] Visible inference context follows.", contextEvent.Text);
                Assert.Contains("This is not private model reasoning", contextEvent.Text);
                Assert.Contains("loop_id: default-conversation", contextEvent.Text);
                Assert.Contains("source=current-turn-input", contextEvent.Text);
                Assert.Contains("compaction:", contextEvent.Text);
                Assert.Contains("workspace_commands_allowed_by_loop:", contextEvent.Text);
                Assert.Contains("hello from web", contextEvent.Text);
            },
            deltaEvent =>
            {
                Assert.Equal("assistant_delta", deltaEvent.Type);
                Assert.Contains("web response: hello from web", deltaEvent.Text);
            },
            finalEvent =>
            {
                Assert.Equal("assistant_final", finalEvent.Type);
                Assert.Contains("web response: hello from web", finalEvent.Text);
            });
    }

    [Fact]
    public async Task Transcript_hydration_remains_available_while_another_process_owns_custom_loop_hosting()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        await WriteCurrentTranscriptAsync(workspace, "hosted elsewhere", "durable answer");
        await using var competingGate = new CustomLoopWorkspaceExecutionGate(new WorkspacePaths(workspace.RootPath));
        using var activeExecution = competingGate.TryAcquire("competing-custom-loop", new string('a', CustomLoopLimits.Sha256HexCharacters)).Lease!;

        var transcript = Assert.IsAssignableFrom<IReadOnlyList<WebTranscriptMessage>>(await host.GetCurrentTranscriptAsync());

        Assert.Equal(["hosted elsewhere", "durable answer"], transcript.Select(message => message.Content));
    }

    [Fact]
    public async Task Transcript_hydration_on_a_fresh_initialized_workspace_returns_an_empty_canonical_transcript()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();

        var transcript = Assert.IsAssignableFrom<IReadOnlyList<WebTranscriptMessage>>(await host.GetCurrentTranscriptAsync());
        var snapshot = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationSnapshotAsync();

        Assert.Empty(transcript);
        Assert.Empty(snapshot.Messages);
        Assert.False(HasArchivedConversation(workspace));
    }

    [Fact]
    public async Task Restarted_web_host_restores_and_continues_the_same_logical_conversation()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        string conversationVersion;

        await using (var firstHost = CreateHost(workspace.RootPath, codexPath))
        {
            await firstHost.InitializeWorkspaceAsync();
            await firstHost.SendMessageAsync("web first turn", (_, _) => Task.CompletedTask);
            conversationVersion = (await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationSnapshotAsync()).Version;
        }

        await using var restartedHost = CreateHost(workspace.RootPath, codexPath);
        var restored = Assert.IsAssignableFrom<IReadOnlyList<WebTranscriptMessage>>(await restartedHost.GetCurrentTranscriptAsync());

        Assert.Collection(
            restored,
            message =>
            {
                Assert.Equal("User", message.Role);
                Assert.Equal("web first turn", message.Content);
            },
            message =>
            {
                Assert.Equal("Assistant", message.Role);
                Assert.Equal("web response: web first turn", message.Content);
            });

        await restartedHost.SendMessageAsync("web second turn", (_, _) => Task.CompletedTask);
        var continued = Assert.IsAssignableFrom<IReadOnlyList<WebTranscriptMessage>>(await restartedHost.GetCurrentTranscriptAsync());
        var current = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationSnapshotAsync();

        Assert.Equal(conversationVersion, current.Version);
        Assert.Equal(4, continued.Count);
        Assert.Equal(["web first turn", "web response: web first turn", "web second turn", "web response: web second turn"], continued.Select(message => message.Content));
        Assert.Equal(continued.Select(message => message.Content), current.Messages.Select(message => message.Content));
        Assert.False(HasArchivedConversation(workspace));
    }

    [Fact]
    public async Task Transcript_hydration_waits_for_the_active_turn_and_returns_its_complete_canonical_messages()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var deltaObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var send = host.SendMessageAsync("hydrate during turn", async (streamEvent, cancellationToken) =>
        {
            if (streamEvent.Type == "assistant_delta")
            {
                deltaObserved.TrySetResult();
                await releaseDelta.Task.WaitAsync(cancellationToken);
            }
        });
        await deltaObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var hydration = host.GetCurrentTranscriptAsync();
        await Task.Delay(100);
        Assert.False(hydration.IsCompleted);

        releaseDelta.TrySetResult();
        await send;
        var transcript = Assert.IsAssignableFrom<IReadOnlyList<WebTranscriptMessage>>(await hydration);

        Assert.Collection(
            transcript,
            message =>
            {
                Assert.Equal("User", message.Role);
                Assert.Equal("hydrate during turn", message.Content);
            },
            message =>
            {
                Assert.Equal("Assistant", message.Role);
                Assert.Equal("web response: hydrate during turn", message.Content);
            });
    }

    [Fact]
    public async Task Transcript_hydration_does_not_cross_even_a_runtime_independent_turn_boundary()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);
        await host.InitializeWorkspaceAsync();
        var responseObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var send = host.SendMessageAsync("/help", async (_, cancellationToken) =>
        {
            responseObserved.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
        });
        await responseObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var hydration = host.GetCurrentTranscriptAsync();
        await Task.Delay(100);
        Assert.False(hydration.IsCompleted);

        releaseResponse.TrySetResult();
        await send;
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<WebTranscriptMessage>>(await hydration));
    }

    [Fact]
    public async Task SendMessageAsync_surfaces_ambiguous_provider_failure_as_needs_review_event()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, "provider down");
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        var streamEvent = Assert.Single(events);
        Assert.Equal("needs_review", streamEvent.Type);
        Assert.Contains("Codex app-server turn failed: provider down", streamEvent.Text, StringComparison.Ordinal);
        Assert.Contains("Automatic redispatch is forbidden", streamEvent.Text, StringComparison.Ordinal);
        Assert.Null(streamEvent.Error);
    }

    [Fact]
    public async Task SendMessageAsync_surfaces_disabled_loop_as_error_event()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var definitionPath = workspace.File(".agent", "loops", "definitions", "default-conversation.json");
        var definitionJson = await File.ReadAllTextAsync(definitionPath);
        await File.WriteAllTextAsync(definitionPath, definitionJson.Replace("\"state\": \"enabled\"", "\"state\": \"disabled\"", StringComparison.Ordinal));
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        var streamEvent = Assert.Single(events);
        Assert.Equal("error", streamEvent.Type);
        Assert.Equal("Loop `default-conversation` is not enabled.", streamEvent.Error);
    }

    [Fact]
    public async Task SendMessageAsync_emits_cancelled_event_after_active_turn_is_cancelled()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: 5000);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var events = new List<WebStreamEvent>();

        var sendTask = host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });
        var cancelled = false;
        for (var attempt = 0; attempt < 200 && !cancelled; attempt++)
        {
            cancelled = host.CancelCurrentTurn();
            await Task.Delay(10);
        }

        Assert.True(cancelled);
        await sendTask;

        var streamEvent = Assert.Single(events);
        Assert.Equal("cancelled", streamEvent.Type);
        Assert.Equal("Message cancelled.", streamEvent.Text);

        events.Clear();
        await host.SendMessageAsync("after cancel", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        Assert.Collection(
            events,
            deltaEvent =>
            {
                Assert.Equal("assistant_delta", deltaEvent.Type);
                Assert.Equal("web response: after cancel", deltaEvent.Text);
            },
            finalEvent =>
            {
                Assert.Equal("assistant_final", finalEvent.Type);
                Assert.Equal("web response: after cancel", finalEvent.Text);
            });
    }

    [Fact]
    public async Task Cancelling_chat_defers_runtime_disposal_until_an_active_custom_loop_remains_controllable()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: 30_000);
        var approvals = new WebApprovalCoordinator();
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(options, approvals);
        await host.InitializeWorkspaceAsync();
        var definition = await CreateInvocationLoopAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-during-chat-cancel", "host-dispose-custom-loop");

        var invocation = host.InvokeLoopAsync(input, "connection-1");
        await WaitForMarkerAsync(workspace.File("host-dispose-custom-loop.marker"));
        var running = await WaitForRunAsync(host, input.OperationId);
        var send = host.SendMessageAsync("hello from web", (_, _) => Task.CompletedTask);
        var chatCancelled = false;
        for (var attempt = 0; attempt < 200 && !chatCancelled; attempt++)
        {
            chatCancelled = host.CancelCurrentTurn();
            await Task.Delay(10);
        }

        Assert.True(chatCancelled);
        await send;
        running = Assert.IsType<LoopRunSnapshot>(await host.GetLoopRunAsync(running.Id));
        var cancellation = await host.CancelLoopAsync(new LoopRunControlInput(running.Id, running.LifecycleVersion, "cancel-after-chat-cancel"));
        var completed = await invocation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(cancellation.Status, new[] { "CancelRequested", "Cancelled", "AuditWarning" });
        Assert.NotNull(cancellation.Run);
        Assert.Contains(completed.ExecutionStatus, new[] { "Cancelled", "NeedsReview", "Failed" });
    }

    [Fact]
    public async Task CancelCurrentTurn_returns_false_when_no_turn_is_running()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        Assert.False(host.CancelCurrentTurn());
    }

    [Fact]
    public async Task SendMessageAsync_validates_message()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            return host.SendMessageAsync(" ", (_, _) => Task.CompletedTask);
        });
    }

    [Fact]
    public async Task SendMessageAsync_validates_event_writer()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            return host.SendMessageAsync("hello", null!);
        });
    }

    [Fact]
    public async Task ResumeLoopAsync_requires_an_initialized_workspace()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test"]);
        await using var host = new WebAgentRuntimeHost(options, approvals);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResumeLoopAsync(new LoopRunControlInput("run-one", 1, "resume-uninitialized"), "connection-1"));

        Assert.Contains("Workspace is not initialized", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_retries_recovery_after_retained_runtime_startup_schema_failure()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var paths = new WorkspacePaths(workspace.RootPath);
        var runStore = new CustomLoopRunStore(paths);
        var running = RunningRun("run-web-unsupported-startup-recovery");
        await PersistRunningRunAsync(runStore, running);
        var indexPath = Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json");
        await File.WriteAllTextAsync(indexPath, "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}");

        await host.SetVerboseModeAsync(true, (_, _) => Task.CompletedTask);
        File.Delete(indexPath);
        var recovered = await host.GetLoopRunAsync(running.Id);

        Assert.Equal("Paused", recovered?.Status);
    }

    [Fact]
    public async Task DisposeAsync_cancels_an_active_custom_loop_through_the_host_lifetime()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: 30_000);
        var approvals = new WebApprovalCoordinator();
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        var host = new WebAgentRuntimeHost(options, approvals);
        await host.InitializeWorkspaceAsync();
        var definition = await CreateInvocationLoopAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-host-dispose", "host-dispose-custom-loop");

        var invocation = host.InvokeLoopAsync(input, "connection-1");
        await WaitForMarkerAsync(workspace.File("host-dispose-custom-loop.marker"));
        var dispose = host.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        var invocationException = await Record.ExceptionAsync(async () => await invocation);

        Assert.True(invocationException is null or OperationCanceledException, invocationException?.ToString());
        if (invocationException is null)
        {
            var response = await invocation;
            Assert.Contains(response.ExecutionStatus, new[] { "Cancelled", "NeedsReview", "Failed" });
        }
    }

    [Fact]
    public async Task Owner_disconnect_returns_a_zero_execution_tool_rejection_and_the_custom_run_continues()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: -1);
        var approvalPublication = new ApprovalPublicationSignal();
        var approvals = new WebApprovalCoordinator(approvalPublication);
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(options, approvals);
        await host.InitializeWorkspaceAsync();
        await File.WriteAllTextAsync(workspace.File("approval-only-note.txt"), "content-that-must-not-be-returned");
        var definition = await CreateInvocationLoopAsync(workspace, [LoopToolAssignment.Read]);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-owner-disconnect-tool", "request-the-governed-read");

        var invocation = host.InvokeLoopAsync(input, "connection-1");
        Assert.Equal("connection-1", await approvalPublication.WaitForNonemptyApprovalAsync());
        Assert.Single(approvals.GetPending("connection-1"));
        await approvals.DisconnectOwnerAsync("connection-1");
        var response = await invocation;
        var toolResponse = await File.ReadAllTextAsync(workspace.File("owner-disconnected-tool-response.json"));
        var audit = await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson"));

        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.Contains("continued after governed tool denial", response.Run!.FinalOutput, StringComparison.Ordinal);
        Assert.Contains("owner_disconnected", toolResponse, StringComparison.Ordinal);
        Assert.Contains("\"success\":false", toolResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("content-that-must-not-be-returned", toolResponse, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"tool.approval.decision\"", audit, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"approval_rejected\"", audit, StringComparison.Ordinal);
        var toolExecution = Assert.Single(audit.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), line => line.Contains("\"action\":\"tool.execute\"", StringComparison.Ordinal));
        Assert.Contains("\"outcome\":\"approval_rejected\"", toolExecution, StringComparison.Ordinal);
        Assert.Contains("\"approved_by_human\":false", toolExecution, StringComparison.Ordinal);
    }

    private static WebAgentRuntimeHost CreateHost(string rootPath, string? codexPath = null, string model = "gpt-test")
    {
        var arguments = new List<string> { "--workdir", rootPath, "--model", model };
        if (codexPath is not null)
        {
            arguments.AddRange(["--codex-path", codexPath]);
        }

        var options = WebRunOptions.FromArguments(arguments.ToArray());
        return new WebAgentRuntimeHost(options, new WebApprovalCoordinator());
    }

    private static CustomLoopRunRecord RunningRun(string runId)
    {
        var now = DateTimeOffset.Parse("2026-07-26T12:00:00+00:00");
        var definition = CustomLoopDefinitionContentHash.Apply(CustomLoopDefinition.CreateSeed("loop-web-recovery", "role-workspace", "step-only", "create-loop-web-recovery", now) with { ContentHash = string.Empty });
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
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            $"admit-{runId}",
            WorkspaceActors.Web,
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
            null);
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

    private static async Task WriteCurrentTranscriptAsync(TestWorkspace workspace, string prompt, string answer)
    {
        var path = CurrentTranscriptPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""
            {"schemaVersion":1,"conversationId":"current","sequence":1,"timestampUtc":"2026-06-01T00:01:00+00:00","messageId":"message-1","publicationId":"publication-1","role":"user","content":"{{prompt}}"}
            {"schemaVersion":1,"conversationId":"current","sequence":2,"timestampUtc":"2026-06-01T00:02:00+00:00","messageId":"message-2","publicationId":"publication-2","role":"assistant","content":"{{answer}}"}
            """);
    }

    private static async Task<LoopDefinitionSnapshot> CreateInvocationLoopAsync(TestWorkspace workspace, IReadOnlyList<LoopToolAssignment>? toolAssignments = null)
    {
        var facade = new LoopAuthoringFacade(workspace.RootPath);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-host-dispose-loop")).Definition);
        var input = new LoopDefinitionInput(
            "Host disposal loop",
            "Verifies host-lifetime cancellation.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [new LoopInferenceStep(created.InferenceSteps.Single().Id, "Wait", "Wait for the admitted prompt.", new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null))],
            toolAssignments ?? [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, "update-host-dispose-loop", input);
        return Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
    }

    private static async Task WaitForMarkerAsync(string markerPath)
    {
        if (File.Exists(markerPath))
        {
            return;
        }

        var markerCreated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(markerPath)!, Path.GetFileName(markerPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        watcher.Created += (_, _) => markerCreated.TrySetResult(true);
        watcher.Changed += (_, _) => markerCreated.TrySetResult(true);
        watcher.Error += (_, args) => markerCreated.TrySetException(args.GetException());
        watcher.EnableRaisingEvents = true;

        if (File.Exists(markerPath))
        {
            return;
        }

        await markerCreated.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(markerPath), "The custom-loop provider attempt signal arrived without a durable marker.");
    }

    private static async Task<LoopRunSnapshot> WaitForRunAsync(WebAgentRuntimeHost host, string admissionOperationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var summary = (await host.GetLoopRunsAsync()).Items.SingleOrDefault(run => string.Equals(run.AdmissionOperationId, admissionOperationId, StringComparison.Ordinal));
            if (summary is not null && await host.GetLoopRunAsync(summary.Id) is { } run)
            {
                return run;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Custom run for admission operation `{admissionOperationId}` was not persisted.");
    }

    private static string CurrentTranscriptPath(TestWorkspace workspace)
    {
        return workspace.File(".agent", "memory", "conversations", "current.ndjson");
    }

    private static bool HasArchivedConversation(TestWorkspace workspace)
    {
        var archivePath = workspace.File(".agent", "memory", "conversations", "archive");
        return Directory.Exists(archivePath) && Directory.EnumerateFiles(archivePath, "*.ndjson").Any();
    }

    private static async Task<string> CreateFakeCodexExecutableAsync(
        TestWorkspace workspace,
        string? turnFailureMessage = null,
        int turnDelayMilliseconds = 0,
        bool advertiseConfiguredModels = true)
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
            $turnFailureMessage = {{FormatPowerShellStringLiteral(turnFailureMessage)}}
            $turnDelayMilliseconds = {{turnDelayMilliseconds}}
            $advertisedModels = if ({{(advertiseConfiguredModels ? "$true" : "$false")}}) { @("test-model", "gpt-test") } else { @("older-model") }
            $turnNumber = 0

            function Write-ProtocolJson($value) {
                $value | ConvertTo-Json -Compress -Depth 20
                [Console]::Out.Flush()
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                $message = $line | ConvertFrom-Json

                if ($message.id -eq 99) {
                    $toolResponse = $message | ConvertTo-Json -Compress -Depth 20
                    [IO.File]::WriteAllText((Join-Path $PSScriptRoot "owner-disconnected-tool-response.json"), $toolResponse)
                    $text = "continued after governed tool denial"
                    Write-ProtocolJson @{ method = "item/agentMessage/delta"; params = @{ threadId = $threadId; turnId = "turn-test"; delta = $text } }
                    Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = "turn-test"; turn = @{ id = "turn-test"; status = "completed"; items = @(@{ type = "agentMessage"; phase = "final_answer"; text = $text }) } } }
                    continue
                }

                switch ($message.method) {
                    "initialize" {
                        Write-ProtocolJson @{ id = $message.id; result = @{} }
                    }

                    "initialized" {
                    }

                    "model/list" {
                        $models = @($advertisedModels | ForEach-Object { @{ id = $_; model = $_ } })
                        Write-ProtocolJson @{ id = $message.id; result = @{ data = $models } }
                    }

                    "thread/start" {
                        Write-ProtocolJson @{ id = $message.id; result = @{ thread = @{ id = $threadId } } }
                    }

                    "turn/start" {
                        $turnNumber++
                        $turnId = "turn-test"
                        $userText = [string]$message.params.input[0].text
                        $currentUserMarker = "Current user message:"
                        $currentUserIndex = $userText.IndexOf($currentUserMarker)
                        if ($currentUserIndex -ge 0) {
                            $userText = $userText.Substring($currentUserIndex + $currentUserMarker.Length).Trim()
                        }

                        $text = "web response: $userText"
                        Write-ProtocolJson @{ id = $message.id; result = @{ turn = @{ id = $turnId } } }
                        if ($turnDelayMilliseconds -eq -1) {
                            Write-ProtocolJson @{ id = 99; method = "item/tool/call"; params = @{ threadId = $threadId; turnId = $turnId; callId = "call-owner-disconnect"; namespace = "embodysense"; tool = "command"; arguments = @{ command = "read"; path = "approval-only-note.txt" } } }
                            break
                        }
                        if ($turnDelayMilliseconds -ge 30000) {
                            [IO.File]::WriteAllText((Join-Path $PSScriptRoot "host-dispose-custom-loop.marker"), "started")
                            Start-Sleep -Milliseconds $turnDelayMilliseconds
                        }
                        elseif ($turnDelayMilliseconds -gt 0 -and $userText.Contains("hello from web")) {
                            Start-Sleep -Milliseconds $turnDelayMilliseconds
                        }

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
}
