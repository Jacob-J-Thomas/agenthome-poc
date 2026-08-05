using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Application.Context;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Context;
using EmbodySense.Core.Common.Context.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Memory.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class DefaultConversationLoopRunnerTests
{
    [Fact]
    public async Task RunTurnAsync_builds_request_persists_messages_and_emits_visible_context()
    {
        var client = new RecordingInferenceClient("completed response");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);
        var chunks = new List<string>();
        var diagnostics = new List<RuntimeDiagnosticMessage>();

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest(
            "hello",
            (chunk, _) =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            },
            (diagnostic, _) =>
            {
                diagnostics.Add(diagnostic);
                return Task.CompletedTask;
            }));

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, result.Status);
        Assert.True(result.UserMessageAccepted);
        Assert.NotNull(result.RunIdentity);
        Assert.Equal("default-conversation", result.RunIdentity.LoopId);
        Assert.Equal("completed response", result.AssistantOutput);
        Assert.Equal(["completed response"], chunks);
        var inferenceRequest = Assert.Single(client.Requests);
        Assert.NotNull(inferenceRequest.Correlation);
        Assert.StartsWith("turn-", inferenceRequest.Correlation.ProviderAttemptId, StringComparison.Ordinal);
        Assert.EndsWith(":provider-attempt:1", inferenceRequest.Correlation.ProviderAttemptId, StringComparison.Ordinal);
        Assert.EndsWith(":provider-correlation:1", inferenceRequest.Correlation.ProviderCorrelationId, StringComparison.Ordinal);
        Assert.Equal(result.RunIdentity.RunId, inferenceRequest.Correlation.ToolAuditCorrelation!.RunId);
        Assert.Equal(result.RunIdentity.LoopId, inferenceRequest.Correlation.ToolAuditCorrelation.LoopId);
        Assert.Equal(result.RunIdentity.RoleId, inferenceRequest.Correlation.ToolAuditCorrelation.RoleId);
        Assert.Equal(DefaultConversationLoopGraphIds.DispatchInference, inferenceRequest.Correlation.ToolAuditCorrelation.StepId);
        Assert.Equal(inferenceRequest.Correlation.ProviderCorrelationId, inferenceRequest.Correlation.ToolAuditCorrelation.AttemptCorrelationId);
        Assert.Collection(inferenceRequest.Messages, message => Assert.Equal("hello", message.Content));
        var instructionContext = Assert.IsType<LlmInferenceInstructionContext>(inferenceRequest.InstructionContext);
        Assert.True(EmbodySenseDeveloperInstructions.Matches(instructionContext.Governance, Enum.GetValues<ToolCommand>()));
        var startupInstruction = Assert.Single(instructionContext.TrustedInstructions);
        Assert.Equal("startup-context-1", startupInstruction.SourceId);
        Assert.Equal("startup context", startupInstruction.Content);
        Assert.False(instructionContext.PreserveExactLogicalContext);
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("startup context", message.Content),
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            memory.Messages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(RuntimeDiagnosticKind.VerboseContext, diagnostic.Kind);
        Assert.Equal("Visible inference context", diagnostic.Title);
        Assert.Contains("loop_id: default-conversation", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("role_id: default-assistant", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("surface: web", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("edit_mode: SystemLocked", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("graph_entry_node: accept-user-message", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("graph_nodes:", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("capability_ids:", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("workspace_commands_allowed_by_loop:", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("compaction:", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("provider-adapter", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains(".agent/MEMORY.md is not included", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("source=startup-context", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("source=current-turn-input", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("startup context", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("hello", diagnostic.Content, StringComparison.Ordinal);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Completed, run.Status));
        Assert.All(runs.Saved, run =>
        {
            Assert.Equal("default-conversation", run.LoopId);
            Assert.Equal("default-assistant", run.RoleId);
            Assert.Equal("web", run.Surface);
            Assert.Equal("SystemLocked", run.Metadata["loopEditMode"]);
            Assert.Equal(DefaultConversationLoopGraphIds.AcceptUserMessage, run.Metadata["graphEntryNodeId"]);
            Assert.Equal("5", run.Metadata["graphNodeCount"]);
        });
    }

    [Fact]
    public async Task RunTurnAsync_verbose_context_reports_memory_loaded_and_in_band_truncation()
    {
        var client = new RecordingInferenceClient("completed response");
        var contextStore = new StaticWorkspaceContextStore(new WorkspaceContextDocument(
            "memory",
            ".agent/MEMORY.md",
            ".agent/MEMORY.md",
            WorkspaceContextDocumentKind.ContextualState,
            "memory note" + Environment.NewLine + "[truncated]",
            23,
            null));
        var startupMessages = await new AgentContextProvider(contextStore).LoadAsync(new WorkspacePaths(Directory.GetCurrentDirectory()));
        var state = new ConversationRuntimeState(startupMessages);
        var runner = new DefaultConversationLoopRunner(client, state, loopDefinition: LoopDefinition.CreateDefaultConversation(), surface: RuntimeSurfaceId.Web);
        var diagnostics = new List<RuntimeDiagnosticMessage>();

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest(
            "hello",
            diagnosticHandler: (diagnostic, _) =>
            {
                diagnostics.Add(diagnostic);
                return Task.CompletedTask;
            }));

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, result.Status);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains(".agent/MEMORY.md is present in the active startup context", diagnostic.Content, StringComparison.Ordinal);
        Assert.Contains("in-band truncation", diagnostic.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurnAsync_parks_an_unobserved_provider_failure_without_completed_assistant_message()
    {
        var client = new RecordingInferenceClient("unused") { Failure = new InvalidOperationException("provider failed") };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.NeedsReview, result.Status);
        Assert.Contains("provider failed", result.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("Automatic redispatch is forbidden", result.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("transport was quarantined", result.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, client.QuarantineCount);
        Assert.True(result.UserMessageAccepted);
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("startup context", message.Content),
            message => Assert.Equal("hello", message.Content));
        var message = Assert.Single(memory.Messages);
        Assert.Equal("hello", message.Content);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.NeedsReview, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_persists_a_conclusive_terminal_provider_failure_without_quarantine_or_review()
    {
        var client = new RecordingInferenceClient("unused") { Failure = new LlmInferenceTerminalFailureException("provider rejected the turn", "provider-turn-1") };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var turns = new RecordingDefaultConversationTurnStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web, turns);
        const string RequestId = "terminal-provider-failure";

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));
        var turn = await turns.LoadAsync(DefaultConversationTurnProtocol.CreateTurnId(RequestId));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Equal("provider rejected the turn", result.FailureDetail);
        Assert.True(result.UserMessageAccepted);
        Assert.Equal(0, client.QuarantineCount);
        Assert.NotNull(turn);
        Assert.Equal(DefaultConversationProviderOutcome.ObservedFailure, turn.ProviderOutcome);
        Assert.Equal("provider-turn-1", turn.ProviderResponseId);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, turn.Checkpoint);
        Assert.Equal(LoopRunStatus.Failed, turn.Run.Status);
        Assert.Null(turn.AssistantMessage);
        Assert.Collection(memory.Messages, message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)));
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Failed, run.Status));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunTurnAsync_persists_successful_empty_provider_completion_as_observed_failure(string output)
    {
        const string RequestId = "empty-provider-success";
        var client = new RecordingInferenceClient(output) { ProviderResponseId = "provider-empty-1" };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var turns = new RecordingDefaultConversationTurnStore();
        var runner = new DefaultConversationLoopRunner(client, new ConversationRuntimeState(), memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web, turns);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));
        var turn = await turns.LoadAsync(DefaultConversationTurnProtocol.CreateTurnId(RequestId));
        var replay = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, replay.Status);
        Assert.Contains("no usable assistant output", result.FailureDetail, StringComparison.Ordinal);
        Assert.True(result.UserMessageAccepted);
        Assert.Equal(0, client.QuarantineCount);
        Assert.Single(client.Requests);
        Assert.NotNull(turn);
        Assert.Equal(DefaultConversationProviderOutcome.ObservedFailure, turn.ProviderOutcome);
        Assert.Equal("provider-empty-1", turn.ProviderResponseId);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, turn.Checkpoint);
        Assert.Equal(LoopRunStatus.Failed, turn.Run.Status);
        Assert.Null(turn.AssistantMessage);
        Assert.False(DefaultConversationTurnProtocol.CanAbandonReview(turn));
        Assert.Empty(await turns.ListNeedsReviewAsync());
        Assert.Collection(memory.Messages, message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)));
    }

    [Fact]
    public async Task RunTurnAsync_retains_an_observed_response_for_review_when_completion_audit_fails()
    {
        var response = new LlmInferenceResponse("observed answer", LlmInferenceSurface.OpenAiCodex, ProviderResponseId: "provider-turn-2");
        var client = new RecordingInferenceClient("unused")
        {
            Failure = new LlmInferenceObservedResponseException("completion audit failed after provider success", response, new IOException("audit unavailable"))
        };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var turns = new RecordingDefaultConversationTurnStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web, turns);
        const string RequestId = "observed-response-audit-failure";

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));
        var turn = await turns.LoadAsync(DefaultConversationTurnProtocol.CreateTurnId(RequestId));

        Assert.Equal(DefaultConversationLoopTurnStatus.NeedsReview, result.Status);
        Assert.Contains("completion audit failed", result.FailureDetail, StringComparison.Ordinal);
        Assert.True(result.UserMessageAccepted);
        Assert.Equal(0, client.QuarantineCount);
        Assert.NotNull(turn);
        Assert.Equal(DefaultConversationProviderOutcome.ObservedWithAuditFailure, turn.ProviderOutcome);
        Assert.Equal("provider-turn-2", turn.ProviderResponseId);
        Assert.Equal("observed answer", turn.AssistantMessage!.Content);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, turn.Checkpoint);
        Assert.Equal(LoopRunStatus.NeedsReview, turn.Run.Status);
        Assert.Collection(memory.Messages, message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)));
    }

    [Fact]
    public async Task RunTurnAsync_persists_an_empty_observed_audit_failure_as_conclusive_failure()
    {
        var response = new LlmInferenceResponse(string.Empty, LlmInferenceSurface.OpenAiCodex, ProviderResponseId: "provider-empty-audit-1");
        var client = new RecordingInferenceClient("unused")
        {
            Failure = new LlmInferenceObservedResponseException("completion audit failed after provider success", response, new IOException("audit unavailable"))
        };
        var turns = new RecordingDefaultConversationTurnStore();
        var runner = new DefaultConversationLoopRunner(client, new ConversationRuntimeState(), new RecordingConversationMemoryStore(), LoopDefinition.CreateDefaultConversation(), new RecordingLoopRunStore(), RuntimeSurfaceId.Web, turns);
        const string RequestId = "empty-observed-audit-failure";

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));
        var turn = await turns.LoadAsync(DefaultConversationTurnProtocol.CreateTurnId(RequestId));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Contains("no usable assistant output", result.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(0, client.QuarantineCount);
        Assert.NotNull(turn);
        Assert.Equal(DefaultConversationProviderOutcome.ObservedFailure, turn.ProviderOutcome);
        Assert.Equal("provider-empty-audit-1", turn.ProviderResponseId);
        Assert.Equal(LoopRunStatus.Failed, turn.Run.Status);
        Assert.Null(turn.AssistantMessage);
        Assert.False(DefaultConversationTurnProtocol.CanAbandonReview(turn));
    }

    [Fact]
    public async Task RunTurnAsync_replays_a_completed_caller_request_without_provider_redispatch_or_duplicate_publication()
    {
        var client = new RecordingInferenceClient("completed response");
        var memory = new RecordingConversationMemoryStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory);
        const string RequestId = "caller-request-1";

        var first = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));
        var replay = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, first.Status);
        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, replay.Status);
        Assert.Equal(first.RunIdentity, replay.RunIdentity);
        Assert.Equal("completed response", replay.AssistantOutput);
        Assert.Single(client.Requests);
        Assert.Collection(
            memory.Messages,
            message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)),
            message => Assert.Equal((LlmMessageRole.Assistant, "completed response"), (message.Role, message.Content)));
    }

    [Theory]
    [InlineData(DefaultConversationTurnBoundary.TurnAdmitted)]
    [InlineData(DefaultConversationTurnBoundary.RunStartCheckpointed)]
    public async Task RunTurnAsync_replays_terminal_preacceptance_failures_without_marking_the_user_message_accepted(DefaultConversationTurnBoundary failureBoundary)
    {
        var client = new RecordingInferenceClient("must not run");
        var memory = new RecordingConversationMemoryStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(
            client,
            state,
            memory,
            LoopDefinition.CreateDefaultConversation(),
            new RecordingLoopRunStore(),
            RuntimeSurfaceId.Web,
            failpoint: new GenericFailureFailpoint(failureBoundary));
        const string RequestId = "preacceptance-replay";

        var failed = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));
        var replayed = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, failed.Status);
        Assert.False(failed.UserMessageAccepted);
        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, replayed.Status);
        Assert.False(replayed.UserMessageAccepted);
        Assert.Empty(client.Requests);
        Assert.Empty(memory.Messages);
        Assert.Empty(state.Messages);
    }

    [Fact]
    public async Task RunTurnAsync_rejects_reuse_of_a_caller_request_for_a_different_payload()
    {
        var client = new RecordingInferenceClient("completed response");
        var runner = new DefaultConversationLoopRunner(client, new ConversationRuntimeState(), new RecordingConversationMemoryStore());
        const string RequestId = "caller-request-2";

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, (await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("first", requestId: RequestId))).Status);
        var conflict = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("different", requestId: RequestId));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, conflict.Status);
        Assert.Contains("already used for a different", conflict.FailureDetail, StringComparison.Ordinal);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task RunTurnAsync_rejects_a_pre_cancelled_turn_before_accepting_its_prompt()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var client = new RecordingInferenceClient("unused") { Failure = new OperationCanceledException(cancellation.Token) };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", cancellationToken: cancellation.Token));

        Assert.Equal(DefaultConversationLoopTurnStatus.Cancelled, result.Status);
        Assert.False(result.UserMessageAccepted);
        Assert.Empty(state.Messages);
        Assert.Empty(memory.Messages);
        Assert.Empty(runs.Saved);
    }

    [Fact]
    public async Task RunTurnAsync_fails_before_context_assembly_when_durable_conversation_synchronization_fails()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore { LoadCurrentException = new IOException("conversation unavailable") };
        var state = new ConversationRuntimeState([LlmMessage.System("startup")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Contains("conversation unavailable", result.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
        Assert.Equal(["startup"], state.Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task RunTurnAsync_cancels_before_context_assembly_when_durable_conversation_synchronization_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore
        {
            BeforeLoadCurrent = _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", cancellationToken: cancellation.Token));

        Assert.Equal(DefaultConversationLoopTurnStatus.Cancelled, result.Status);
        Assert.Empty(client.Requests);
        Assert.Empty(state.Messages);
    }

    [Fact]
    public async Task RunTurnAsync_rejects_disabled_loop_without_accepting_user_message()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var disabledLoop = LoopDefinition.CreateDefaultConversation() with { State = LoopState.Disabled };
        var runner = new DefaultConversationLoopRunner(client, state, memory, disabledLoop, runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.False(result.UserMessageAccepted);
        Assert.Equal("Loop `default-conversation` is not enabled.", result.FailureDetail);
        Assert.Empty(client.Requests);
        Assert.Empty(state.Messages);
        Assert.Empty(memory.Messages);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Failed, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_rejects_graph_shapes_the_default_runner_does_not_execute()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var graph = BuiltInLoopGraphDefinition.CreateDefaultConversation();
        var extendedLoop = LoopDefinition.CreateDefaultConversation() with
        {
            Graph = graph with
            {
                Nodes = graph.Nodes.Concat(
                [
                    new LoopGraphNodeDefinition(
                        "future-hook",
                        "Future hook",
                        "A future hook node that the current default conversation runner must not silently ignore.",
                        LoopGraphNodeKind.ToolActuation,
                        LoopGraphNodeEditMode.UserEditable,
                        [])
                ]).ToArray(),
                Edges = graph.Edges.Concat(
                [
                    new LoopGraphEdgeDefinition(
                        "context-to-future-hook",
                        DefaultConversationLoopGraphIds.AssembleContext,
                        "future-hook",
                        LoopGraphEdgeCondition.Success,
                        "A future hook can be reached from context assembly in this unsupported graph shape."),
                    new LoopGraphEdgeDefinition(
                        "future-hook-to-complete-run",
                        "future-hook",
                        DefaultConversationLoopGraphIds.CompleteRun,
                        LoopGraphEdgeCondition.Success,
                        "A future hook can terminate in this unsupported graph shape.")
                ]).ToArray()
            }
        };
        var runner = new DefaultConversationLoopRunner(client, state, memory, extendedLoop, runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.False(result.UserMessageAccepted);
        Assert.Contains("does not execute yet", result.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
        Assert.Empty(state.Messages);
        Assert.Empty(memory.Messages);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Failed, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_rejects_changed_default_graph_edges()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var graph = BuiltInLoopGraphDefinition.CreateDefaultConversation();
        var changedLoop = LoopDefinition.CreateDefaultConversation() with
        {
            Graph = graph with
            {
                Edges = graph.Edges.Select(edge => edge.Id == "context-to-inference" ? edge with { Condition = LoopGraphEdgeCondition.Always } : edge).ToArray()
            }
        };
        var runner = new DefaultConversationLoopRunner(client, state, memory, changedLoop, runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.False(result.UserMessageAccepted);
        Assert.Contains("missing required default edge `context-to-inference`", result.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
        Assert.Empty(state.Messages);
        Assert.Empty(memory.Messages);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Failed, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_returns_failed_result_when_started_run_cannot_be_recorded()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore { FailureAtSaveNumber = 1 };
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.False(result.UserMessageAccepted);
        Assert.Contains("Could not record loop run start", result.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
        Assert.Collection(state.Messages, message => Assert.Equal("startup context", message.Content));
        Assert.Empty(memory.Messages);
    }

    [Fact]
    public async Task RunTurnAsync_preserves_needs_review_when_terminal_run_status_cannot_be_recorded()
    {
        var client = new RecordingInferenceClient("unused") { Failure = new InvalidOperationException("provider failed") };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore { FailureAtSaveNumber = 2 };
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.NeedsReview, result.Status);
        Assert.True(result.UserMessageAccepted);
        Assert.Contains("provider failed", result.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("Durable terminal synchronization also failed", result.FailureDetail, StringComparison.Ordinal);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_returns_failed_result_when_completed_run_status_cannot_be_recorded()
    {
        var client = new RecordingInferenceClient("completed response");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore { FailureAtSaveNumber = 2 };
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Contains("terminal synchronization did not complete", result.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("provider redispatch is forbidden", result.FailureDetail, StringComparison.Ordinal);
        Assert.True(result.UserMessageAccepted);
        Assert.Collection(
            result.TranscriptMessages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            memory.Messages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_retains_observed_assistant_for_restart_recovery_when_publication_fails()
    {
        var client = new RecordingInferenceClient("completed response");
        var memory = new RecordingConversationMemoryStore { FailureAtAppendNumber = 2 };
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Contains("memory store failed", result.FailureDetail, StringComparison.Ordinal);
        Assert.True(result.UserMessageAccepted);
        Assert.Collection(
            result.TranscriptMessages,
            message => Assert.Equal("hello", message.Content));
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("hello", message.Content));
        var persistedMessage = Assert.Single(memory.Messages);
        Assert.Equal("hello", persistedMessage.Content);
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_leaves_an_uncertain_user_append_for_recovery_and_blocks_a_later_dispatch()
    {
        var client = new RecordingInferenceClient("unused");
        var memory = new RecordingConversationMemoryStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(
            client,
            state,
            memory,
            LoopDefinition.CreateDefaultConversation(),
            new RecordingLoopRunStore(),
            RuntimeSurfaceId.Web,
            failpoint: new GenericFailureFailpoint(DefaultConversationTurnBoundary.UserTranscriptAppended));

        var failed = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));
        var blocked = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("do not dispatch"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, failed.Status);
        Assert.True(failed.UserMessageAccepted);
        Assert.Contains("Restart reconciliation", failed.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, blocked.Status);
        Assert.Contains("incomplete durable checkpoint", blocked.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
        Assert.Collection(memory.Messages, message => Assert.Equal("hello", message.Content));
    }

    [Fact]
    public async Task RunTurnAsync_holds_the_conversation_commit_boundary_until_the_assistant_is_persisted()
    {
        var client = new BlockingInferenceClient("completed response");
        var memory = new RecordingConversationMemoryStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        var runner = new DefaultConversationLoopRunner(client, state, memory);

        var turn = runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello"));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var competingLease = state.AcquireExclusiveAccessAsync();

        Assert.False(competingLease.IsCompleted);

        client.Release.TrySetResult();
        var result = await turn;
        using (await competingLease)
        {
            state.AppendMessage(LlmMessage.Assistant("later custom-loop publication"));
        }

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, result.Status);
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("startup context", message.Content),
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content),
            message => Assert.Equal("later custom-loop publication", message.Content));
    }

    [Fact]
    public async Task RunTurnAsync_serializes_workspace_runtime_instances_and_synchronizes_durable_history_before_inference()
    {
        var scope = "workspace-" + Guid.NewGuid().ToString("N");
        var firstClient = new BlockingInferenceClient("first response");
        var secondClient = new RecordingInferenceClient("second response");
        var memory = new RecordingConversationMemoryStore();
        var firstState = new ConversationRuntimeState([LlmMessage.System("startup context")], exclusiveAccessScope: scope);
        var secondState = new ConversationRuntimeState([LlmMessage.System("startup context")], exclusiveAccessScope: scope);
        var firstRunner = new DefaultConversationLoopRunner(firstClient, firstState, memory);
        var secondRunner = new DefaultConversationLoopRunner(secondClient, secondState, memory);

        var firstTurn = firstRunner.RunTurnAsync(new DefaultConversationLoopTurnRequest("first prompt"));
        await firstClient.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTurn = secondRunner.RunTurnAsync(new DefaultConversationLoopTurnRequest("second prompt"));

        await Task.Delay(50);
        Assert.Empty(secondClient.Requests);

        firstClient.Release.TrySetResult();
        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, (await firstTurn).Status);
        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, (await secondTurn).Status);
        var secondRequest = Assert.Single(secondClient.Requests);
        Assert.Collection(
            secondRequest.Messages,
            message => Assert.Equal("first prompt", message.Content),
            message => Assert.Equal("first response", message.Content),
            message => Assert.Equal("second prompt", message.Content));
        Assert.Equal("startup context", Assert.Single(secondRequest.InstructionContext!.TrustedInstructions).Content);
        Assert.Collection(
            memory.Messages,
            message => Assert.Equal("first prompt", message.Content),
            message => Assert.Equal("first response", message.Content),
            message => Assert.Equal("second prompt", message.Content),
            message => Assert.Equal("second response", message.Content));
    }

    [Fact]
    public async Task RunTurnAsync_preserves_active_context_when_another_runtime_rotates_the_durable_conversation()
    {
        var client = new RecordingInferenceClient("must not run");
        var memory = new RecordingConversationMemoryStore();
        var state = new ConversationRuntimeState([LlmMessage.System("startup context")]);
        state.AppendMessage(LlmMessage.User("active prompt"));
        state.AppendMessage(LlmMessage.Assistant("active response"));
        var runner = new DefaultConversationLoopRunner(client, state, memory);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("next prompt"));

        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, result.Status);
        Assert.Contains("changed outside this runtime", result.FailureDetail, StringComparison.Ordinal);
        Assert.False(result.UserMessageAccepted);
        Assert.Empty(client.Requests);
        Assert.Equal(["startup context", "active prompt", "active response"], state.Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task RunTurnAsync_cancels_a_queued_turn_without_accepting_its_prompt_or_creating_a_run()
    {
        var client = new BlockingInferenceClient("first response");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);
        using var queuedCancellation = new CancellationTokenSource();

        var firstTurn = runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("first prompt"));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedTurn = runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("abandoned prompt", cancellationToken: queuedCancellation.Token));
        await queuedCancellation.CancelAsync();

        var cancelled = await queuedTurn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DefaultConversationLoopTurnStatus.Cancelled, cancelled.Status);
        Assert.False(cancelled.UserMessageAccepted);
        Assert.Null(cancelled.RunIdentity);
        Assert.DoesNotContain(state.Messages, message => message.Content == "abandoned prompt");
        Assert.DoesNotContain(memory.Messages, message => message.Content == "abandoned prompt");
        Assert.Single(runs.Saved);

        client.Release.TrySetResult();
        var completed = await firstTurn;

        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, completed.Status);
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("first prompt", message.Content),
            message => Assert.Equal("first response", message.Content));
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Completed, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_commits_a_returned_assistant_response_even_when_the_request_is_cancelled_after_generation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingInferenceClient("completed response") { AfterGenerate = cancellation.Cancel };
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, result.Status);
        Assert.Collection(
            state.Messages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            memory.Messages,
            message => Assert.Equal("hello", message.Content),
            message => Assert.Equal("completed response", message.Content));
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.Completed, run.Status));
    }

    [Fact]
    public async Task RunTurnAsync_parks_cancellation_during_inference_when_provider_outcome_is_unknown()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingInferenceClient("partial response");
        var memory = new RecordingConversationMemoryStore();
        var runs = new RecordingLoopRunStore();
        var state = new ConversationRuntimeState();
        var runner = new DefaultConversationLoopRunner(client, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web);

        var result = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest(
            "hello",
            responseChunkHandler: (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            },
            cancellationToken: cancellation.Token));

        Assert.Equal(DefaultConversationLoopTurnStatus.NeedsReview, result.Status);
        Assert.Contains("Automatic redispatch is forbidden", result.FailureDetail, StringComparison.Ordinal);
        Assert.True(result.UserMessageAccepted);
        Assert.Collection(state.Messages, message => Assert.Equal("hello", message.Content));
        Assert.Collection(memory.Messages, message => Assert.Equal("hello", message.Content));
        Assert.Collection(result.TranscriptMessages, message => Assert.Equal("hello", message.Content));
        Assert.Collection(
            runs.Saved,
            run => Assert.Equal(LoopRunStatus.Started, run.Status),
            run => Assert.Equal(LoopRunStatus.NeedsReview, run.Status));
    }

    private sealed class RecordingInferenceClient(string output) : ILlmInferenceClient, IQuarantinableInferenceClient
    {
        public List<LlmInferenceRequest> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public Action? AfterGenerate { get; init; }

        public int QuarantineCount { get; private set; }

        public string? ProviderResponseId { get; init; }

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Failure is not null)
            {
                throw Failure;
            }

            if (responseChunkHandler is not null)
            {
                await responseChunkHandler(output, cancellationToken);
            }

            AfterGenerate?.Invoke();
            return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex, ProviderResponseId: ProviderResponseId);
        }

        public Task QuarantineAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuarantineCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDefaultConversationTurnStore : IDefaultConversationTurnStore
    {
        public DefaultConversationTurnRecord? Record { get; private set; }

        public Task<DefaultConversationTurnStoreResult> CreateAsync(DefaultConversationTurnRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Record is null)
            {
                Record = record;
                return Task.FromResult(new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Created, record));
            }

            var status = Record.LifecycleVersion == record.LifecycleVersion && Record.Checkpoint == record.Checkpoint
                ? DefaultConversationTurnStoreStatus.Replay
                : DefaultConversationTurnStoreStatus.Conflict;
            return Task.FromResult(new DefaultConversationTurnStoreResult(status, Record));
        }

        public Task<DefaultConversationTurnStoreResult> UpdateAsync(DefaultConversationTurnRecord record, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Record is null || Record.LifecycleVersion != expectedLifecycleVersion || record.LifecycleVersion != expectedLifecycleVersion + 1)
            {
                return Task.FromResult(new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Conflict, Record));
            }

            Record = record;
            return Task.FromResult(new DefaultConversationTurnStoreResult(DefaultConversationTurnStoreStatus.Updated, record));
        }

        public Task<DefaultConversationTurnRecord?> LoadAsync(string turnId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(string.Equals(Record?.TurnId, turnId, StringComparison.Ordinal) ? Record : null);
        }

        public Task<IReadOnlyList<DefaultConversationTurnRecord>> ListIncompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DefaultConversationTurnRecord> records = Record is not null && Record.Checkpoint < DefaultConversationTurnCheckpoint.Terminal ? [Record] : [];
            return Task.FromResult(records);
        }

        public Task<IReadOnlyList<DefaultConversationTurnRecord>> ListNeedsReviewAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DefaultConversationTurnRecord> records = Record is not null && Record.Run.Status == LoopRunStatus.NeedsReview && Record.ReviewResolution is null ? [Record] : [];
            return Task.FromResult(records);
        }
    }

    private sealed class StaticWorkspaceContextStore(params WorkspaceContextDocument[] documents) : IWorkspaceContextStore
    {
        public Task<IReadOnlyList<WorkspaceContextDocument>> LoadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceContextDocument>>(documents);
        }
    }

    private sealed class BlockingInferenceClient(string output) : ILlmInferenceClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex);
        }
    }

    private sealed class GenericFailureFailpoint(DefaultConversationTurnBoundary boundary) : IDefaultConversationTurnFailpoint
    {
        public Task AfterBoundaryAsync(DefaultConversationTurnBoundary currentBoundary, DefaultConversationTurnRecord record, CancellationToken cancellationToken = default)
        {
            return currentBoundary == boundary
                ? Task.FromException(new IOException($"Injected durable-boundary failure after `{currentBoundary}`."))
                : Task.CompletedTask;
        }
    }

    private sealed class RecordingConversationMemoryStore : IConversationMemoryStore
    {
        private readonly List<(string MessageId, string PublicationId)?> _publicationIdentities = [];

        public List<LlmMessage> Messages { get; } = [];

        public int? FailureAtAppendNumber { get; init; }

        public Exception? LoadCurrentException { get; init; }

        public Action<CancellationToken>? BeforeLoadCurrent { get; init; }

        public Task AppendMessageAsync(LlmMessage message, CancellationToken cancellationToken = default)
        {
            if (FailureAtAppendNumber == Messages.Count + 1)
            {
                throw new IOException("memory store failed");
            }

            Messages.Add(message);
            _publicationIdentities.Add(null);
            return Task.CompletedTask;
        }

        public Task<bool> TryAppendMessageAsync(string expectedConversationId, string expectedConversationVersion, IReadOnlyList<LlmMessage> expectedPrefix, LlmMessage message, CancellationToken cancellationToken = default)
        {
            if (FailureAtAppendNumber == Messages.Count + 1)
            {
                throw new IOException("memory store failed");
            }

            var matches = string.Equals(expectedConversationId, "current", StringComparison.Ordinal)
                && string.Equals(expectedConversationVersion, "default-loop-version", StringComparison.Ordinal)
                && Messages.Count == expectedPrefix.Count
                && Messages.Zip(expectedPrefix).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
            if (matches)
            {
                Messages.Add(message);
                _publicationIdentities.Add(null);
            }

            return Task.FromResult(matches);
        }

        public Task<ConversationPublicationAppendResult> TryPublishMessageAsync(string expectedConversationId, string expectedConversationVersion, IReadOnlyList<LlmMessage> expectedPrefix, ConversationMessagePublication publication, CancellationToken cancellationToken = default)
        {
            if (FailureAtAppendNumber == Messages.Count + 1)
            {
                throw new IOException("memory store failed");
            }

            var prefixMatches = string.Equals(expectedConversationId, "current", StringComparison.Ordinal)
                && string.Equals(expectedConversationVersion, "default-loop-version", StringComparison.Ordinal)
                && Messages.Count >= expectedPrefix.Count
                && Messages.Take(expectedPrefix.Count).Zip(expectedPrefix).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
            if (!prefixMatches)
            {
                return Task.FromResult(PublicationResult(ConversationPublicationAppendStatus.Conflict));
            }

            if (Messages.Count == expectedPrefix.Count)
            {
                Messages.Add(publication.Message);
                _publicationIdentities.Add((publication.MessageId, publication.PublicationId));
                return Task.FromResult(PublicationResult(ConversationPublicationAppendStatus.Appended));
            }

            if (Messages.Count == expectedPrefix.Count + 1
                && _publicationIdentities[expectedPrefix.Count] is { } identity
                && string.Equals(identity.MessageId, publication.MessageId, StringComparison.Ordinal)
                && string.Equals(identity.PublicationId, publication.PublicationId, StringComparison.Ordinal)
                && Messages[^1].Role == publication.Message.Role
                && string.Equals(Messages[^1].Content, publication.Message.Content, StringComparison.Ordinal))
            {
                return Task.FromResult(PublicationResult(ConversationPublicationAppendStatus.AlreadyPresent));
            }

            return Task.FromResult(PublicationResult(ConversationPublicationAppendStatus.Conflict));
        }

        public Task<IReadOnlyList<LlmMessage>> LoadCurrentConversationAsync(CancellationToken cancellationToken = default)
        {
            BeforeLoadCurrent?.Invoke(cancellationToken);
            if (LoadCurrentException is not null)
            {
                throw LoadCurrentException;
            }

            return Task.FromResult<IReadOnlyList<LlmMessage>>(Messages);
        }

        public Task<ConversationMemorySnapshot> LoadCurrentConversationSnapshotAsync(CancellationToken cancellationToken = default)
        {
            BeforeLoadCurrent?.Invoke(cancellationToken);
            if (LoadCurrentException is not null)
            {
                throw LoadCurrentException;
            }

            return Task.FromResult(new ConversationMemorySnapshot("current", "default-loop-version", Messages));
        }

        public Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ConversationTranscriptListItem>>([]);
        }

        public Task StartFreshConversationAsync(CancellationToken cancellationToken = default)
        {
            Messages.Clear();
            _publicationIdentities.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LlmMessage>>([]);
        }

        public Task ResumeConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationMemorySearchResult>> SearchCurrentConversationAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ConversationMemorySearchResult>>([]);
        }

        private ConversationPublicationAppendResult PublicationResult(ConversationPublicationAppendStatus status)
        {
            return new ConversationPublicationAppendResult(status, new ConversationMemorySnapshot("current", "default-loop-version", Messages.ToArray()));
        }
    }

    private sealed class RecordingLoopRunStore : ILoopRunStore
    {
        public List<LoopRunRecord> Saved { get; } = [];

        public int? FailureAtSaveNumber { get; init; }

        public Task SaveAsync(LoopRunRecord run, CancellationToken cancellationToken = default)
        {
            if (FailureAtSaveNumber == Saved.Count + 1)
            {
                throw new IOException("run store failed");
            }

            Saved.Add(run);
            return Task.CompletedTask;
        }

        public Task<LoopRunRecord?> LoadAsync(string loopId, string runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LoopRunRecord?>(null);
        }

        public Task<IReadOnlyList<LoopRunRecord>> ListAsync(string loopId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LoopRunRecord>>([]);
        }
    }
}
