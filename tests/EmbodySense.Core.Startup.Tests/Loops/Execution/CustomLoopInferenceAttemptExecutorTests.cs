using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class CustomLoopInferenceAttemptExecutorTests
{
    private const string DefinitionHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ExecuteAsync_creates_and_disposes_a_fresh_transport_for_each_attempt_and_uses_the_pinned_model()
    {
        using var workspace = new TestWorkspace();
        var clients = new List<AsyncFakeInferenceClient>();
        var observedOptions = new List<LlmInferenceClientOptions>();
        var observedBrokers = new List<IToolBroker?>();
        var executor = CreateExecutor(workspace, (_, _, _) => Task.FromResult(Response("completed", "pinned-model", "provider-response")),
            (options, broker, behavior) =>
            {
                observedOptions.Add(options);
                observedBrokers.Add(broker);
                var client = new AsyncFakeInferenceClient(broker, behavior);
                clients.Add(client);
                return client;
            });

        var first = await executor.ExecuteAsync(CreateRequest());
        var second = await executor.ExecuteAsync(CreateRequest() with { Attempt = 2, AttemptCorrelationId = "attempt-2" });

        Assert.Equal(2, clients.Count);
        Assert.NotSame(clients[0], clients[1]);
        Assert.All(clients, client => Assert.True(client.Disposed));
        Assert.All(observedBrokers, Assert.Null);
        Assert.All(observedOptions, options => Assert.Equal("pinned-model", options.Model));
        Assert.All(observedOptions, options => Assert.Equal(Path.GetFullPath(workspace.RootPath), options.WorkingDirectory));
        Assert.Equal("completed", first.OutputText);
        Assert.Equal(nameof(LlmInferenceSurface.OpenAiCodex), first.Provider);
        Assert.Equal("pinned-model", first.Model);
        Assert.Equal("provider-response", first.ProviderResponseId);
        Assert.Equal(0, first.ToolRequestsConsumed);
        Assert.Equal(first with { }, second);
    }

    [Fact]
    public async Task ExecuteAsync_keeps_exit_attempts_toolless()
    {
        using var workspace = new TestWorkspace();
        IToolBroker? observedBroker = null;
        var executor = CreateExecutor(workspace, (_, _, _) => Task.FromResult(Response()),
            (options, broker, behavior) =>
            {
                observedBroker = broker;
                return new AsyncFakeInferenceClient(broker, behavior);
            });
        var request = CreateRequest() with { StepId = "exit", IsExit = true };

        var result = await executor.ExecuteAsync(request);

        Assert.Null(observedBroker);
        Assert.Equal(0, result.ToolRequestsConsumed);
    }

    [Fact]
    public async Task ExecuteAsync_exposes_only_exact_admitted_commands_and_correlates_every_governance_audit()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "hello from system");
        var toolResults = new List<ToolResult>();
        var executor = CreateExecutor(workspace, async (broker, inferenceRequest, cancellationToken) =>
        {
            Assert.NotNull(broker);
            Assert.Equal([ToolCommand.List, ToolCommand.Read, ToolCommand.Search], broker.AvailableCommands);
            toolResults.Add(await broker.ExecuteAsync(new ToolRequest(ToolCommand.List, "system"), cancellationToken));
            toolResults.Add(await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken));
            toolResults.Add(await broker.ExecuteAsync(new ToolRequest(ToolCommand.Search, "system", Pattern: "hello"), cancellationToken));
            toolResults.Add(await broker.ExecuteAsync(new ToolRequest(ToolCommand.Write, Path.Combine("generated", "forged.txt"), Content: "forged"), cancellationToken));
            return Response();
        });
        var request = CreateRequest(
            allowTools: true,
            assignments: [CustomLoopToolAssignment.Search, CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read]);

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(4, result.ToolRequestsConsumed);
        Assert.Collection(
            toolResults,
            item => Assert.Equal(ToolExecutionOutcome.Succeeded, item.Outcome),
            item => Assert.Equal(ToolExecutionOutcome.Succeeded, item.Outcome),
            item => Assert.Equal(ToolExecutionOutcome.Succeeded, item.Outcome),
            item => Assert.Equal(ToolExecutionOutcome.Denied, item.Outcome));
        Assert.False(File.Exists(Path.Combine(paths.WorkspaceGeneratedPath, "forged.txt")));
        Assert.All(toolResults, result => Assert.Equal(CreateCorrelation(), result.Request.AuditCorrelation));

        var events = await new AuditLog(paths).ReadTailAsync(100);
        var authorityEvents = events.Where(item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate).ToArray();
        Assert.Equal(4, authorityEvents.Length);
        Assert.Contains(authorityEvents, item => item.Outcome == AuditSchema.Outcomes.Denied && Metadata(item, "command") == "write");
        Assert.All(authorityEvents, AssertCorrelation);
        Assert.All(events.Where(item => item.Action is AuditSchema.Actions.ToolPermissionEvaluate or AuditSchema.Actions.ToolExecute), AssertCorrelation);
    }

    [Fact]
    public async Task ExecuteAsync_reloads_the_permission_policy_before_each_tool_call()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        var file = Path.Combine(paths.WorkspaceSystemPath, "note.txt");
        await File.WriteAllTextAsync(file, "reload me");
        var approvalPrompt = new RecordingApprovalPrompt(approved: false);
        var outcomes = new List<ToolExecutionOutcome>();
        var executor = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            outcomes.Add((await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken)).Outcome);
            await File.WriteAllTextAsync(paths.PermissionsPath, "{}", cancellationToken);
            outcomes.Add((await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken)).Outcome);
            return Response();
        }, approvalPrompt: approvalPrompt);

        var result = await executor.ExecuteAsync(CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read]));

        Assert.Equal([ToolExecutionOutcome.Succeeded, ToolExecutionOutcome.ApprovalRejected], outcomes);
        Assert.Equal(2, result.ToolRequestsConsumed);
        var approval = Assert.Single(approvalPrompt.Requests);
        Assert.Equal("read", approval.Command);
        Assert.Equal(Path.Combine("system", "note.txt"), approval.TargetPath);
    }

    [Fact]
    public async Task ExecuteAsync_reloads_role_authority_before_each_tool_call_and_denies_revoked_commands()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "do not read after revocation");
        var authorityProvider = new RevokingAuthorityProvider();
        ToolResult? observed = null;
        var executor = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            observed = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken);
            return Response();
        }, authorityProvider: authorityProvider);

        var result = await executor.ExecuteAsync(CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read]));

        Assert.Equal(2, authorityProvider.ResolveCount);
        Assert.Equal(1, result.ToolRequestsConsumed);
        Assert.Equal(ToolExecutionOutcome.Denied, Assert.IsType<ToolResult>(observed).Outcome);
        var authorityEvent = Assert.Single(await new AuditLog(paths).ReadTailAsync(100), item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate);
        Assert.Equal(AuditSchema.Outcomes.Denied, authorityEvent.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_denies_and_audits_the_sixth_tool_request_in_an_attempt()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "bounded");
        var outcomes = new List<ToolExecutionOutcome>();
        var executor = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            for (var index = 0; index < 5; index++)
            {
                outcomes.Add((await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken)).Outcome);
            }

            Assert.Empty(broker.AvailableCommands);
            outcomes.Add((await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken)).Outcome);

            return Response();
        });

        var result = await executor.ExecuteAsync(CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read]));

        Assert.Equal(6, result.ToolRequestsConsumed);
        Assert.Equal(5, outcomes.Count(item => item == ToolExecutionOutcome.Succeeded));
        Assert.Equal(ToolExecutionOutcome.Denied, outcomes[^1]);
        var events = await new AuditLog(paths).ReadTailAsync(100);
        var limitEvent = Assert.Single(events, item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate && Metadata(item, "limit_scope") == "attempt");
        Assert.Equal(AuditSchema.Outcomes.Denied, limitEvent.Outcome);
        Assert.Equal("6", Metadata(limitEvent, "tool_request_ordinal"));
        Assert.Equal("5", Metadata(limitEvent, "limit"));
        AssertCorrelation(limitEvent);
    }

    [Theory]
    [InlineData(29, 2, 2, 1)]
    [InlineData(30, 1, 1, 0)]
    public async Task ExecuteAsync_enforces_the_persisted_run_tool_limit(int callsAlreadyUsed, int attemptedCalls, int expectedConsumed, int expectedSucceeded)
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "bounded");
        var outcomes = new List<ToolExecutionOutcome>();
        var executor = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            Assert.Equal(callsAlreadyUsed < CustomLoopLimits.MaxGovernedToolRequestsPerRun, broker.AvailableCommands.Count > 0);
            for (var index = 0; index < attemptedCalls; index++)
            {
                outcomes.Add((await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken)).Outcome);
            }

            return Response();
        });
        var request = CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read]) with { ToolRequestsUsedInRun = callsAlreadyUsed };

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(expectedConsumed, result.ToolRequestsConsumed);
        Assert.Equal(expectedSucceeded, outcomes.Count(item => item == ToolExecutionOutcome.Succeeded));
        Assert.Equal(ToolExecutionOutcome.Denied, outcomes[^1]);
        var events = await new AuditLog(paths).ReadTailAsync(100);
        var limitEvent = Assert.Single(events, item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate && Metadata(item, "limit_scope") == "run");
        Assert.Equal("30", Metadata(limitEvent, "limit"));
        AssertCorrelation(limitEvent);
    }

    [Fact]
    public async Task ExecuteAsync_records_integrity_and_fails_when_request_repeats_after_visible_over_limit_denial()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "bounded");
        var evidenceSink = new RecordingEvidenceSink();
        var executor = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            for (var index = 0; index < 6; index++)
            {
                await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken);
            }

            await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken);
            return Response();
        }, evidenceSink: evidenceSink);

        await Assert.ThrowsAsync<CustomLoopToolEvidenceIntegrityException>(() => executor.ExecuteAsync(CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read])));

        Assert.DoesNotContain(evidenceSink.Evidence, item => item.RequestOrdinal == 7);
        Assert.Contains(evidenceSink.Evidence, item => item is { RequestOrdinal: 6, Phase: CustomLoopToolEvidencePhase.IntegrityFailed });
    }

    [Fact]
    public async Task ExecuteAsync_persists_the_thirty_first_denial_and_repeat_integrity_without_actuation()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "bounded");
        var store = new CustomLoopRunStore(paths);
        var admitted = await CreateAdmittedRunAsync(store);
        var evidenceSink = new CustomLoopRunToolEvidenceSink(store);
        var inner = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            var denied = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken);
            Assert.Equal(ToolExecutionOutcome.Denied, denied.Outcome);
            await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken);
            return Response();
        }, evidenceSink: evidenceSink);
        var executor = new RunLimitAttemptExecutor(inner);
        var runner = new CustomLoopOrderedRunner(store, new CustomLoopContextResolver(), executor, new PublishedConversation(), new AuditLog(paths), new TestAuthorityProvider());

        var execution = await runner.RunAsync(new CustomLoopOrderedRunRequest(admitted.Id, "web"));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, execution.Status);
        var reloaded = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var toolEvents = reloaded.Events.Where(item => item.ToolEvidence is not null).ToArray();
        Assert.True(toolEvents.Length == 5, $"Phases: {string.Join(',', toolEvents.Select(item => $"{item.ToolEvidence!.Phase}:{item.ToolEvidence.ReturnedToModel}"))}. Failure: {reloaded.FailureDetail}");
        Assert.Single(toolEvents, item => item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.RequestReserved);
        Assert.Single(toolEvents, item => item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.IntegrityFailed);
        Assert.Single(toolEvents.Select(item => item.ToolEvidence!.RequestCorrelationId).Distinct(StringComparer.Ordinal));
        var sourceEvent = Assert.Single(toolEvents, item => item.ToolEvidence!.Governance is not null && item.ToolEvidence.Phase == CustomLoopToolEvidencePhase.GovernanceDecided);
        var projectedRun = Assert.IsType<LoopRunSnapshot>(await new LoopRunInspectionFacade(workspace.RootPath).GetAsync(admitted.Id));
        AssertToolEvidenceProjection(sourceEvent, Assert.Single(projectedRun.Events, item => item.Sequence == sourceEvent.Sequence));
        var audit = await new AuditLog(paths).ReadTailAsync(200);
        Assert.DoesNotContain(audit, item => item.Action == AuditSchema.Actions.ToolExecute);
        Assert.Single(audit, item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate && Metadata(item, "limit_scope") == "run");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_audits_bounded_hash_and_length_then_fails_closed_for_malformed_unreservable_requests(bool invalidCommand)
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        var oversizedTarget = new string('x', CustomLoopLimits.MaxGovernedToolTargetCharacters + 1);
        var malformed = invalidCommand ? new ToolRequest((ToolCommand)999, "system") : new ToolRequest(ToolCommand.Read, oversizedTarget);
        var executor = CreateExecutor(workspace, async (broker, inferenceRequest, cancellationToken) =>
        {
            Assert.NotNull(broker);
            await broker.ExecuteAsync(malformed, cancellationToken);
            return Response();
        });

        await Assert.ThrowsAsync<CustomLoopToolEvidenceIntegrityException>(() => executor.ExecuteAsync(CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read])));

        var auditEvent = Assert.Single(await new AuditLog(paths).ReadTailAsync(100), item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate);
        Assert.Equal(AuditSchema.Outcomes.Failed, auditEvent.Outcome);
        Assert.Equal("malformed-tool-request", auditEvent.Target);
        Assert.Equal("1", Metadata(auditEvent, "tool_request_ordinal"));
        if (!invalidCommand)
        {
            Assert.Equal(oversizedTarget.Length.ToString(), Metadata(auditEvent, "target_characters"));
            Assert.Equal(CustomLoopTraceContentHash.Compute(oversizedTarget), Metadata(auditEvent, "target_hash"));
            Assert.DoesNotContain(oversizedTarget, await File.ReadAllTextAsync(paths.EventsLogPath), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExecuteAsync_disposes_the_transport_when_inference_throws()
    {
        using var workspace = new TestWorkspace();
        AsyncFakeInferenceClient? client = null;
        var executor = CreateExecutor(workspace, (_, _, _) => throw new IOException("provider exploded"),
            (options, broker, behavior) => client = new AsyncFakeInferenceClient(broker, behavior));

        var providerRequestStarted = false;
        var exception = await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(CreateRequest(), providerRequestStarted: () => providerRequestStarted = true));

        Assert.Equal("provider exploded", exception.Message);
        Assert.True(providerRequestStarted);
        Assert.NotNull(client);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task ExecuteAsync_preserves_successful_inference_when_transport_disposal_throws()
    {
        using var workspace = new TestWorkspace();
        ThrowingDisposeInferenceClient? client = null;
        var executor = CreateInjectedExecutor(
            CreateOptions(workspace),
            new RecordingApprovalPrompt(),
            (_, _) => client = new ThrowingDisposeInferenceClient());

        var result = await executor.ExecuteAsync(CreateRequest());

        Assert.Equal("completed", result.OutputText);
        Assert.NotNull(client);
        Assert.True(client.DisposeAttempted);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_report_dispatch_when_transport_construction_fails()
    {
        using var workspace = new TestWorkspace();
        var providerRequestStarted = false;
        var executor = CreateInjectedExecutor(CreateOptions(workspace), new RecordingApprovalPrompt(), (_, _) => throw new FileNotFoundException("codex executable missing"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => executor.ExecuteAsync(CreateRequest(), providerRequestStarted: () => providerRequestStarted = true));

        Assert.False(providerRequestStarted);
    }

    [Fact]
    public async Task ExecuteAsync_supports_sync_disposal_and_rejects_non_disposable_transports()
    {
        using var workspace = new TestWorkspace();
        SyncFakeInferenceClient? syncClient = null;
        var syncExecutor = CreateInjectedExecutor(
            CreateOptions(workspace),
            new RecordingApprovalPrompt(),
            (_, _) => syncClient = new SyncFakeInferenceClient());

        await syncExecutor.ExecuteAsync(CreateRequest());

        Assert.NotNull(syncClient);
        Assert.True(syncClient.Disposed);

        var invalidExecutor = CreateInjectedExecutor(
            CreateOptions(workspace),
            new RecordingApprovalPrompt(),
            (_, _) => new NonDisposableFakeInferenceClient());
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalidExecutor.ExecuteAsync(CreateRequest()));

        var nullExecutor = CreateInjectedExecutor(
            CreateOptions(workspace),
            new RecordingApprovalPrompt(),
            (_, _) => null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => nullExecutor.ExecuteAsync(CreateRequest()));
    }

    [Fact]
    public async Task ExecuteAsync_rejects_malformed_or_escalated_requests_before_constructing_a_transport()
    {
        using var workspace = new TestWorkspace();
        var factoryCalls = 0;
        var executor = CreateInjectedExecutor(
            CreateOptions(workspace),
            new RecordingApprovalPrompt(),
            (_, _) =>
            {
                factoryCalls++;
                return new AsyncFakeInferenceClient(null, (_, _, _) => Task.FromResult(Response()));
            });
        var valid = CreateRequest();

        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ExecuteAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { RunId = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { LoopId = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { RoleId = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { DefinitionHash = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { StepId = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { AttemptCorrelationId = " " }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ExecuteAsync(valid with { ModelSnapshot = null! }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { ModelSnapshot = new CustomLoopModelSnapshot(" ", "model") }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ExecuteAsync(valid with { AdmittedToolAssignments = null! }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ExecuteAsync(valid with { InferenceRequest = null! }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.ExecuteAsync(valid with { DefinitionVersion = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.ExecuteAsync(valid with { Iteration = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.ExecuteAsync(valid with { Attempt = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { DefinitionHash = new string('A', 64) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.ExecuteAsync(valid with { ToolRequestsUsedInRun = -1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.ExecuteAsync(valid with { ToolRequestsUsedInRun = 32 }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { ModelSnapshot = new CustomLoopModelSnapshot("azure", "model") }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { AllowTools = true, AdmittedToolAssignments = [CustomLoopToolAssignment.Unknown] }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { AllowTools = true, AdmittedToolAssignments = [CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Read] }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { IsExit = true, StepId = "exit", AllowTools = true, AdmittedToolAssignments = [CustomLoopToolAssignment.Read] }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { StepId = "exit" }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(valid with { AllowTools = true }));

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task ExecuteAsync_accepts_the_configured_azure_provider_alias()
    {
        using var workspace = new TestWorkspace();
        var options = CreateOptions(workspace) with { Surface = LlmInferenceSurface.AzureAiFoundry };
        var executor = CreateInjectedExecutor(
            options,
            new RecordingApprovalPrompt(),
            (_, broker) => new AsyncFakeInferenceClient(broker, (_, _, _) => Task.FromResult(new LlmInferenceResponse("azure", LlmInferenceSurface.AzureAiFoundry))));
        var request = CreateRequest() with { ModelSnapshot = new CustomLoopModelSnapshot("azure-ai-foundry", "pinned") };

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(nameof(LlmInferenceSurface.AzureAiFoundry), result.Provider);
    }

    [Fact]
    public async Task Model_availability_rejects_a_configured_surface_without_a_production_adapter()
    {
        using var workspace = new TestWorkspace();
        var options = CreateOptions(workspace) with { Surface = LlmInferenceSurface.AzureAiFoundry };
        var executor = new CustomLoopInferenceAttemptExecutor(options, (IAgentToolApprovalPrompt)new RecordingApprovalPrompt());

        var available = await executor.IsAvailableAsync(new CustomLoopModelSnapshot("azure-ai-foundry", "configured-model"));

        Assert.False(available);
    }

    [Theory]
    [InlineData("openai", "configured-model", true)]
    [InlineData("openai-codex", "configured-model", true)]
    [InlineData("azure-ai-foundry", "configured-model", false)]
    [InlineData("openai", "different-model", false)]
    public async Task Model_availability_requires_the_configured_provider_and_exact_model(string admittedProvider, string? admittedModel, bool expected)
    {
        using var workspace = new TestWorkspace();
        var executor = CreateInjectedExecutor(CreateOptions(workspace), new RecordingApprovalPrompt(), (_, _) => throw new InvalidOperationException("Availability checks must not construct a provider transport."));

        var available = await executor.IsAvailableAsync(new CustomLoopModelSnapshot(admittedProvider, admittedModel));

        Assert.Equal(expected, available);
    }

    [Fact]
    public async Task Model_availability_preserves_an_explicit_provider_default_without_substituting_a_configured_model()
    {
        using var workspace = new TestWorkspace();
        var options = CreateOptions(workspace) with { Model = null };
        var executor = CreateInjectedExecutor(options, new RecordingApprovalPrompt(), (_, _) => throw new InvalidOperationException("Availability checks must not construct a provider transport."));

        Assert.True(await executor.IsAvailableAsync(new CustomLoopModelSnapshot(nameof(LlmInferenceSurface.OpenAiCodex), null)));
        Assert.False(await executor.IsAvailableAsync(new CustomLoopModelSnapshot(nameof(LlmInferenceSurface.OpenAiCodex), "configured-model")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.IsAvailableAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.IsAvailableAsync(new CustomLoopModelSnapshot(" ", null)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.IsAvailableAsync(new CustomLoopModelSnapshot(nameof(LlmInferenceSurface.OpenAiCodex), null), cancelled.Token));
    }

    [Fact]
    public void Constructor_requires_explicit_runtime_dependencies()
    {
        using var workspace = new TestWorkspace();
        var options = CreateOptions(workspace);
        var prompt = new RecordingApprovalPrompt();

        Assert.Throws<ArgumentNullException>(() => new CustomLoopInferenceAttemptExecutor(null!, (IAgentToolApprovalPrompt)prompt));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopInferenceAttemptExecutor(options, (IAgentToolApprovalPrompt)null!));
        Assert.Throws<ArgumentException>(() => new CustomLoopInferenceAttemptExecutor(options with { WorkingDirectory = " " }, (IAgentToolApprovalPrompt)prompt));
    }

    private static CustomLoopInferenceAttemptExecutor CreateExecutor(
        TestWorkspace workspace,
        Func<IToolBroker?, LlmInferenceRequest, CancellationToken, Task<LlmInferenceResponse>> behavior,
        Func<LlmInferenceClientOptions, IToolBroker?, Func<IToolBroker?, LlmInferenceRequest, CancellationToken, Task<LlmInferenceResponse>>, ILlmInferenceClient>? factory = null,
        RecordingApprovalPrompt? approvalPrompt = null,
        ICustomLoopToolEvidenceSink? evidenceSink = null,
        ICustomLoopToolAuthorityProvider? authorityProvider = null)
    {
        var effectivePrompt = approvalPrompt ?? new RecordingApprovalPrompt();
        return new CustomLoopInferenceAttemptExecutor(
            CreateOptions(workspace),
            (IToolApprovalPrompt)effectivePrompt,
            authorityProvider ?? new TestAuthorityProvider(),
            evidenceSink ?? new NullEvidenceSink(),
            (options, broker) => factory?.Invoke(options, broker, behavior) ?? new AsyncFakeInferenceClient(broker, behavior));
    }

    private static CustomLoopInferenceAttemptExecutor CreateInjectedExecutor(
        LlmInferenceClientOptions options,
        RecordingApprovalPrompt approvalPrompt,
        CustomLoopInferenceClientFactory clientFactory)
    {
        return new CustomLoopInferenceAttemptExecutor(options, (IToolApprovalPrompt)approvalPrompt, new TestAuthorityProvider(), new NullEvidenceSink(), clientFactory);
    }

    private static LlmInferenceClientOptions CreateOptions(TestWorkspace workspace)
    {
        return new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "configured-model",
            WorkingDirectory = workspace.RootPath,
            CodexSandbox = "read-only"
        };
    }

    private static CustomLoopInferenceAttemptRequest CreateRequest(
        bool allowTools = false,
        IReadOnlyList<CustomLoopToolAssignment>? assignments = null)
    {
        return new CustomLoopInferenceAttemptRequest(
            "run-1",
            "loop-1",
            "role-1",
            1,
            DefinitionHash,
            1,
            "step-one",
            1,
            "attempt-1",
            IsExit: false,
            AllowTools: allowTools,
            new CustomLoopModelSnapshot("openai", "pinned-model"),
            assignments ?? [],
            ToolRequestsUsedInRun: 0,
            LlmInferenceRequest.FromUserText("prompt"));
    }

    private static async Task<CustomLoopRunRecord> CreateAdmittedRunAsync(CustomLoopRunStore store)
    {
        var now = DateTimeOffset.UtcNow.ToUniversalTime();
        var definition = CustomLoopDefinition.CreateSeed("loop-real-limit", "role-1", "step-one", "create-real-limit", now) with
        {
            ToolAssignments = [CustomLoopToolAssignment.Read]
        };
        definition = CustomLoopDefinitionContentHash.Apply(definition with { ContentHash = string.Empty });
        var authority = (await new TestAuthorityProvider().ResolveAsync(definition.RoleId, definition.ToolAssignments)) with { EvaluatedAtUtc = now };
        var admittedEvent = new CustomLoopRunEvent(1, "event-admitted", now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, "openai", "pinned-model", null, null, authority);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-real-limit",
            definition.Id,
            1,
            CustomLoopRunStatus.Admitted,
            now,
            now,
            null,
            "web",
            new CustomLoopModelSnapshot("openai", "pinned-model"),
            "invoke-real-limit",
            "test-user",
            string.Empty,
            definition,
            "prompt",
            null,
            CustomLoopContextSnapshot.CreateEmpty(now),
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admittedEvent],
            null,
            null,
            null);
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(run)).Status);
        var auditMarker = new CustomLoopRunEvent(2, "event-admission-audit", now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null);
        var audited = run with { LifecycleVersion = 2, Events = [.. run.Events, auditMarker] };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(audited, run.LifecycleVersion)).Status);
        return audited;
    }

    private static ToolAuditCorrelation CreateCorrelation()
    {
        var admitted = new[] { CustomLoopToolAssignment.Search, CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read };
        var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
        return new ToolAuditCorrelation(
            "run-1",
            "loop-1",
            "role-1",
            1,
            DefinitionHash,
            1,
            "step-one",
            1,
            "attempt-1",
            "list,read,search",
            "list,read,search",
            "list,read,search",
            CustomLoopTraceContentHash.Compute("role-1\n" + string.Join('\n', admitted)),
            CustomLoopTraceContentHash.Compute(string.Join('\n', catalog)));
    }

    private static LlmInferenceResponse Response(string output = "done", string? model = "pinned-model", string? responseId = "response-1")
    {
        return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex, model, responseId);
    }

    private static async Task<WorkspacePaths> InitializeWorkspaceAsync(TestWorkspace workspace)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        Directory.CreateDirectory(paths.WorkspaceSystemPath);
        Directory.CreateDirectory(paths.WorkspaceGeneratedPath);
        await File.WriteAllTextAsync(paths.PermissionsPath, PermissionsDocument.CreateDefault(paths).ToJson());
        return paths;
    }

    private static string? Metadata(EmbodySense.Core.Common.Governance.Audit.Models.AuditEvent auditEvent, string key)
    {
        return auditEvent.Metadata.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static void AssertCorrelation(EmbodySense.Core.Common.Governance.Audit.Models.AuditEvent auditEvent)
    {
        Assert.Equal("run-1", Metadata(auditEvent, "run_id"));
        Assert.Equal("loop-1", Metadata(auditEvent, "loop_id"));
        Assert.Equal("role-1", Metadata(auditEvent, "role_id"));
        Assert.Equal("1", Metadata(auditEvent, "definition_version"));
        Assert.Equal(DefinitionHash, Metadata(auditEvent, "definition_hash"));
        Assert.Equal("1", Metadata(auditEvent, "iteration"));
        Assert.Equal("step-one", Metadata(auditEvent, "step_id"));
        Assert.Equal("1", Metadata(auditEvent, "attempt"));
        Assert.Equal("attempt-1", Metadata(auditEvent, "attempt_correlation_id"));
    }

    private static void AssertToolEvidenceProjection(CustomLoopRunEvent sourceEvent, LoopRunEventSnapshot projectedEvent)
    {
        var source = Assert.IsType<CustomLoopToolTraceEvidence>(sourceEvent.ToolEvidence);
        var projected = Assert.IsType<LoopRunToolEvidenceSnapshot>(projectedEvent.ToolEvidence);
        Assert.Equal(source.Phase.ToString(), projected.Phase);
        Assert.Equal(source.RequestOrdinal, projected.RequestOrdinal);
        Assert.Equal(source.RequestCorrelationId, projected.RequestCorrelationId);
        Assert.Equal(source.BrokerRequestId, projected.BrokerRequestId);
        Assert.Equal(source.Command.ToString(), projected.Command);
        Assert.Equal(source.TargetPath, projected.TargetPath);
        Assert.Equal(source.Content, projected.Content);
        Assert.Equal(source.Pattern, projected.Pattern);
        Assert.Equal(source.ResolvedTarget, projected.ResolvedTarget);
        Assert.Equal(source.Outcome?.ToString(), projected.Outcome);
        Assert.Equal(source.CanonicalResultReturnedToModel, projected.CanonicalResultReturnedToModel);
        Assert.Equal(source.CanonicalResultHash, projected.CanonicalResultHash);
        Assert.Equal(source.CanonicalResultCharacterCount, projected.CanonicalResultCharacterCount);
        Assert.Equal(source.ReturnedToModel, projected.ReturnedToModel);
        Assert.Equal(source.ReservedUtf8Bytes, projected.ReservedUtf8Bytes);
        Assert.Equal(source.Authority.RoleId, projected.Authority.RoleId);
        Assert.Equal(source.Authority.AdmittedMaximum.Select(value => value.ToString()), projected.Authority.AdmittedMaximum);
        Assert.Equal(source.Authority.CurrentRoleCeiling.Select(value => value.ToString()), projected.Authority.CurrentRoleCeiling);
        Assert.Equal(source.Authority.ImplementedCatalog.Select(value => value.ToString()), projected.Authority.ImplementedCatalog);
        Assert.Equal(source.Authority.EffectiveAssignments.Select(value => value.ToString()), projected.Authority.EffectiveAssignments);
        Assert.Equal(source.Authority.RoleCeilingHash, projected.Authority.RoleCeilingHash);
        Assert.Equal(source.Authority.CatalogHash, projected.Authority.CatalogHash);
        Assert.Equal(source.Authority.EvaluatedAtUtc, projected.Authority.EvaluatedAtUtc);
        Assert.Equal(source.Authority.IsValid, projected.Authority.IsValid);
        Assert.Equal(source.Authority.Detail, projected.Authority.Detail);
        var sourceGovernance = Assert.IsType<ToolGovernanceEvidence>(source.Governance);
        var projectedGovernance = Assert.IsType<LoopRunToolGovernanceSnapshot>(projected.Governance);
        Assert.Equal(sourceGovernance.AuthorityDecision.ToString(), projectedGovernance.AuthorityDecision);
        Assert.Equal(sourceGovernance.AuthorityDetail, projectedGovernance.AuthorityDetail);
        Assert.Equal(sourceGovernance.PermissionDecision?.ToString(), projectedGovernance.PermissionDecision);
        Assert.Equal(sourceGovernance.PermissionMatchedPath, projectedGovernance.PermissionMatchedPath);
        Assert.Equal(sourceGovernance.PermissionDetail, projectedGovernance.PermissionDetail);
        Assert.Equal(sourceGovernance.PermissionPolicyHash, projectedGovernance.PermissionPolicyHash);
        Assert.Equal(sourceGovernance.ApprovalDecision.ToString(), projectedGovernance.ApprovalDecision);
        Assert.Equal(sourceGovernance.ApprovalDecisionBy, projectedGovernance.ApprovalDecisionBy);
        Assert.Equal(sourceGovernance.ApprovalDetail, projectedGovernance.ApprovalDetail);
    }

    private sealed class RecordingApprovalPrompt(bool approved = false) : IAgentToolApprovalPrompt, IToolApprovalPrompt
    {
        public List<AgentToolApprovalRequest> Requests { get; } = [];

        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult((approved, "test", approved ? "approved" : "rejected"));
        }

        async Task<ToolApprovalResponse> IToolApprovalPrompt.RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        {
            var agentRequest = new AgentToolApprovalRequest(
                request.RequestId,
                request.ToolRequest.Command.ToString().ToLowerInvariant(),
                request.ToolRequest.TargetPath,
                request.ResolvedPath,
                request.Operation.ToString().ToLowerInvariant(),
                request.PermissionEvaluation.MatchedPath,
                request.PermissionEvaluation.Detail);
            var response = await RequestApprovalAsync(agentRequest, cancellationToken);
            return response.Approved
                ? ToolApprovalResponse.Approve(response.DecisionBy, response.Detail)
                : ToolApprovalResponse.Reject(response.DecisionBy, response.Detail);
        }
    }

    private sealed class TestAuthorityProvider : ICustomLoopToolAuthorityProvider
    {
        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
        {
            var admitted = admittedMaximum.ToArray();
            var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
            return Task.FromResult(new CustomLoopToolAuthoritySnapshot(
                roleId,
                admitted,
                admitted,
                catalog,
                admitted,
                CustomLoopTraceContentHash.Compute(roleId + "\n" + string.Join('\n', admitted)),
                CustomLoopTraceContentHash.Compute(string.Join('\n', catalog)),
                DateTimeOffset.UtcNow,
                true,
                "Test authority preserves the immutable admitted maximum."));
        }
    }

    private sealed class RevokingAuthorityProvider : ICustomLoopToolAuthorityProvider
    {
        public int ResolveCount { get; private set; }

        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            var admitted = admittedMaximum.ToArray();
            var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
            var current = ResolveCount == 1 ? admitted : [];
            return Task.FromResult(new CustomLoopToolAuthoritySnapshot(
                roleId,
                admitted,
                current,
                catalog,
                current,
                CustomLoopTraceContentHash.Compute(roleId + "\n" + string.Join('\n', current)),
                CustomLoopTraceContentHash.Compute(string.Join('\n', catalog)),
                DateTimeOffset.UtcNow,
                true,
                ResolveCount == 1 ? "Initial admitted authority." : "Authority revoked before tool actuation."));
        }
    }

    private sealed class NullEvidenceSink : ICustomLoopToolEvidenceSink
    {
        public Task RecordAsync(string runId, int iteration, string stepId, int attempt, CustomLoopToolTraceEvidence evidence, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEvidenceSink : ICustomLoopToolEvidenceSink
    {
        public List<CustomLoopToolTraceEvidence> Evidence { get; } = [];

        public Task RecordAsync(string runId, int iteration, string stepId, int attempt, CustomLoopToolTraceEvidence evidence, CancellationToken cancellationToken = default)
        {
            Evidence.Add(evidence);
            return Task.CompletedTask;
        }
    }

    private sealed class RunLimitAttemptExecutor(CustomLoopInferenceAttemptExecutor inner) : ICustomLoopInferenceAttemptExecutor
    {
        public Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null)
        {
            return inner.ExecuteAsync(request with { ToolRequestsUsedInRun = CustomLoopLimits.MaxGovernedToolRequestsPerRun }, cancellationToken, providerRequestStarted);
        }
    }

    private sealed class PublishedConversation : ICustomLoopConversationPublisher
    {
        public Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default)
        {
            request.AppendStarted?.Invoke();
            return Task.FromResult(new CustomLoopConversationPublicationResult(CustomLoopConversationPublicationOutcome.Published, request.OperationId, "Published."));
        }
    }

    private sealed class ThrowingDisposeInferenceClient : ILlmInferenceClient, IAsyncDisposable
    {
        public bool DisposeAttempted { get; private set; }

        public Task<LlmInferenceResponse> GenerateAsync(LlmInferenceRequest request, Func<string, CancellationToken, Task>? responseChunkHandler = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Response("completed"));
        }

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            return ValueTask.FromException(new IOException("transport cleanup failed"));
        }
    }

    private sealed class AsyncFakeInferenceClient(
        IToolBroker? broker,
        Func<IToolBroker?, LlmInferenceRequest, CancellationToken, Task<LlmInferenceResponse>> behavior) : ILlmInferenceClient, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            return behavior(broker, request, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyncFakeInferenceClient : ILlmInferenceClient, IDisposable
    {
        public bool Disposed { get; private set; }

        public Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Response());
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class NonDisposableFakeInferenceClient : ILlmInferenceClient
    {
        public Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Response());
        }
    }
}
