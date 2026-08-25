using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
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
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using System.Text.Json;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
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
    public void Constructor_rejects_pre_resolved_status_outside_the_exact_runtime_request()
    {
        using var workspace = new TestWorkspace();
        var executablePath = workspace.File("fake-codex.cmd");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", executablePath]);
        var status = CreateCompatibleRuntimeStatus(executablePath, "gpt-test");

        Assert.Throws<ArgumentNullException>(() => new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), (CodexRuntimeStatus)null!));
        Assert.Throws<ArgumentException>(() => new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), status with { Compatibility = CodexRuntimeCompatibility.ProbeFailed }));
        Assert.Throws<ArgumentException>(() => new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), status with { ResolvedExecutablePath = null }));
        Assert.Throws<ArgumentException>(() => new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), status with { ConfiguredModel = "different-model" }));
        Assert.Throws<ArgumentException>(() => new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), status with { RequestedExecutablePath = workspace.File("different-codex.cmd") }));
    }

    [Fact]
    public async Task GetConfigurationAsync_reuses_exact_pre_resolved_status_without_probing_the_executable()
    {
        using var workspace = new TestWorkspace();
        var nonexistentExecutable = workspace.File("must-not-be-probed.cmd");
        await using var host = CreateHost(workspace.RootPath, nonexistentExecutable);

        var configuration = await host.GetConfigurationAsync();

        Assert.Equal(CodexRuntimeCompatibility.Compatible, configuration.Runtime.CodexRuntime!.Compatibility);
        Assert.Equal(nonexistentExecutable, configuration.Runtime.CodexRuntime.RequestedExecutablePath);
        Assert.Equal(Path.GetFullPath(nonexistentExecutable), configuration.Runtime.CodexRuntime.ResolvedExecutablePath);
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
    public async Task InvokeGovernedLoopAsync_validates_input_and_owner_then_reaches_the_runtime_boundary()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => host.InvokeGovernedLoopAsync(null!, "connection-1"));

        var transport = new GovernedLoopRunInvocationTransportInput(
            "governed-host-boundary",
            new GovernedLoopRevisionPublicationInput(1, 1, "missing-graph", "missing-revision", new string('a', 64), "publish-one", new string('b', 64)),
            new GovernedLoopAuthorityGrantInput("grant-one", 1, "sha256:" + new string('c', 64)),
            "prompt");
        Assert.True(GovernedLoopRunInvocationTransport.TryCreate(transport, out var input));
        Assert.NotNull(input);

        await Assert.ThrowsAsync<ArgumentException>(() => host.InvokeGovernedLoopAsync(input!, " "));
        var response = await host.InvokeGovernedLoopAsync(input!, "connection-1");

        Assert.Equal("NotFound", response.Status);
    }

    [Fact]
    public async Task ConfirmAndInvokeGovernedLoopAsync_preserves_preparation_unavailability_without_retiring_the_browser_operation()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var modelProfile = VisibleInvocationTestModelProfile.Create();
        await using var host = CreateVisibleInvocationHost(workspace, codexPath, modelProfile);
        await host.InitializeWorkspaceAsync();
        GovernedLoopGraphCandidate candidate;
        GovernedLoopInvocationPreparationResponse preparation;
        await using (var runtime = await CreateWebRuntimeAsync(workspace, codexPath, modelProfile))
        {
            candidate = await CreatePublishedVisibleInvocationGraphAsync(runtime);
            preparation = await runtime.PrepareGovernedLoopInvocationAsync(
                new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        }
        var preview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(preparation.Preview);
        await File.WriteAllTextAsync(new WorkspacePaths(workspace.RootPath).AuthorityProfilesDocumentPath, "{");

        var response = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                preview.SemanticHash,
                null,
                "governed-invoke-confirmation-unavailable",
                "prompt"),
            "connection-1");

        Assert.Equal("Unavailable", response.Status);
        Assert.Null(response.AdmissionStatus);
        Assert.Null(response.AdmissionFailureCode);
        Assert.Null(response.Run);
    }

    [Fact]
    public async Task ConfirmAndInvokeGovernedLoopAsync_replays_a_preview_shaped_operation_after_confirmation_makes_preparation_ready()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var modelProfile = VisibleInvocationTestModelProfile.Create();
        await using var host = CreateVisibleInvocationHost(workspace, codexPath, modelProfile);
        await host.InitializeWorkspaceAsync();
        GovernedLoopGraphCandidate candidate;
        GovernedLoopInvocationPreparationResponse preparation;
        await using (var runtime = await CreateWebRuntimeAsync(workspace, codexPath, modelProfile))
        {
            candidate = await CreatePublishedVisibleInvocationGraphAsync(runtime);
            preparation = await runtime.PrepareGovernedLoopInvocationAsync(
                new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        }
        var preview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(preparation.Preview);
        var request = new GovernedLoopVisibleInvocationRequest(
            candidate.GraphId!,
            candidate.RevisionId!,
            preview.SemanticHash,
            null,
            "governed-invoke-confirmation-replay",
            "prompt");

        var first = await host.ConfirmAndInvokeGovernedLoopAsync(request, "connection-1");
        GovernedLoopInvocationPreparationResponse ready;
        await using (var runtime = await CreateWebRuntimeAsync(workspace, codexPath, modelProfile))
        {
            ready = await runtime.PrepareGovernedLoopInvocationAsync(
                new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        }
        var replay = await host.ConfirmAndInvokeGovernedLoopAsync(request, "connection-1");

        Assert.NotEqual("Rejected", first.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Ready, ready.Status);
        Assert.Null(ready.Preview);
        Assert.NotEmpty(ready.EligibleGrants);
        Assert.NotEqual("Rejected", replay.Status);
        Assert.NotEqual("GrantChoiceRequired", replay.AdmissionFailureCode);
        Assert.Null(replay.AdmissionFailureCode);
    }

    [Fact]
    public async Task ConfirmAndInvokeGovernedLoopAsync_rejects_browser_grant_selection_and_missing_preview_while_confirmation_is_required()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var modelProfile = VisibleInvocationTestModelProfile.Create();
        await using var host = CreateVisibleInvocationHost(workspace, codexPath, modelProfile);
        await host.InitializeWorkspaceAsync();
        GovernedLoopGraphCandidate candidate;
        GovernedLoopInvocationPreparationResponse preparation;
        await using (var runtime = await CreateWebRuntimeAsync(workspace, codexPath, modelProfile))
        {
            candidate = await CreatePublishedVisibleInvocationGraphAsync(runtime);
            preparation = await runtime.PrepareGovernedLoopInvocationAsync(
                new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        }
        var preview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(preparation.Preview);

        var selectedGrant = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                preview.SemanticHash,
                new GovernedLoopVisibleInvocationGrantSelection("browser-grant", 1, new string('a', 64)),
                "governed-invoke-confirmation-selected-grant",
                "prompt"),
            "connection-1");
        var missingPreview = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                null,
                null,
                "governed-invoke-confirmation-missing-preview",
                "prompt"),
            "connection-1");
        var stalePreview = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                new string('a', 64),
                null,
                "governed-invoke-confirmation-stale-preview",
                "prompt"),
            "connection-1");

        Assert.Equal("Rejected", selectedGrant.Status);
        Assert.Equal("Invalid", selectedGrant.AdmissionFailureCode);
        Assert.Equal("Rejected", missingPreview.Status);
        Assert.Equal("ConfirmationRequired", missingPreview.AdmissionFailureCode);
        Assert.Equal("Rejected", stalePreview.Status);
        Assert.Equal("Stale", stalePreview.AdmissionFailureCode);
    }

    [Fact]
    public async Task ConfirmAndInvokeGovernedLoopAsync_rejects_mixed_missing_and_forged_grant_choices_after_preparation_is_ready()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var modelProfile = VisibleInvocationTestModelProfile.Create();
        await using var host = CreateVisibleInvocationHost(workspace, codexPath, modelProfile);
        await host.InitializeWorkspaceAsync();
        GovernedLoopGraphCandidate candidate;
        GovernedLoopInvocationPreparationResponse preparation;
        await using (var runtime = await CreateWebRuntimeAsync(workspace, codexPath, modelProfile))
        {
            candidate = await CreatePublishedVisibleInvocationGraphAsync(runtime);
            preparation = await runtime.PrepareGovernedLoopInvocationAsync(
                new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        }
        var preview = Assert.IsType<GovernedLoopInvocationAuthorityPreview>(preparation.Preview);
        _ = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                preview.SemanticHash,
                null,
                "governed-invoke-confirmation-create-grant",
                "prompt"),
            "connection-1");

        var mixedPreviewAndGrant = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                preview.SemanticHash,
                new GovernedLoopVisibleInvocationGrantSelection("browser-grant", 1, new string('a', 64)),
                "governed-invoke-ready-mixed-preview",
                "prompt"),
            "connection-1");
        var missingGrant = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                null,
                null,
                "governed-invoke-ready-missing-grant",
                "prompt"),
            "connection-1");
        var forgedGrant = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                null,
                new GovernedLoopVisibleInvocationGrantSelection("forged-grant", 1, new string('b', 64)),
                "governed-invoke-ready-forged-grant",
                "prompt"),
            "connection-1");
        GovernedLoopInvocationPreparationResponse ready;
        await using (var runtime = await CreateWebRuntimeAsync(workspace, codexPath, modelProfile))
        {
            ready = await runtime.PrepareGovernedLoopInvocationAsync(
                new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        }
        var exactGrant = Assert.Single(ready.EligibleGrants).Grant;
        var selectedGrant = await host.ConfirmAndInvokeGovernedLoopAsync(
            new GovernedLoopVisibleInvocationRequest(
                candidate.GraphId!,
                candidate.RevisionId!,
                null,
                new GovernedLoopVisibleInvocationGrantSelection(exactGrant.GrantId.Value, exactGrant.Revision.Value, exactGrant.ContentHash),
                "governed-invoke-ready-exact-grant",
                "prompt"),
            "connection-1");

        Assert.Equal("Invalid", mixedPreviewAndGrant.AdmissionFailureCode);
        Assert.Equal("GrantChoiceRequired", missingGrant.AdmissionFailureCode);
        Assert.Equal("Stale", forgedGrant.AdmissionFailureCode);
        Assert.NotEqual("Rejected", selectedGrant.Status);
    }

    [Fact]
    public async Task InitializeWorkspaceAsync_serializes_concurrent_clients_and_reports_the_already_initialized_race()
    {
        using var workspace = new TestWorkspace();
        var initializer = new SerializedTestWorkspaceInitializer();
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test"]);
        await using var host = new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), initializer);

        var first = host.InitializeWorkspaceAsync();
        await initializer.WaitUntilEnteredAsync();
        var second = host.InitializeWorkspaceAsync();
        await Task.Delay(50);

        Assert.Equal(1, initializer.CallCount);
        initializer.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, initializer.CallCount);
        Assert.All(results, result => Assert.True(result.Initialized));
        Assert.Equal("initialized", results[0].InitializationOutcome);
        Assert.Equal("already-initialized", results[1].InitializationOutcome);
        Assert.All(results, result => Assert.Equal("initialized", result.InitializationState));
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
        await using var host = CreateResolvingHost(workspace.RootPath, codexPath, "gpt-test");
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
        await using var host = CreateResolvingHost(workspace.RootPath, codexPath, "gpt-test");
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
    public async Task SendMessageAsync_surfaces_a_conclusive_terminal_provider_failure_as_an_error_event()
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
        Assert.Equal("error", streamEvent.Type);
        Assert.Contains("Codex app-server turn failed: provider down", streamEvent.Error, StringComparison.Ordinal);
        Assert.Null(streamEvent.Text);
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
    public async Task SendMessageAsync_parks_active_provider_cancellation_for_review_before_starting_a_new_session()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateTrackedFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        await host.SendMessageAsync("warm provider session", (_, _) => Task.CompletedTask);
        var instancePath = workspace.File("web-app-server-instances.txt");
        var providerTurnsPath = workspace.File("web-provider-turns.txt");
        var initialInstance = Assert.Single(await File.ReadAllLinesAsync(instancePath));
        Assert.Equal([$"{initialInstance}:warm provider session"], await File.ReadAllLinesAsync(providerTurnsPath));
        var events = new List<WebStreamEvent>();

        var sendTask = host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });
        var markerPath = workspace.File("web-turn-cancellation.marker");
        await WaitForMarkerAsync(markerPath);
        Assert.Equal(initialInstance, await File.ReadAllTextAsync(markerPath));
        Assert.True(host.CancelCurrentTurn());
        var cancelled = await sendTask.WaitAsync(TimeSpan.FromSeconds(10));

        var streamEvent = Assert.Single(events);
        Assert.Equal(AgentRuntimeTurnStatus.MessageNeedsReview, cancelled.Status);
        Assert.Equal("needs_review", streamEvent.Type);
        Assert.Contains("irreversible turn/start transport-write boundary", streamEvent.Text, StringComparison.Ordinal);
        var turns = new DefaultConversationTurnStore(new WorkspacePaths(workspace.RootPath));
        var review = Assert.Single(await turns.ListNeedsReviewAsync());
        Assert.Equal(DefaultConversationTurnReviewCause.OutcomeUnknown, review.ReviewCause);
        Assert.Equal(DefaultConversationProviderOutcome.OutcomeUnknown, review.ProviderOutcome);
        Assert.Equal(
            [$"{initialInstance}:warm provider session", $"{initialInstance}:hello from web"],
            await File.ReadAllLinesAsync(providerTurnsPath));

        events.Clear();
        var resolution = await host.SendMessageAsync($"/review resolve {review.TurnId}", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        Assert.Equal(AgentRuntimeTurnStatus.CommandHandled, resolution.Status);
        var resolutionEvent = Assert.Single(events);
        Assert.Equal("assistant_final", resolutionEvent.Type);
        Assert.Contains("explicitly abandoning its outcome-unknown provider attempt", resolutionEvent.Text, StringComparison.Ordinal);
        Assert.Empty(await turns.ListNeedsReviewAsync());
        Assert.Single(await File.ReadAllLinesAsync(instancePath));
        Assert.Equal(
            [$"{initialInstance}:warm provider session", $"{initialInstance}:hello from web"],
            await File.ReadAllLinesAsync(providerTurnsPath));

        events.Clear();
        var afterReview = await host.SendMessageAsync("after cancel", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, afterReview.Status);
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

        var instances = await File.ReadAllLinesAsync(instancePath);
        Assert.Equal(2, instances.Length);
        Assert.Equal(initialInstance, instances[0]);
        Assert.NotEqual(initialInstance, instances[1]);
        Assert.Equal(
            [
                $"{initialInstance}:warm provider session",
                $"{initialInstance}:hello from web",
                $"{instances[1]}:after cancel"
            ],
            await File.ReadAllLinesAsync(providerTurnsPath));
    }

    [Fact]
    public async Task Cancelling_chat_defers_runtime_disposal_until_an_active_custom_loop_remains_controllable()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: 30_000);
        var approvals = new WebApprovalCoordinator();
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        await using var host = CreateHost(options, approvals);
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
        var releasePath = workspace.File("host-dispose-custom-loop.release");
        CustomLoopRunRecord terminalRun;
        LoopRunInvocationResponse completed;
        try
        {
            terminalRun = await WaitForTerminalRunAsync(new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath)), running.Id);
            await File.WriteAllTextAsync(releasePath, "released");
            completed = await invocation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await File.WriteAllTextAsync(releasePath, "released");
        }

        Assert.Contains(cancellation.Status, new[] { "CancelRequested", "Cancelled", "AuditWarning" });
        Assert.NotNull(cancellation.Run);
        Assert.Contains(terminalRun.Status, new[] { CustomLoopRunStatus.Cancelled, CustomLoopRunStatus.NeedsReview, CustomLoopRunStatus.Failed });
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
        var host = CreateHost(options, approvals);
        await host.InitializeWorkspaceAsync();
        var definition = await CreateInvocationLoopAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-host-dispose", "host-dispose-custom-loop");

        var invocation = host.InvokeLoopAsync(input, "connection-1");
        await WaitForMarkerAsync(workspace.File("host-dispose-custom-loop.marker"));
        var dispose = host.DisposeAsync().AsTask();
        try
        {
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await File.WriteAllTextAsync(workspace.File("host-dispose-custom-loop.release"), "released");
        }
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
        await using var host = CreateHost(options, approvals);
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

    private static async Task<GovernedLoopGraphCandidate> CreatePublishedVisibleInvocationGraphAsync(AgentRuntime runtime)
    {
        var catalog = await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync();
        var role = Assert.Single(catalog.Roles.Roles, item => item.IsAdmissionReady);
        var candidate = VisibleInvocationGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.RoleId, role.Revision),
            role.ContentHash));
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-visible-invocation-host-test",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var draft = Assert.IsType<GovernedLoopGraphReadResponse>(created.Current);
        var lifecycle = Assert.IsType<GovernedLoopRevisionLifecycleHead>(draft.Lifecycle);
        var published = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "publish-visible-invocation-host-test",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            lifecycle.Status,
            lifecycle.LifecycleVersion,
            lifecycle.DraftRevision,
            lifecycle.PublishedRevision,
            null));

        Assert.Equal("committed", created.Status);
        Assert.Equal("committed", published.Status);
        return candidate;
    }

    private static GovernedLoopGraphCandidate VisibleInvocationGraphCandidate(ContextualRoleRevisionPin role)
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
            "visible-invocation-host-test",
            "revision-1",
            "Prove the visible host retries one preview-shaped browser invocation safely.",
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
                "Visible invocation host test",
                "Exact browser-confirmed invocation fixture.",
                [
                    new GovernedLoopNodeDisplayMetadata(trigger.Id, "Trigger", "Start.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata(exit.Id, "Exit", "Publish.", 200, 0),
                ]),
            DefaultRoutingPolicy());
    }

    private static GovernedModelRoutingPolicy DefaultRoutingPolicy()
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/model-profile/codex", out var profileId, out _));
        Assert.True(CapabilityDataClass.TryParse("public", out var publicDataClass, out _));
        var unbounded = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Unbounded);
        var privacy = GovernedModelPrivacyRequirement.Create(
            1,
            localOnly: true,
            CapabilityEgressMode.None,
            [],
            [publicDataClass!],
            ["local"],
            GovernedModelRetentionPosture.None,
            GovernedModelTrainingPosture.Prohibited);
        var requirements = GovernedModelProfileRequirements.Create(
            1,
            [GovernedModelModality.Text],
            [],
            1,
            1,
            privacy,
            GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded));
        return GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(profileId!), [], requirements);
    }

    private static WebAgentRuntimeHost CreateVisibleInvocationHost(
        TestWorkspace workspace,
        string codexPath,
        VisibleInvocationTestModelProfile modelProfile)
    {
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        var approvals = new WebApprovalCoordinator();
        return new WebAgentRuntimeHost(
            options,
            approvals,
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            null,
            runtimeStatus => AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                approvals,
                workspace.ServerStatePath,
                runtimeStatus,
                additionalModelProfileProviders: [modelProfile.Provider]));
    }

    private static async Task<AgentRuntime> CreateWebRuntimeAsync(
        TestWorkspace workspace,
        string codexPath,
        VisibleInvocationTestModelProfile modelProfile)
    {
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            new WebApprovalCoordinator(),
            workspace.ServerStatePath,
            CreateCompatibleRuntimeStatus(codexPath, "gpt-test"),
            additionalModelProfileProviders: [modelProfile.Provider]);
        return await factory.CreateAsync(
            "gpt-test",
            workspace.RootPath,
            codexPath,
            "read-only",
            AgentRuntimeSurface.Web);
    }

    private static WebAgentRuntimeHost CreateHost(string rootPath, string? codexPath = null, string model = "gpt-test")
    {
        var arguments = new List<string> { "--workdir", rootPath, "--model", model };
        if (codexPath is not null)
        {
            arguments.AddRange(["--codex-path", codexPath]);
        }

        var options = WebRunOptions.FromArguments(arguments.ToArray());
        return codexPath is null
            ? new WebAgentRuntimeHost(options, new WebApprovalCoordinator())
            : new WebAgentRuntimeHost(options, new WebApprovalCoordinator(), CreateCompatibleRuntimeStatus(codexPath, model));
    }

    private static WebAgentRuntimeHost CreateHost(WebRunOptions options, WebApprovalCoordinator approvalCoordinator)
    {
        var executablePath = options.CodexExecutablePath ?? throw new ArgumentException("The runtime behavior helper requires an explicit fake executable.", nameof(options));
        return new WebAgentRuntimeHost(options, approvalCoordinator, CreateCompatibleRuntimeStatus(executablePath, options.Model!));
    }

    private static WebAgentRuntimeHost CreateResolvingHost(string rootPath, string codexPath, string model)
    {
        var options = WebRunOptions.FromArguments(["--workdir", rootPath, "--model", model, "--codex-path", codexPath]);
        return new WebAgentRuntimeHost(options, new WebApprovalCoordinator());
    }

    private static CodexRuntimeStatus CreateCompatibleRuntimeStatus(string executablePath, string model)
    {
        return new CodexRuntimeStatus(
            CodexRuntimeCompatibility.Compatible,
            executablePath,
            Path.GetFullPath(executablePath),
            "codex-cli 999.0.0-test",
            model,
            "controlled test",
            "The isolated fake provider is pre-admitted for this Web runtime behavior test.");
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

    private static async Task<CustomLoopRunRecord> WaitForTerminalRunAsync(CustomLoopRunStore store, string runId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var run = await store.GetAsync(runId);
            if (run?.Status is CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview or CustomLoopRunStatus.Failed)
            {
                return run;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Custom run `{runId}` did not reach a terminal cancellation state.");
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
        const string RelativeDirectory = "fake-codex-conversation";
        var directory = workspace.File(RelativeDirectory);
        Directory.CreateDirectory(directory);
        var configurationPath = Path.Combine(directory, "conversation-config.json");
        var configuration = new
        {
            version = "codex-cli 999.0.0-test",
            advertisedModels = advertiseConfiguredModels ? new[] { "test-model", "gpt-test" } : new[] { "older-model" },
            responsePrefix = "web response: ",
            turnFailureMessage,
            waitForTurnRelease = turnDelayMilliseconds >= 30_000,
            requestGovernedTool = turnDelayMilliseconds == -1,
            turnReadyMarkerPath = workspace.File("host-dispose-custom-loop.marker"),
            turnReleaseMarkerPath = workspace.File("host-dispose-custom-loop.release"),
            toolResponsePath = workspace.File("owner-disconnected-tool-response.json")
        };
        await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(configuration, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return await CancellationHostExecutable.CreateAsync(workspace, RelativeDirectory, "codex-conversation-probe", "conversation-config.json");
    }

    private static async Task<string> CreateTrackedFakeCodexExecutableAsync(TestWorkspace workspace)
    {
        var scriptPath = workspace.File("tracked-fake-codex.js");
        var commandPath = workspace.File(OperatingSystem.IsWindows() ? "tracked-fake-codex.cmd" : "tracked-fake-codex");
        await File.WriteAllTextAsync(scriptPath, """
            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli 999.0.0-test\n");
              process.exit(0);
            }

            const crypto = require("node:crypto");
            const fs = require("node:fs");
            const path = require("node:path");
            const readline = require("node:readline");
            const instanceId = crypto.randomUUID().replaceAll("-", "");
            const threadId = `thread-${instanceId}`;
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            fs.appendFileSync(path.join(__dirname, "web-app-server-instances.txt"), `${instanceId}\n`);

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            function userText(message) {
              const inputText = String(message.params?.input?.[0]?.text ?? "");
              const marker = "Current user message:";
              const markerIndex = inputText.indexOf(marker);
              return markerIndex < 0 ? inputText : inputText.slice(markerIndex + marker.length).trim();
            }

            input.on("line", (line) => {
              const message = JSON.parse(line);
              switch (message.method) {
                case "initialize":
                  write({ id: message.id, result: {} });
                  break;
                case "model/list":
                  write({ id: message.id, result: { data: ["test-model", "gpt-test"].map((model) => ({ id: model, model })) } });
                  break;
                case "thread/start":
                  const model = String(message.params?.model ?? "");
                  const modelProvider = String(message.params?.modelProvider ?? "");
                  write({ id: message.id, result: { model, modelProvider, thread: { id: threadId, modelProvider } } });
                  break;
                case "turn/start": {
                  const turnId = `turn-${instanceId}`;
                  const text = `web response: ${userText(message)}`;
                  fs.appendFileSync(path.join(__dirname, "web-provider-turns.txt"), `${instanceId}:${userText(message)}\n`);
                  write({ id: message.id, result: { turn: { id: turnId } } });
                  if (userText(message).includes("hello from web")) {
                    fs.writeFileSync(path.join(__dirname, "web-turn-cancellation.marker"), instanceId);
                    break;
                  }
                  write({ method: "item/agentMessage/delta", params: { threadId, turnId, delta: text } });
                  write({ method: "turn/completed", params: { threadId, turnId, turn: { id: turnId, status: "completed", items: [{ type: "agentMessage", phase: "final_answer", text }] } } });
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
                node "%~dp0tracked-fake-codex.js" %*
                """);
        }
        else
        {
            await File.WriteAllTextAsync(commandPath, """
                #!/bin/sh
                exec node "$(dirname "$0")/tracked-fake-codex.js" "$@"
                """);
            File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return commandPath;
    }

}
