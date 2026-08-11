using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
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
        var second = await executor.ExecuteAsync(CreateRequest(attempt: 2, attemptCorrelationId: "attempt-2"));

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
    public async Task ExecuteAsync_rejects_exit_attempts_before_constructing_provider_transport()
    {
        using var workspace = new TestWorkspace();
        var factoryCalls = 0;
        var executor = CreateExecutor(workspace, (_, _, _) => Task.FromResult(Response()),
            (options, broker, behavior) =>
            {
                factoryCalls++;
                return new AsyncFakeInferenceClient(broker, behavior);
            });
        var request = CreateRequest() with { StepId = "exit", IsExit = true };

        var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => executor.ExecuteAsync(request));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest, exception.ExecutionStatus);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task ExecuteAsync_fences_the_exact_server_derived_provider_operation_through_the_public_authority_boundary()
    {
        using var workspace = new TestWorkspace();
        var boundary = new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.Direct);
        var transportWrites = 0;
        var providerStarts = 0;
        var executor = CreateExecutor(
            workspace,
            (_, _, _) =>
            {
                transportWrites++;
                return Task.FromResult(Response());
            },
            effectAuthorityBoundary: boundary);
        var request = CreateRequest();

        var result = await executor.ExecuteAsync(request, providerRequestStarted: () => providerStarts++);

        Assert.Equal("done", result.OutputText);
        var authorityRequest = Assert.Single(boundary.Requests);
        Assert.Equal(request.RunId, authorityRequest.ExecutionBinding.RunId);
        Assert.Equal(request.StepId, authorityRequest.NodeId);
        Assert.Equal(request.Attempt, authorityRequest.NodeAttempt);
        Assert.Equal(request.AttemptCorrelationId, authorityRequest.CorrelationId);
        Assert.Equal(CanonicalInferenceAuthorityTestData.ProviderOperationId(request), authorityRequest.EffectOperationId);
        Assert.Equal(GovernedLoopEffectBoundaryKind.ProviderTransport, authorityRequest.BoundaryKind);
        Assert.Equal(
            [CanonicalInferenceAuthorityTestData.ModelInferenceCapabilityId],
            authorityRequest.RequiredCapabilityPins.Select(pin => pin.DescriptorIdentity.Id.Value));
        Assert.Equal(authorityRequest.RequiredCapabilityPins.Select(pin => pin.DescriptorIdentity), authorityRequest.RequiredAuthority.Capabilities);
        Assert.Equal(1, boundary.CommitInvocations);
        Assert.Equal(1, transportWrites);
        Assert.Equal(1, providerStarts);
    }

    [Fact]
    public async Task ExecuteAsync_requires_model_and_workspace_pins_only_for_the_exact_tool_enabled_provider_node()
    {
        using var workspace = new TestWorkspace();
        var boundary = new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.Direct);
        var executor = CreateExecutor(
            workspace,
            (_, _, _) => Task.FromResult(Response()),
            effectAuthorityBoundary: boundary);

        await executor.ExecuteAsync(CreateRequest(
            allowTools: true,
            assignments: [CustomLoopToolAssignment.Read]));

        var authorityRequest = Assert.Single(boundary.Requests);
        Assert.Equal(
            [
                CanonicalInferenceAuthorityTestData.ModelInferenceCapabilityId,
                CanonicalInferenceAuthorityTestData.WorkspaceCommandCapabilityId,
            ],
            authorityRequest.RequiredCapabilityPins.Select(pin => pin.DescriptorIdentity.Id.Value));
        Assert.Equal(authorityRequest.RequiredCapabilityPins.Select(pin => pin.DescriptorIdentity), authorityRequest.RequiredAuthority.Capabilities);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_missing_partial_and_substituted_canonical_proof_before_transport_construction()
    {
        using var workspace = new TestWorkspace();
        var factoryCalls = 0;
        var boundary = new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.Direct);
        var executor = CreateInjectedExecutor(
            CreateOptions(workspace),
            new RecordingApprovalPrompt(),
            (_, _) =>
            {
                factoryCalls++;
                return new AsyncFakeInferenceClient(null, (_, _, _) => Task.FromResult(Response()));
            },
            boundary);
        var valid = CreateRequest();
        var substitutedArtifact = CreateRequest(
            allowTools: true,
            assignments: [CustomLoopToolAssignment.Read]).GraphArtifact;
        var candidates = new[]
        {
            valid with { AdmissionReceipt = null, ExecutionBinding = null, GraphArtifact = null },
            valid with { ExecutionBinding = null, GraphArtifact = null },
            valid with { GraphArtifact = substitutedArtifact },
            valid with { AllowTools = true, AdmittedToolAssignments = [CustomLoopToolAssignment.Read] },
        };

        foreach (var candidate in candidates)
        {
            var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => executor.ExecuteAsync(candidate));
            Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest, exception.ExecutionStatus);
            Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, exception.EvidenceStatus);
            Assert.Null(exception.Decision);
        }

        Assert.Equal(0, factoryCalls);
        Assert.Empty(boundary.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_canonical_proof_when_no_fresh_authority_boundary_was_composed()
    {
        using var workspace = new TestWorkspace();
        var factoryCalls = 0;
        var executor = new CustomLoopInferenceAttemptExecutor(
            CreateOptions(workspace),
            (IToolApprovalPrompt)new RecordingApprovalPrompt(),
            new TestAuthorityProvider(),
            new NullEvidenceSink(),
            new TestCapabilityAdmissionService(),
            (_, _) =>
            {
                factoryCalls++;
                return new AsyncFakeInferenceClient(null, (_, _, _) => Task.FromResult(Response()));
            },
            capabilityAuthorityTransaction: null,
            effectAuthorityBoundary: null);

        var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => executor.ExecuteAsync(CreateRequest()));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable, exception.ExecutionStatus);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, exception.EvidenceStatus);
        Assert.Null(exception.Decision);
        Assert.Equal(0, factoryCalls);
    }

    [Theory]
    [InlineData(EffectBoundaryBehavior.Deny, GovernedLoopEffectAuthorityExecutionStatus.Decided, GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, GovernedLoopEffectAuthorityDisposition.Deny)]
    [InlineData(EffectBoundaryBehavior.Pause, GovernedLoopEffectAuthorityExecutionStatus.Decided, GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, GovernedLoopEffectAuthorityDisposition.Pause)]
    [InlineData(EffectBoundaryBehavior.Ambiguous, GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, GovernedLoopEffectAuthorityDisposition.Pause)]
    public async Task ExecuteAsync_propagates_typed_stopped_dispositions_without_provider_transport(
        EffectBoundaryBehavior behavior,
        GovernedLoopEffectAuthorityExecutionStatus expectedStatus,
        GovernedLoopEffectAuthorityEvidenceStoreStatus expectedEvidence,
        GovernedLoopEffectAuthorityDisposition expectedDisposition)
    {
        using var workspace = new TestWorkspace();
        var transportWrites = 0;
        var providerStarts = 0;
        var boundary = new RecordingEffectAuthorityBoundary(behavior);
        var executor = CreateExecutor(
            workspace,
            (_, _, _) =>
            {
                transportWrites++;
                return Task.FromResult(Response());
            },
            effectAuthorityBoundary: boundary);

        var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() =>
            executor.ExecuteAsync(CreateRequest(), providerRequestStarted: () => providerStarts++));

        Assert.Equal(expectedStatus, exception.ExecutionStatus);
        Assert.Equal(expectedEvidence, exception.EvidenceStatus);
        Assert.Equal(expectedDisposition, Assert.IsType<GovernedLoopEffectAuthorityDecision>(exception.Decision).Disposition);
        Assert.Equal(0, boundary.CommitInvocations);
        Assert.Equal(0, transportWrites);
        Assert.Equal(0, providerStarts);
    }

    [Fact]
    public async Task ExecuteAsync_authority_race_stops_after_client_creation_without_crossing_provider_transport()
    {
        using var workspace = new TestWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportWrites = 0;
        var clientConstructions = 0;
        var boundary = new RecordingEffectAuthorityBoundary(
            EffectBoundaryBehavior.Pause,
            async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });
        var executor = CreateExecutor(
            workspace,
            (_, _, _) =>
            {
                transportWrites++;
                return Task.FromResult(Response());
            },
            (options, broker, behavior) =>
            {
                clientConstructions++;
                return new AsyncFakeInferenceClient(broker, behavior);
            },
            effectAuthorityBoundary: boundary);

        var execution = executor.ExecuteAsync(CreateRequest());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, clientConstructions);
        release.TrySetResult();
        var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => execution);

        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Pause, Assert.IsType<GovernedLoopEffectAuthorityDecision>(exception.Decision).Disposition);
        Assert.Equal(0, boundary.CommitInvocations);
        Assert.Equal(0, transportWrites);
    }

    [Fact]
    public async Task ExecuteAsync_hostile_double_commit_cannot_duplicate_the_provider_transport_callback()
    {
        using var workspace = new TestWorkspace();
        var transportWrites = 0;
        var providerStarts = 0;
        var boundary = new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.DoubleCommit);
        var executor = CreateExecutor(
            workspace,
            (_, _, _) =>
            {
                transportWrites++;
                return Task.FromResult(Response());
            },
            effectAuthorityBoundary: boundary);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(CreateRequest(), providerRequestStarted: () => providerStarts++));

        Assert.Contains("exactly once", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, boundary.CommitInvocations);
        Assert.Equal(1, transportWrites);
        Assert.Equal(1, providerStarts);
    }

    [Theory]
    [InlineData(EffectBoundaryBehavior.NullResult)]
    [InlineData(EffectBoundaryBehavior.MalformedResult)]
    public async Task ExecuteAsync_rejects_missing_or_malformed_boundary_results_without_provider_transport(
        EffectBoundaryBehavior behavior)
    {
        using var workspace = new TestWorkspace();
        var transportWrites = 0;
        var providerStarts = 0;
        var boundary = new RecordingEffectAuthorityBoundary(behavior);
        var executor = CreateExecutor(
            workspace,
            (_, _, _) =>
            {
                transportWrites++;
                return Task.FromResult(Response());
            },
            effectAuthorityBoundary: boundary);

        var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() =>
            executor.ExecuteAsync(CreateRequest(), providerRequestStarted: () => providerStarts++));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable, exception.ExecutionStatus);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, exception.EvidenceStatus);
        Assert.Null(exception.Decision);
        Assert.Equal(0, boundary.CommitInvocations);
        Assert.Equal(0, transportWrites);
        Assert.Equal(0, providerStarts);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_a_valid_stopped_decision_for_a_different_exact_request()
    {
        using var workspace = new TestWorkspace();
        var transportWrites = 0;
        var boundary = new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.MismatchedPause);
        var executor = CreateExecutor(
            workspace,
            (_, _, _) =>
            {
                transportWrites++;
                return Task.FromResult(Response());
            },
            effectAuthorityBoundary: boundary);

        var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => executor.ExecuteAsync(CreateRequest()));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable, exception.ExecutionStatus);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, exception.EvidenceStatus);
        Assert.Null(exception.Decision);
        Assert.Equal(0, boundary.CommitInvocations);
        Assert.Equal(0, transportWrites);
    }

    [Fact]
    public async Task ExecuteAsync_boundary_return_before_unawaited_callback_cancels_transport_before_write()
    {
        using var workspace = new TestWorkspace();
        var transportEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportWrites = 0;
        var providerStarts = 0;
        var boundary = new RecordingEffectAuthorityBoundary(
            EffectBoundaryBehavior.ReturnBeforeCommitCompletes,
            afterCommitStarted: cancellationToken => transportEntered.Task.WaitAsync(cancellationToken));
        var executor = CreateExecutor(
            workspace,
            async (_, _, cancellationToken) =>
            {
                transportEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                transportWrites++;
                return Response();
            },
            effectAuthorityBoundary: boundary);

        var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() =>
            executor.ExecuteAsync(CreateRequest(), providerRequestStarted: () => providerStarts++));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(boundary.AwaitPendingCommitAsync);

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable, exception.ExecutionStatus);
        Assert.Null(exception.Decision);
        Assert.Equal(1, boundary.CommitInvocations);
        Assert.Equal(0, transportWrites);
        Assert.Equal(1, providerStarts);
    }

    [Fact]
    public async Task ExecuteAsync_callback_captured_until_after_boundary_return_cannot_cross_provider_transport()
    {
        using var workspace = new TestWorkspace();
        var transportWrites = 0;
        var providerStarts = 0;
        var boundary = new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.CaptureCommit);
        var executor = CreateExecutor(
            workspace,
            (_, _, _) =>
            {
                transportWrites++;
                return Task.FromResult(Response());
            },
            effectAuthorityBoundary: boundary);

        var exception = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() =>
            executor.ExecuteAsync(CreateRequest(), providerRequestStarted: () => providerStarts++));
        var callbackException = await Assert.ThrowsAsync<InvalidOperationException>(boundary.InvokeCapturedCommitAsync);

        Assert.Contains("only while its authority boundary is open", callbackException.Message, StringComparison.Ordinal);
        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable, exception.ExecutionStatus);
        Assert.Equal(1, boundary.CommitInvocations);
        Assert.Equal(0, transportWrites);
        Assert.Equal(0, providerStarts);
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
        Assert.Equal(7, authorityEvents.Length);
        Assert.Equal(3, authorityEvents.Count(item => Metadata(item, "authority_phase") == "pre_actuation_revalidation" && item.Outcome == AuditSchema.Outcomes.Allowed));
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
    public async Task ExecuteAsync_revalidates_authority_after_approval_and_denies_revocation_before_actuation()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(paths.PermissionsPath, "{}");
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "must remain unread");
        var authorityProvider = new TestAuthorityProvider();
        var approvalPrompt = new RecordingApprovalPrompt(approved: true, beforeDecision: () => authorityProvider.Revoke("role-2"));
        var evidenceSink = new RecordingEvidenceSink();
        ToolResult? observed = null;
        var executor = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            observed = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken);
            return Response();
        }, approvalPrompt: approvalPrompt, evidenceSink: evidenceSink, authorityProvider: authorityProvider);

        var result = await executor.ExecuteAsync(CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read]));

        Assert.Equal(3, authorityProvider.ResolveCount);
        Assert.Equal(1, result.ToolRequestsConsumed);
        var toolResult = Assert.IsType<ToolResult>(observed);
        Assert.Equal(ToolExecutionOutcome.Denied, toolResult.Outcome);
        Assert.DoesNotContain("must remain unread", toolResult.OutputText, StringComparison.Ordinal);
        Assert.Equal(ToolAuthorityDecision.Denied, toolResult.Governance?.AuthorityDecision);
        Assert.Equal(ToolApprovalDecision.Approved, toolResult.Governance?.ApprovalDecision);
        Assert.Single(approvalPrompt.Requests);
        var refreshedEvidence = evidenceSink.Evidence.Where(item => item.Phase is CustomLoopToolEvidencePhase.GovernanceDecided or CustomLoopToolEvidencePhase.OutcomeObserved).ToArray();
        Assert.NotEmpty(refreshedEvidence);
        Assert.All(refreshedEvidence, item =>
        {
            Assert.Equal("role-2", item.Authority.RoleId);
            Assert.Empty(item.Authority.CurrentRoleCeiling);
            Assert.Empty(item.Authority.EffectiveAssignments);
            Assert.False(item.Authority.IsValid);
        });
        Assert.Contains(refreshedEvidence, item => item is { Phase: CustomLoopToolEvidencePhase.OutcomeObserved, Governance.AuthorityDecision: ToolAuthorityDecision.Denied, Governance.ApprovalDecision: ToolApprovalDecision.Approved });

        var events = await new AuditLog(paths).ReadTailAsync(100);
        var revalidation = Assert.Single(events, item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate && Metadata(item, "authority_phase") == "pre_actuation_revalidation");
        Assert.Equal(AuditSchema.Outcomes.Denied, revalidation.Outcome);
        Assert.Equal("role-1", Metadata(revalidation, "role_id"));
        Assert.Equal("role-2", Metadata(revalidation, "current_role_id"));
        Assert.Equal(string.Empty, Metadata(revalidation, "current_role_commands"));
        Assert.Equal("false", Metadata(revalidation, "authority_valid")?.ToLowerInvariant());
        Assert.DoesNotContain(events, item => item.Action == AuditSchema.Actions.ToolExecutionIntent);
        var deniedExecution = Assert.Single(events, item => item.Action == AuditSchema.Actions.ToolExecute);
        Assert.Equal(AuditSchema.Outcomes.Denied, deniedExecution.Outcome);
        Assert.Equal("role-1", Metadata(deniedExecution, "role_id"));
        Assert.Equal("role-2", Metadata(deniedExecution, "current_role_id"));
        Assert.Equal("true", Metadata(deniedExecution, "approved_by_human")?.ToLowerInvariant());
    }

    [Fact]
    public async Task ExecuteAsync_denies_and_audits_the_sixth_tool_request_in_an_attempt()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "bounded");
        var outcomes = new List<ToolExecutionOutcome>();
        ToolResult? denied = null;
        var executor = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            for (var index = 0; index < 5; index++)
            {
                outcomes.Add((await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken)).Outcome);
            }

            Assert.Empty(broker.AvailableCommands);
            denied = await broker.ExecuteAsync(new ToolRequest(ToolCommand.Read, Path.Combine("system", "note.txt")), cancellationToken);
            outcomes.Add(denied.Outcome);

            return Response();
        });

        var result = await executor.ExecuteAsync(CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read]));

        Assert.Equal(6, result.ToolRequestsConsumed);
        Assert.Equal(5, outcomes.Count(item => item == ToolExecutionOutcome.Succeeded));
        Assert.Equal(ToolExecutionOutcome.Denied, outcomes[^1]);
        Assert.Equal(ToolResultRetentionStatus.Retained, Assert.IsType<ToolResult>(denied).Retention?.Status);
        Assert.True(File.Exists(workspace.File(denied.Retention!.ManifestPath!.Replace('/', Path.DirectorySeparatorChar))));
        var events = await new AuditLog(paths).ReadTailAsync(100);
        var limitEvent = Assert.Single(events, item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate && Metadata(item, "limit_scope") == "attempt");
        Assert.Equal(AuditSchema.Outcomes.Denied, limitEvent.Outcome);
        Assert.Equal("6", Metadata(limitEvent, "tool_request_ordinal"));
        Assert.Equal("5", Metadata(limitEvent, "limit"));
        AssertCorrelation(limitEvent);
        Assert.Equal(6, events.Count(item => item.Action == AuditSchema.Actions.ToolResponseRetain && item.Outcome == AuditSchema.Outcomes.Succeeded));
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
                await broker.ExecuteAsync(new ToolRequest(
                    ToolCommand.Read,
                    Path.Combine("system", "note.txt"),
                    CorrelationId: $"visible-request-{index + 1}"), cancellationToken);
            }

            await broker.ExecuteAsync(new ToolRequest(
                ToolCommand.Read,
                Path.Combine("system", "repeated.txt"),
                CorrelationId: "exact-repeated-request"), cancellationToken);
            return Response();
        }, evidenceSink: evidenceSink);

        await Assert.ThrowsAsync<CustomLoopToolEvidenceIntegrityException>(() => executor.ExecuteAsync(CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read])));

        Assert.DoesNotContain(evidenceSink.Evidence, item => item is { RequestOrdinal: 6, Phase: CustomLoopToolEvidencePhase.IntegrityFailed });
        var integrity = Assert.Single(evidenceSink.Evidence, item => item.Phase == CustomLoopToolEvidencePhase.IntegrityFailed);
        Assert.Equal(7, integrity.RequestOrdinal);
        Assert.Equal("exact-repeated-request", integrity.RequestCorrelationId);
        Assert.Equal(Path.Combine("system", "repeated.txt"), integrity.TargetPath);
        Assert.Null(integrity.BrokerRequestId);
        Assert.Null(integrity.Governance);
        Assert.Null(integrity.Outcome);
        Assert.Equal(CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes, integrity.ReservedUtf8Bytes);
    }

    [Fact]
    public async Task ExecuteAsync_completes_repeat_integrity_audit_after_provider_cancellation()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "bounded");
        using var providerCancellation = new CancellationTokenSource();
        var evidenceSink = new CancelOnIntegrityEvidenceSink(providerCancellation);
        var executor = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            for (var ordinal = 1; ordinal <= CustomLoopLimits.MaxGovernedToolRequestsPerAttempt; ordinal++)
            {
                await broker.ExecuteAsync(new ToolRequest(
                    ToolCommand.Read,
                    Path.Combine("system", "note.txt"),
                    CorrelationId: $"cancel-audit-{ordinal}"), cancellationToken);
            }

            await broker.ExecuteAsync(new ToolRequest(
                ToolCommand.Read,
                Path.Combine("system", "note.txt"),
                CorrelationId: "cancel-audit-visible-denial"), cancellationToken);
            await broker.ExecuteAsync(new ToolRequest(
                ToolCommand.Read,
                Path.Combine("system", "repeated.txt"),
                CorrelationId: "cancel-audit-repeat"), cancellationToken);
            return Response();
        }, evidenceSink: evidenceSink);

        await Assert.ThrowsAsync<CustomLoopToolEvidenceIntegrityException>(() => executor.ExecuteAsync(
            CreateRequest(allowTools: true, assignments: [CustomLoopToolAssignment.Read]),
            providerCancellation.Token));

        Assert.True(providerCancellation.IsCancellationRequested);
        var audit = await new AuditLog(paths).ReadTailAsync(200);
        var failed = Assert.Single(audit, item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate
            && item.Outcome == AuditSchema.Outcomes.Failed
            && Metadata(item, "tool_request_ordinal") == "7");
        Assert.Equal("attempt", Metadata(failed, "limit_scope"));
    }

    [Fact]
    public async Task ExecuteAsync_persists_the_exact_seventh_attempt_request_for_public_inspection_without_actuation()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "bounded");
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "repeated.txt"), "must not be read");
        var store = new CustomLoopRunStore(paths);
        var admitted = await CreateAdmittedRunAsync(store);
        var evidenceSink = new CustomLoopRunToolEvidenceSink(store);
        var executor = new CanonicalProofAttemptExecutor(CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            for (var ordinal = 1; ordinal <= CustomLoopLimits.MaxGovernedToolRequestsPerAttempt; ordinal++)
            {
                var result = await broker.ExecuteAsync(new ToolRequest(
                    ToolCommand.Read,
                    Path.Combine("system", "note.txt"),
                    CorrelationId: $"attempt-visible-{ordinal}"), cancellationToken);
                Assert.Equal(ToolExecutionOutcome.Succeeded, result.Outcome);
            }

            var denied = await broker.ExecuteAsync(new ToolRequest(
                ToolCommand.Read,
                Path.Combine("system", "note.txt"),
                CorrelationId: "provider-reused-attempt-correlation"), cancellationToken);
            Assert.Equal(ToolExecutionOutcome.Denied, denied.Outcome);
            await broker.ExecuteAsync(new ToolRequest(
                ToolCommand.Read,
                Path.Combine("system", "repeated.txt"),
                CorrelationId: "provider-reused-attempt-correlation"), cancellationToken);
            return Response();
        }, evidenceSink: evidenceSink));
        var runner = new CustomLoopOrderedRunner(store, new CustomLoopContextResolver(), executor, new PublishedConversation(), new AuditLog(paths), new TestAuthorityProvider(), capabilityAdmissionService: new TestCapabilityAdmissionService());

        var execution = await runner.RunAsync(new CustomLoopOrderedRunRequest(admitted.Id, "web"));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, execution.Status);
        var reloaded = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        Assert.Equal(CustomLoopLimits.MaxModelVisibleGovernedToolRequestsPerAttempt, reloaded.Events.Count(item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.RequestReserved));
        var integrityEvent = Assert.Single(reloaded.Events, item => item.ToolEvidence?.Phase == CustomLoopToolEvidencePhase.IntegrityFailed);
        var integrity = integrityEvent.ToolEvidence!;
        Assert.Equal(CustomLoopLimits.MaxRecordedGovernedToolRequestsPerAttempt, integrity.RequestOrdinal);
        Assert.Equal("provider-reused-attempt-correlation", integrity.RequestCorrelationId);
        Assert.Equal(Path.Combine("system", "repeated.txt"), integrity.TargetPath);
        Assert.Null(integrity.BrokerRequestId);
        Assert.Null(integrity.Governance);
        Assert.Null(integrity.Outcome);
        Assert.Equal(CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes, integrity.ReservedUtf8Bytes);
        var projectedRun = Assert.IsType<LoopRunSnapshot>(await new LoopRunInspectionFacade(workspace.RootPath).GetAsync(admitted.Id));
        AssertToolEvidenceProjection(integrityEvent, Assert.Single(projectedRun.Events, item => item.Sequence == integrityEvent.Sequence));
        var audit = await new AuditLog(paths).ReadTailAsync(200);
        Assert.Equal(CustomLoopLimits.MaxGovernedToolRequestsPerAttempt, audit.Count(item => item.Action == AuditSchema.Actions.ToolExecute));
        var limitAudits = audit.Where(item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate && Metadata(item, "limit_scope") == "attempt").ToArray();
        Assert.Equal(2, limitAudits.Length);
        Assert.Single(limitAudits, item => item.Outcome == AuditSchema.Outcomes.Denied && Metadata(item, "tool_request_ordinal") == "6");
        Assert.Single(limitAudits, item => item.Outcome == AuditSchema.Outcomes.Failed && Metadata(item, "tool_request_ordinal") == "7");
    }

    [Fact]
    public async Task ExecuteAsync_persists_the_thirty_first_denial_and_repeat_integrity_without_actuation()
    {
        using var workspace = new TestWorkspace();
        var paths = await InitializeWorkspaceAsync(workspace);
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "note.txt"), "bounded");
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceSystemPath, "repeated.txt"), "must not be read");
        var store = new CustomLoopRunStore(paths);
        var admitted = await CreateAdmittedRunAsync(store);
        var evidenceSink = new CustomLoopRunToolEvidenceSink(store);
        var inner = CreateExecutor(workspace, async (broker, _, cancellationToken) =>
        {
            Assert.NotNull(broker);
            var denied = await broker.ExecuteAsync(new ToolRequest(
                ToolCommand.Read,
                Path.Combine("system", "note.txt"),
                CorrelationId: "provider-reused-correlation"), cancellationToken);
            Assert.Equal(ToolExecutionOutcome.Denied, denied.Outcome);
            await broker.ExecuteAsync(new ToolRequest(
                ToolCommand.Read,
                Path.Combine("system", "repeated.txt"),
                CorrelationId: "provider-reused-correlation"), cancellationToken);
            return Response();
        }, evidenceSink: evidenceSink);
        var executor = new CanonicalProofAttemptExecutor(new RunLimitAttemptExecutor(inner));
        var runner = new CustomLoopOrderedRunner(store, new CustomLoopContextResolver(), executor, new PublishedConversation(), new AuditLog(paths), new TestAuthorityProvider(), capabilityAdmissionService: new TestCapabilityAdmissionService());

        var execution = await runner.RunAsync(new CustomLoopOrderedRunRequest(admitted.Id, "web"));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, execution.Status);
        var reloaded = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var toolEvents = reloaded.Events.Where(item => item.ToolEvidence is not null).ToArray();
        Assert.True(toolEvents.Length == 5, $"Phases: {string.Join(',', toolEvents.Select(item => $"{item.ToolEvidence!.Phase}:{item.ToolEvidence.ReturnedToModel}"))}. Failure: {reloaded.FailureDetail}");
        Assert.Single(toolEvents, item => item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.RequestReserved);
        var integrity = Assert.Single(toolEvents, item => item.ToolEvidence!.Phase == CustomLoopToolEvidencePhase.IntegrityFailed).ToolEvidence!;
        Assert.Single(toolEvents.Select(item => item.ToolEvidence!.RequestCorrelationId).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, integrity.RequestOrdinal);
        Assert.Equal("provider-reused-correlation", integrity.RequestCorrelationId);
        Assert.Equal(Path.Combine("system", "repeated.txt"), integrity.TargetPath);
        Assert.Equal(2, toolEvents.Select(item => (item.ToolEvidence!.RequestOrdinal, item.ToolEvidence.RequestCorrelationId)).Distinct().Count());
        Assert.Null(integrity.BrokerRequestId);
        Assert.Null(integrity.Governance);
        Assert.Null(integrity.Outcome);
        Assert.Equal(CustomLoopLimits.MaxRepeatedGovernedToolRequestIntegrityEvidenceUtf8Bytes, integrity.ReservedUtf8Bytes);
        var sourceEvent = Assert.Single(toolEvents, item => item.ToolEvidence!.Governance is not null && item.ToolEvidence.Phase == CustomLoopToolEvidencePhase.GovernanceDecided);
        var projectedRun = Assert.IsType<LoopRunSnapshot>(await new LoopRunInspectionFacade(workspace.RootPath).GetAsync(admitted.Id));
        AssertToolEvidenceProjection(sourceEvent, Assert.Single(projectedRun.Events, item => item.Sequence == sourceEvent.Sequence));
        var audit = await new AuditLog(paths).ReadTailAsync(200);
        Assert.DoesNotContain(audit, item => item.Action == AuditSchema.Actions.ToolExecute);
        var limitAudits = audit.Where(item => item.Action == AuditSchema.Actions.ToolLoopAuthorityEvaluate && Metadata(item, "limit_scope") == "run").ToArray();
        Assert.Equal(2, limitAudits.Length);
        Assert.Single(limitAudits, item => item.Outcome == AuditSchema.Outcomes.Denied && Metadata(item, "tool_request_ordinal") == "1");
        Assert.Single(limitAudits, item => item.Outcome == AuditSchema.Outcomes.Failed && Metadata(item, "tool_request_ordinal") == "2");
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
    public async Task ExecuteAsync_marks_provider_started_inside_authority_commit_and_disposes_when_transport_write_fails()
    {
        using var workspace = new TestWorkspace();
        var boundary = new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.Direct);
        var transportWriteAttempts = 0;
        AsyncFakeInferenceClient? client = null;
        var executor = CreateExecutor(
            workspace,
            (_, _, _) =>
            {
                transportWriteAttempts++;
                throw new IOException("provider exploded");
            },
            (options, broker, behavior) => client = new AsyncFakeInferenceClient(broker, behavior),
            effectAuthorityBoundary: boundary);

        var providerRequestStarted = false;
        var exception = await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(CreateRequest(), providerRequestStarted: () => providerRequestStarted = true));

        Assert.Equal("provider exploded", exception.Message);
        Assert.True(providerRequestStarted);
        Assert.Equal(1, boundary.CommitInvocations);
        Assert.Equal(1, transportWriteAttempts);
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
        ICustomLoopToolAuthorityProvider? authorityProvider = null,
        IGovernedLoopEffectAuthorityBoundary? effectAuthorityBoundary = null)
    {
        var effectivePrompt = approvalPrompt ?? new RecordingApprovalPrompt();
        return new CustomLoopInferenceAttemptExecutor(
            CreateOptions(workspace),
            (IToolApprovalPrompt)effectivePrompt,
            authorityProvider ?? new TestAuthorityProvider(),
            evidenceSink ?? new NullEvidenceSink(),
            new TestCapabilityAdmissionService(),
            (options, broker) => factory?.Invoke(options, broker, behavior) ?? new AsyncFakeInferenceClient(broker, behavior),
            capabilityAuthorityTransaction: null,
            effectAuthorityBoundary: effectAuthorityBoundary ?? new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.Direct));
    }

    private static CustomLoopInferenceAttemptExecutor CreateInjectedExecutor(
        LlmInferenceClientOptions options,
        RecordingApprovalPrompt approvalPrompt,
        CustomLoopInferenceClientFactory clientFactory,
        IGovernedLoopEffectAuthorityBoundary? effectAuthorityBoundary = null)
    {
        return new CustomLoopInferenceAttemptExecutor(
            options,
            (IToolApprovalPrompt)approvalPrompt,
            new TestAuthorityProvider(),
            new NullEvidenceSink(),
            new TestCapabilityAdmissionService(),
            clientFactory,
            capabilityAuthorityTransaction: null,
            effectAuthorityBoundary: effectAuthorityBoundary ?? new RecordingEffectAuthorityBoundary(EffectBoundaryBehavior.Direct));
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
        IReadOnlyList<CustomLoopToolAssignment>? assignments = null,
        int attempt = 1,
        string? attemptCorrelationId = null)
    {
        return CanonicalInferenceAuthorityTestData.Request(
            allowTools,
            assignments,
            attempt,
            attemptCorrelationId);
    }

    private static async Task<CustomLoopRunRecord> CreateAdmittedRunAsync(CustomLoopRunStore store)
    {
        var now = DateTimeOffset.UtcNow.ToUniversalTime();
        var definition = CustomLoopDefinition.CreateSeed("loop-real-limit", "role-1", "step-one", "create-real-limit", now) with
        {
            ToolAssignments = [CustomLoopToolAssignment.Read]
        };
        definition = CustomLoopDefinitionContentHash.Apply(definition with
        {
            ContentHash = string.Empty,
            CapabilityRequirements = LoopCapabilityRequirements.CreateCustomLoopManifest(definition.Id, definition.ToolAssignments)
        });
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
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, now)
        };
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

    private static string? Metadata(EmbodySense.Core.Common.Governance.Audit.AuditEvent auditEvent, string key)
    {
        return auditEvent.Metadata.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static void AssertCorrelation(EmbodySense.Core.Common.Governance.Audit.AuditEvent auditEvent)
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
        if (source.Governance is null)
        {
            Assert.Null(projected.Governance);
            return;
        }

        var sourceGovernance = source.Governance;
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

    private sealed class RecordingApprovalPrompt(bool approved = false, Action? beforeDecision = null) : IAgentToolApprovalPrompt, IToolApprovalPrompt
    {
        public List<AgentToolApprovalRequest> Requests { get; } = [];

        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            beforeDecision?.Invoke();
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
        public int ResolveCount { get; private set; }
        private bool _revoked;
        private string? _currentRoleId;

        public void Revoke(string? currentRoleId = null)
        {
            _revoked = true;
            _currentRoleId = currentRoleId;
        }

        public Task<CustomLoopToolAuthoritySnapshot> ResolveAsync(string roleId, IReadOnlyList<CustomLoopToolAssignment> admittedMaximum, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            var admitted = admittedMaximum.ToArray();
            var catalog = new[] { CustomLoopToolAssignment.List, CustomLoopToolAssignment.Read, CustomLoopToolAssignment.Search };
            var current = _revoked ? [] : admitted;
            var resolvedRoleId = _currentRoleId ?? roleId;
            var valid = _currentRoleId is null;
            return Task.FromResult(new CustomLoopToolAuthoritySnapshot(
                resolvedRoleId,
                admitted,
                current,
                catalog,
                current,
                CustomLoopTraceContentHash.Compute(resolvedRoleId + "\n" + string.Join('\n', current)),
                CustomLoopTraceContentHash.Compute(string.Join('\n', catalog)),
                DateTimeOffset.UtcNow,
                valid,
                _currentRoleId is not null ? "The admitted directory role changed." : _revoked ? "Test authority was revoked." : "Test authority preserves the immutable admitted maximum."));
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

    private sealed class CancelOnIntegrityEvidenceSink(CancellationTokenSource cancellation) : ICustomLoopToolEvidenceSink
    {
        public Task RecordAsync(string runId, int iteration, string stepId, int attempt, CustomLoopToolTraceEvidence evidence, CancellationToken cancellationToken = default)
        {
            if (evidence.Phase == CustomLoopToolEvidencePhase.IntegrityFailed)
            {
                cancellation.Cancel();
            }

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

    private sealed class CanonicalProofAttemptExecutor(ICustomLoopInferenceAttemptExecutor inner) : ICustomLoopInferenceAttemptExecutor
    {
        public Task<CustomLoopInferenceAttemptResult> ExecuteAsync(
            CustomLoopInferenceAttemptRequest request,
            CancellationToken cancellationToken = default,
            Action? providerRequestStarted = null)
        {
            var canonical = CanonicalInferenceAuthorityTestData.Request(
                request.AllowTools,
                request.AdmittedToolAssignments,
                request.Attempt,
                request.AttemptCorrelationId,
                request.RunId,
                request.LoopId,
                request.RoleId);
            return inner.ExecuteAsync(
                request with
                {
                    CapabilityAdmission = canonical.CapabilityAdmission,
                    AdmissionReceipt = canonical.AdmissionReceipt,
                    ExecutionBinding = canonical.ExecutionBinding,
                    GraphArtifact = canonical.GraphArtifact,
                },
                cancellationToken,
                providerRequestStarted);
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

    public enum EffectBoundaryBehavior
    {
        Direct,
        Deny,
        Pause,
        Ambiguous,
        DoubleCommit,
        NullResult,
        MalformedResult,
        MismatchedPause,
        ReturnBeforeCommitCompletes,
        CaptureCommit,
    }

    private sealed class RecordingEffectAuthorityBoundary(
        EffectBoundaryBehavior behavior,
        Func<CancellationToken, Task>? beforeDecision = null,
        Func<CancellationToken, Task>? afterCommitStarted = null) : IGovernedLoopEffectAuthorityBoundary
    {
        private readonly object _gate = new();
        private readonly List<GovernedLoopEffectAuthorityRequest> _requests = [];
        private int _commitInvocations;
        private Func<CancellationToken, Task>? _capturedCommit;
        private Task? _pendingCommit;

        public IReadOnlyList<GovernedLoopEffectAuthorityRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public int CommitInvocations => Volatile.Read(ref _commitInvocations);

        public Task InvokeCapturedCommitAsync()
            => (_capturedCommit ?? throw new InvalidOperationException("No provider commit callback was captured."))(CancellationToken.None);

        public Task AwaitPendingCommitAsync()
            => _pendingCommit ?? throw new InvalidOperationException("No provider commit callback is pending.");

        public async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(
            GovernedLoopEffectAuthorityRequest request,
            Func<CancellationToken, Task<TResult>> commit,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _requests.Add(request);
            }

            if (beforeDecision is not null)
            {
                await beforeDecision(cancellationToken);
            }

            switch (behavior)
            {
                case EffectBoundaryBehavior.Direct:
                    {
                        Interlocked.Increment(ref _commitInvocations);
                        var result = await commit(cancellationToken);
                        return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
                            GovernedLoopEffectAuthorityExecutionStatus.Decided,
                            CanonicalInferenceAuthorityTestData.Decision(
                                request,
                                GovernedLoopEffectAuthorityDisposition.Direct,
                                GovernedLoopEffectAuthorityReason.ActiveExact,
                                includeCurrentProof: true),
                            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                            CommitInvoked: true,
                            result,
                            "The exact direct decision committed provider transport.");
                    }
                case EffectBoundaryBehavior.DoubleCommit:
                    {
                        Interlocked.Increment(ref _commitInvocations);
                        _ = await commit(cancellationToken);
                        Interlocked.Increment(ref _commitInvocations);
                        _ = await commit(cancellationToken);
                        throw new Xunit.Sdk.XunitException("The hostile second commit unexpectedly crossed the executor boundary.");
                    }
                case EffectBoundaryBehavior.Deny:
                    return Stopped<TResult>(
                        request,
                        GovernedLoopEffectAuthorityExecutionStatus.Decided,
                        GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                        GovernedLoopEffectAuthorityDisposition.Deny,
                        GovernedLoopEffectAuthorityReason.InvalidRequest,
                        includeCurrentProof: false,
                        "The exact provider effect was denied.");
                case EffectBoundaryBehavior.Pause:
                    return Stopped<TResult>(
                        request,
                        GovernedLoopEffectAuthorityExecutionStatus.Decided,
                        GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                        GovernedLoopEffectAuthorityDisposition.Pause,
                        GovernedLoopEffectAuthorityReason.GrantUnavailable,
                        includeCurrentProof: false,
                        "The exact provider effect was paused.");
                case EffectBoundaryBehavior.Ambiguous:
                    return Stopped<TResult>(
                        request,
                        GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected,
                        GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous,
                        GovernedLoopEffectAuthorityDisposition.Pause,
                        GovernedLoopEffectAuthorityReason.EvidenceAmbiguous,
                        includeCurrentProof: true,
                        "Provider authority evidence was ambiguous.");
                case EffectBoundaryBehavior.NullResult:
                    return null!;
                case EffectBoundaryBehavior.MalformedResult:
                    return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
                        (GovernedLoopEffectAuthorityExecutionStatus)999,
                        Decision: null,
                        (GovernedLoopEffectAuthorityEvidenceStoreStatus)999,
                        CommitInvoked: false,
                        Result: default,
                        "A hostile boundary returned unsupported protocol values.");
                case EffectBoundaryBehavior.MismatchedPause:
                    return Stopped<TResult>(
                        request with { CorrelationId = "substituted-correlation" },
                        GovernedLoopEffectAuthorityExecutionStatus.Decided,
                        GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                        GovernedLoopEffectAuthorityDisposition.Pause,
                        GovernedLoopEffectAuthorityReason.GrantUnavailable,
                        includeCurrentProof: false,
                        "A canonical decision belongs to a different exact request.");
                case EffectBoundaryBehavior.ReturnBeforeCommitCompletes:
                    Interlocked.Increment(ref _commitInvocations);
                    _pendingCommit = commit(cancellationToken);
                    if (afterCommitStarted is not null)
                    {
                        await afterCommitStarted(cancellationToken);
                    }

                    return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
                        GovernedLoopEffectAuthorityExecutionStatus.Decided,
                        CanonicalInferenceAuthorityTestData.Decision(
                            request,
                            GovernedLoopEffectAuthorityDisposition.Direct,
                            GovernedLoopEffectAuthorityReason.ActiveExact,
                            includeCurrentProof: true),
                        GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                        CommitInvoked: true,
                        Result: default,
                        "The hostile boundary returned before its callback completed.");
                case EffectBoundaryBehavior.CaptureCommit:
                    _capturedCommit = async token =>
                    {
                        Interlocked.Increment(ref _commitInvocations);
                        _ = await commit(token);
                    };
                    return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
                        GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable,
                        Decision: null,
                        GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
                        CommitInvoked: false,
                        Result: default,
                        "The boundary returned before invoking its captured callback.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "Unsupported test boundary behavior.");
            }
        }

        private static GovernedLoopEffectAuthorityExecutionResult<TResult> Stopped<TResult>(
            GovernedLoopEffectAuthorityRequest request,
            GovernedLoopEffectAuthorityExecutionStatus status,
            GovernedLoopEffectAuthorityEvidenceStoreStatus evidenceStatus,
            GovernedLoopEffectAuthorityDisposition disposition,
            GovernedLoopEffectAuthorityReason reason,
            bool includeCurrentProof,
            string detail)
            => new(
                status,
                CanonicalInferenceAuthorityTestData.Decision(request, disposition, reason, includeCurrentProof),
                evidenceStatus,
                CommitInvoked: false,
                Result: default,
                detail);
    }

    private sealed class ThrowingDisposeInferenceClient : ILlmInferenceClient, IAsyncDisposable
    {
        public bool DisposeAttempted { get; private set; }

        public Task<LlmInferenceResponse> GenerateAsync(LlmInferenceRequest request, Func<string, CancellationToken, Task>? responseChunkHandler = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Response("completed"));
        }

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler,
            CancellationToken cancellationToken,
            InferenceProviderTransportCommitBoundary providerTransportCommitBoundary)
        {
            LlmInferenceResponse? response = null;
            await providerTransportCommitBoundary(
                token =>
                {
                    response = Response("completed");
                    return Task.CompletedTask;
                },
                cancellationToken);
            return Assert.IsType<LlmInferenceResponse>(response);
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

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler,
            CancellationToken cancellationToken,
            InferenceProviderTransportCommitBoundary providerTransportCommitBoundary)
        {
            LlmInferenceResponse? response = null;
            await providerTransportCommitBoundary(
                async token => response = await behavior(broker, request, token),
                cancellationToken);
            return Assert.IsType<LlmInferenceResponse>(response);
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

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler,
            CancellationToken cancellationToken,
            InferenceProviderTransportCommitBoundary providerTransportCommitBoundary)
        {
            LlmInferenceResponse? response = null;
            await providerTransportCommitBoundary(
                token =>
                {
                    response = Response();
                    return Task.CompletedTask;
                },
                cancellationToken);
            return Assert.IsType<LlmInferenceResponse>(response);
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

        public Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler,
            CancellationToken cancellationToken,
            InferenceProviderTransportCommitBoundary providerTransportCommitBoundary)
        {
            throw new Xunit.Sdk.XunitException("A non-disposable provider transport must be rejected before its authority boundary is entered.");
        }
    }
}
