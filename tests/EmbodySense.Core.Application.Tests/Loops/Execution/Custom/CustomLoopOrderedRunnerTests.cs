using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed class CustomLoopOrderedRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        var store = new FakeRunStore(Run(Definition()));
        var resolver = new CustomLoopContextResolver();
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();

        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(null!, resolver, executor, publisher, audit));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, null!, executor, publisher, audit));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, resolver, null!, publisher, audit));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, resolver, executor, null!, audit));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopOrderedRunner(store, resolver, executor, publisher, null!));
    }

    [Fact]
    public async Task Run_executes_inference_steps_in_persisted_order_and_completes_without_an_Exit_call_when_disabled()
    {
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
                Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
            ],
            maxAdditionalIterations: 0,
            tools: [CustomLoopToolAssignment.Read]);
        var store = new FakeRunStore(Run(definition));
        var audit = new RecordingAuditLog();
        var executor = new QueueExecutor(
            Result("first retained output", toolCalls: 2),
            Result("final output", toolCalls: 1));
        executor.BeforeExecute = request =>
        {
            Assert.Equal(CustomLoopRunEventKind.NodeAttemptStarted, store.Current.Events[^1].Kind);
            Assert.Contains(audit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Started);
            return Task.CompletedTask;
        };
        var runner = Runner(store, executor, audit: audit);

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, string.Join(Environment.NewLine, store.ValidationFailures));
        Assert.Equal(CustomLoopRunStatus.Completed, result.Run!.Status);
        Assert.Equal("final output", result.Run.FinalOutput);
        Assert.Equal(["step-first", "step-second"], executor.Requests.Select(item => item.StepId));
        Assert.All(executor.Requests, request => Assert.False(request.IsExit));
        Assert.All(executor.Requests, request => Assert.Equal([CustomLoopToolAssignment.Read], request.AdmittedToolAssignments));
        Assert.Equal([0, 2], executor.Requests.Select(item => item.ToolRequestsUsedInRun));
        Assert.Equal(3, result.Run.Checkpoint.ToolRequestsUsed);
        Assert.Contains(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("first retained output", StringComparison.Ordinal));
        Assert.DoesNotContain(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("provider-thread", StringComparison.Ordinal));
        var deterministicExit = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted);
        Assert.Equal(CustomLoopExitDecision.Complete, deterministicExit.ExitDecision);
        Assert.Contains("disabled", deterministicExit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionStarted);
        Assert.Contains(audit.Events, item => item.Action == AuditSchema.Actions.LoopRunLifecycle && item.Metadata.ContainsKey("terminalStatus"));
    }

    [Fact]
    public async Task Evidence_is_retained_even_when_output_is_not_visible_to_later_nodes_and_the_last_output_still_becomes_the_iteration_result()
    {
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", Output(retain: false, publish: false)),
                Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
            ],
            maxAdditionalIterations: 0);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("evidence only"), Result("iteration result"));
        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, string.Join(Environment.NewLine, store.ValidationFailures));
        Assert.DoesNotContain(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("evidence only", StringComparison.Ordinal));
        var evidence = Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved && item.StepId == "step-first");
        Assert.Equal("evidence only", evidence.CanonicalOutput);
        Assert.False(evidence.RetainedForLoopReasoning);
        Assert.Equal("iteration result", result.Run.Checkpoint.CurrentIterationResult!.Content);
        Assert.Equal("iteration result", result.Run.FinalOutput);
    }

    [Fact]
    public async Task Canonical_output_preserves_exact_text_and_is_truncated_once_then_reused_for_context_and_evidence()
    {
        var longOutput = "e\u0301" + new string('x', CustomLoopLimits.MaxCanonicalModelOutputCharacters + 20);
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
                Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
            ],
            maxAdditionalIterations: 0);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result(longOutput), Result("final"));

        var runResult = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        var canonical = runResult.Run!.Events.First(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved && item.StepId == "step-first");
        Assert.Equal(CustomLoopLimits.MaxCanonicalModelOutputCharacters, canonical.CanonicalOutput!.Length);
        Assert.Equal(longOutput.Length, canonical.OriginalOutputCharacterCount);
        Assert.True(canonical.CanonicalOutputTruncated);
        Assert.StartsWith("e\u0301", canonical.CanonicalOutput, StringComparison.Ordinal);
        Assert.Contains(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains(canonical.CanonicalOutput, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exact_Repeat_restarts_step_zero_and_carries_the_previous_iteration_result_only_when_Exit_retains_it()
    {
        var exitPolicy = Policy(Output(retain: true, publish: false));
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: exitPolicy,
            tools: [CustomLoopToolAssignment.List]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(
            Result("iteration one"),
            Result("  rEpEaT\r\n"),
            Result("iteration two"),
            Result("Complete"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(["step-only", "exit", "step-only", "exit"], executor.Requests.Select(item => item.StepId));
        Assert.True(executor.Requests[1].IsExit);
        Assert.False(executor.Requests[1].AllowTools);
        Assert.Empty(executor.Requests[1].AdmittedToolAssignments);
        Assert.Contains(executor.Requests[2].InferenceRequest.Messages, item => item.Content.Contains("iteration one", StringComparison.Ordinal));
        Assert.Equal(1, result.Run!.Checkpoint.AcceptedRepeatCount);
        Assert.Equal(2, result.Run.Checkpoint.Iteration);
        Assert.Equal("iteration two", result.Run.FinalOutput);
    }

    [Fact]
    public async Task Repeat_does_not_carry_the_previous_iteration_result_when_Exit_discards_it()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration one"), Result("Repeat"), Result("iteration two"), Result("Complete"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.DoesNotContain(executor.Requests[2].InferenceRequest.Messages, item => item.Content.Contains("iteration one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeat_ceiling_completes_after_the_final_allowed_iteration_without_another_Exit_model_call()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 1,
            exitPolicy: Policy(Output(retain: true, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration one"), Result("Repeat"), Result("iteration two"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(["step-only", "exit", "step-only"], executor.Requests.Select(item => item.StepId));
        Assert.Equal("iteration two", result.Run!.FinalOutput);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted && item.Detail.Contains("repeat ceiling", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Complete.")]
    [InlineData("Complete Repeat")]
    [InlineData("{\"decision\":\"Complete\"}")]
    [InlineData("~~~Complete~~~")]
    public async Task Invalid_Exit_output_never_repeats_and_becomes_NeedsReview(string decision)
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: Policy(Output(retain: true, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration one"), Result(decision));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal(2, executor.Requests.Count);
        var exit = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted);
        Assert.Equal(CustomLoopExitDecision.Invalid, exit.ExitDecision);
        Assert.Equal(decision, exit.CanonicalOutput);
    }

    [Fact]
    public async Task Failed_Exit_attempt_has_no_retry_and_becomes_NeedsReview()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            maxAdditionalIterations: 2,
            exitPolicy: Policy(Output(retain: true, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("iteration one"), new InvalidOperationException("provider down"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Contains(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed && item.StepId == "exit");
    }

    [Fact]
    public async Task Failed_inference_stops_later_steps_without_retry()
    {
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
                Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
            ],
            maxAdditionalIterations: 0);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(new InvalidOperationException("provider down"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Single(executor.Requests);
        Assert.DoesNotContain(result.Run!.Events, item => item.StepId == "step-second");
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("io")]
    [InlineData("wrapped-io")]
    [InlineData("aggregate-timeout")]
    public async Task Transport_failure_after_dispatch_is_uncertain_and_becomes_NeedsReview_without_retry(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "timeout" => new TimeoutException("provider timeout"),
            "io" => new IOException("transport closed"),
            "wrapped-io" => new InvalidOperationException("provider wrapper", new IOException("transport closed")),
            _ => new AggregateException(new InvalidOperationException("definite"), new TimeoutException("provider timeout"))
        };
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var executor = new QueueExecutor(failure);

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("inference_attempt_uncertain", result.Run!.FailureCode);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Outcome_evidence_and_audit_precede_idempotent_publication_which_precedes_the_checkpoint()
    {
        var outputPolicy = Output(retain: false, publish: true);
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", outputPolicy)],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var run = Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", Now));
        var store = new FakeRunStore(run);
        var audit = new RecordingAuditLog();
        var publisher = new RecordingPublisher();
        publisher.BeforePublish = request =>
        {
            Assert.Equal(CustomLoopRunEventKind.ConversationPublicationStarted, store.Current.Events[^1].Kind);
            Assert.Contains(store.Current.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
            Assert.Contains(audit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Succeeded);
            return Task.CompletedTask;
        };
        var executor = new QueueExecutor(Result("published output"));

        var result = await Runner(store, executor, publisher, audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        var publicationRequest = Assert.Single(publisher.Requests);
        Assert.Equal("published output", publicationRequest.CanonicalOutput);
        Assert.StartsWith("publish-", publicationRequest.OperationId, StringComparison.Ordinal);
        var observedSequence = result.Run!.Events.Single(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved).Sequence;
        var publishedSequence = result.Run.Events.Single(item => item.Kind == CustomLoopRunEventKind.ConversationPublished).Sequence;
        var checkpointSequence = result.Run.Events.First(item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted).Sequence;
        Assert.True(observedSequence < publishedSequence);
        Assert.True(publishedSequence < checkpointSequence);
    }

    [Fact]
    public async Task Sequential_node_and_Exit_publications_advance_from_the_immutable_admission_version_using_durable_prior_outputs()
    {
        var publish = Output(retain: true, publish: true);
        var definition = Definition(
            steps:
            [
                Step("step-first", "First", "First instruction", publish),
                Step("step-second", "Second", "Second instruction", publish)
            ],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: true)));
        var conversation = new CustomLoopConversationReference("conversation-one", "immutable-admission-version", Now);
        var store = new FakeRunStore(Run(definition, conversation: conversation));
        var publisher = new RecordingPublisher();

        var result = await Runner(store, new QueueExecutor(Result("first output"), Result("second output")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(3, publisher.Requests.Count);
        Assert.All(publisher.Requests, request => Assert.Equal(conversation.CapturedVersion, request.ExpectedConversationVersion));
        Assert.Empty(publisher.Requests[0].PriorPublications!);
        Assert.Collection(
            publisher.Requests[1].PriorPublications!,
            prior => Assert.Equal("first output", prior.CanonicalOutput));
        Assert.Collection(
            publisher.Requests[2].PriorPublications!,
            prior => Assert.Equal("first output", prior.CanonicalOutput),
            prior => Assert.Equal("second output", prior.CanonicalOutput));
        Assert.Equal(
            ["first output", "second output", "second output"],
            result.Run!.Events.Where(item => item is { Kind: CustomLoopRunEventKind.ConversationPublished, PublishedToInvokingConversation: true }).Select(item => item.CanonicalOutput));
    }

    [Fact]
    public async Task Selected_publication_without_a_bound_destination_is_a_recorded_omission_and_never_calls_the_publisher()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var store = new FakeRunStore(Run(definition));
        var publisher = new RecordingPublisher();

        var result = await Runner(store, new QueueExecutor(Result("evidence")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Empty(publisher.Requests);
        var omitted = Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
        Assert.False(omitted.PublishedToInvokingConversation);
        Assert.Contains("omitted", omitted.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CustomLoopConversationPublicationOutcome.DefinitelyFailed, CustomLoopOrderedRunStatus.Failed)]
    [InlineData(CustomLoopConversationPublicationOutcome.Uncertain, CustomLoopOrderedRunStatus.NeedsReview)]
    [InlineData(CustomLoopConversationPublicationOutcome.Unknown, CustomLoopOrderedRunStatus.NeedsReview)]
    public async Task Publication_failure_is_durable_and_never_reported_as_success(CustomLoopConversationPublicationOutcome outcome, CustomLoopOrderedRunStatus expected)
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do the work", Output(retain: false, publish: true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(retain: false, publish: false)));
        var run = Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", Now));
        var store = new FakeRunStore(run);
        var publisher = new RecordingPublisher { NextResult = new CustomLoopConversationPublicationResult(outcome, null, "safe publication detail") };

        var result = await Runner(store, new QueueExecutor(Result("evidence")), publisher).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(expected, result.Status);
        Assert.NotEqual(CustomLoopRunStatus.Completed, result.Run!.Status);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ConversationPublished);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Fact]
    public async Task Missing_started_audit_blocks_provider_dispatch()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var executor = new QueueExecutor(Result("must not run"));
        var audit = new RecordingAuditLog { FailPredicate = item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Started };

        var result = await Runner(store, executor, audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Empty(executor.Requests);
        Assert.Contains(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted);
    }

    [Fact]
    public async Task Missing_outcome_audit_stops_before_publication_and_checkpoint_and_requires_review()
    {
        var definition = Definition(
            steps: [Step("step-only", "Only", "Do", Output(false, true))],
            maxAdditionalIterations: 0,
            exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "version-one", Now)));
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog { FailPredicate = item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Succeeded };

        var result = await Runner(store, new QueueExecutor(Result("observed")), publisher, audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Empty(publisher.Requests);
        Assert.Contains(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Theory]
    [InlineData("different-provider", "model", 0)]
    [InlineData("provider", "different-model", 0)]
    [InlineData("provider", "model", 7)]
    public async Task Rejected_provider_results_are_audited_as_needs_review_and_never_as_succeeded(string provider, string model, int toolCalls)
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var audit = new RecordingAuditLog();
        var providerResult = new CustomLoopInferenceAttemptResult("untrusted outcome", provider, model, "response-invalid", toolCalls);

        var result = await Runner(store, new QueueExecutor(providerResult), audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("provider_result_mismatch", result.Run!.FailureCode);
        var outcomeAudit = Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome != AuditSchema.Outcomes.Started);
        Assert.Equal(AuditSchema.Outcomes.NeedsReview, outcomeAudit.Outcome);
        Assert.DoesNotContain(audit.Events, item => item.Action == AuditSchema.Actions.LoopNodeAttempt && item.Outcome == AuditSchema.Outcomes.Succeeded);
    }

    [Fact]
    public async Task Caller_cancellation_after_provider_return_cannot_cancel_outcome_or_checkpoint_integrity_writes()
    {
        using var cancellation = new CancellationTokenSource();
        var definition = Definition(exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("observed"));
        executor.AfterExecute = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Contains(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
    }

    [Fact]
    public async Task Invalid_persisted_state_is_rejected_before_dispatch()
    {
        var invalid = Run(Definition()) with { AdmissionRequestHash = new string('0', CustomLoopLimits.Sha256HexCharacters) };
        var store = new FakeRunStore(invalid, validateSeed: false);
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(invalid.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Admission_without_the_durable_audit_marker_is_rejected_before_dispatch()
    {
        var marked = Run(Definition());
        var incomplete = marked with { LifecycleVersion = 1, Events = [marked.Events[0]] };
        var store = new FakeRunStore(incomplete);
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(incomplete.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Contains("admission", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(executor.Requests);
        Assert.Equal(CustomLoopRunStatus.Admitted, store.Current.Status);
    }

    [Fact]
    public async Task Public_execution_rejects_a_Running_run_even_at_a_safe_boundary()
    {
        var admitted = Run(Definition());
        var lifecycle = new CustomLoopRunEvent(3, "event-running", Now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null);
        var running = admitted with
        {
            LifecycleVersion = 3,
            Status = CustomLoopRunStatus.Running,
            ExecutionClock = new CustomLoopExecutionClock(0, Now),
            Events = [.. admitted.Events, lifecycle]
        };
        var store = new FakeRunStore(running);
        var executor = new QueueExecutor(Result("must not run"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(running.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, result.Status);
        Assert.Contains("explicit recovery", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Resume_and_load_boundaries_reject_missing_mismatched_and_failed_reads_without_dispatch()
    {
        var admitted = Run(Definition());
        var lifecycle = new CustomLoopRunEvent(3, "event-running", Now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null);
        var running = admitted with
        {
            LifecycleVersion = 3,
            Status = CustomLoopRunStatus.Running,
            ExecutionClock = new CustomLoopExecutionClock(0, Now),
            Events = [.. admitted.Events, lifecycle]
        };
        var executor = new QueueExecutor(Result("must not run"));
        var missingStore = new FakeRunStore(running) { ReturnMissing = true };
        var missingRunner = Runner(missingStore, executor);
        var missing = await missingRunner.ResumeAsync(new CustomLoopResumeExecutionRequest(running.Id, running.LifecycleVersion, lifecycle.EventId, AuditSchema.Actors.Web));

        var mismatchRunner = Runner(new FakeRunStore(running), executor);
        var mismatch = await mismatchRunner.ResumeAsync(new CustomLoopResumeExecutionRequest(running.Id, running.LifecycleVersion, "different-operation", AuditSchema.Actors.Web));

        var invalidRunning = running with { AdmissionRequestHash = new string('0', CustomLoopLimits.Sha256HexCharacters) };
        var invalid = await Runner(new FakeRunStore(invalidRunning, validateSeed: false), executor).ResumeAsync(new CustomLoopResumeExecutionRequest(invalidRunning.Id, invalidRunning.LifecycleVersion, lifecycle.EventId, AuditSchema.Actors.Web));

        var failedStore = new FakeRunStore(running) { GetException = new IOException("Unavailable.") };
        var failedRunner = Runner(failedStore, executor);
        var failed = await failedRunner.ResumeAsync(new CustomLoopResumeExecutionRequest(running.Id, running.LifecycleVersion, lifecycle.EventId, AuditSchema.Actors.Web));
        var failedPublicRun = await failedRunner.RunAsync(new CustomLoopOrderedRunRequest(running.Id, AuditSchema.Actors.Web));
        failedRunner.CancelActiveAttempt("INVALID");
        failedRunner.CancelActiveAttempt(running.Id);

        Assert.Equal(CustomLoopOrderedRunStatus.NotFound, missing.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, mismatch.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, invalid.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, failed.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, failedPublicRun.Status);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mismatchRunner.ResumeAsync(new CustomLoopResumeExecutionRequest(running.Id, 0, lifecycle.EventId, AuditSchema.Actors.Web)));
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_before_dispatch_cancels_the_admitted_run_without_provider_work()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_run_start_audit_cancels_instead_of_reporting_an_audit_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var audit = new RecordingAuditLog
        {
            BeforeAppend = (auditEvent, token) =>
            {
                if (auditEvent.Action == AuditSchema.Actions.LoopRunLifecycle && auditEvent.Outcome == AuditSchema.Outcomes.Started)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            }
        };

        var result = await Runner(store, executor, audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.DoesNotContain("run_start_audit_failed", result.Detail, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Durable_cancel_between_attempt_audit_and_registration_prevents_provider_dispatch()
    {
        var store = new FakeRunStore(Run(Definition()));
        var executor = new QueueExecutor(Result("must not run"));
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(Now));
        CustomLoopControlResult? cancel = null;
        audit.AfterAppend = async auditEvent =>
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopNodeAttempt && auditEvent.Outcome == AuditSchema.Outcomes.Started)
            {
                cancel = await lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-before-registration", AuditSchema.Actors.Web));
            }
        };

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Conflict_after_a_provider_outcome_persists_a_needs_review_terminal_trace()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            ConflictOnOutcomeWrite = true
        };
        var executor = new QueueExecutor(Result("provider outcome may exist"));

        var result = await Runner(store, executor).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("post_outcome_persistence_conflict", result.Run.FailureCode);
        Assert.Contains("external outcome may exist", result.Run.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
        Assert.Single(executor.Requests);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Conflict_after_a_provider_outcome_preserves_a_concurrent_needs_review_trace()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            ConflictOnOutcomeWrite = true,
            ConcurrentNeedsReviewOnOutcomeConflict = true
        };

        var result = await Runner(store, new QueueExecutor(Result("provider outcome may exist"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("concurrent_review", result.Run.FailureCode);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Conflict_after_a_provider_outcome_reports_when_the_latest_trace_disappears()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            ConflictOnOutcomeWrite = true,
            ReturnMissingAfterOutcomeConflict = true
        };

        var result = await Runner(store, new QueueExecutor(Result("provider outcome may exist"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Contains("latest run trace could not be found", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflict_after_a_provider_outcome_reports_uncertain_escalation_persistence()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            ConflictOnOutcomeWrite = true,
            GetExceptionAfterOutcomeConflict = new IOException("Unavailable.")
        };

        var result = await Runner(store, new QueueExecutor(Result("provider outcome may exist"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Contains("escalation persistence is uncertain", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminal_trace_is_durable_before_audit_and_audit_failure_preserves_Completed_output_with_a_visible_integrity_warning()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))));
        var audit = new RecordingAuditLog
        {
            FailPredicate = item =>
            {
                if (item.Action != AuditSchema.Actions.LoopRunLifecycle || !item.Metadata.ContainsKey("terminalStatus"))
                {
                    return false;
                }

                Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
                Assert.Equal("final", store.Current.FinalOutput);
                Assert.Equal(CustomLoopRunEventKind.LifecycleChanged, store.Current.Events[^1].Kind);
                return true;
            }
        };

        var result = await Runner(store, new QueueExecutor(Result("final")), audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, result.Run!.Status);
        Assert.Null(result.Run.FailureCode);
        Assert.Equal("final", result.Run.FinalOutput);
        Assert.Equal(CustomLoopRunEventKind.IntegrityWarning, result.Run.Events[^1].Kind);
        Assert.Contains("terminal audit append failed", result.Run.Events[^1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminal_warning_persistence_uncertainty_is_visible_without_rewriting_the_truthful_terminal_outcome()
    {
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            AppendTerminalWarningException = new IOException("warning store unavailable")
        };
        var audit = new RecordingAuditLog
        {
            FailPredicate = item => item.Action == AuditSchema.Actions.LoopRunLifecycle && item.Metadata.ContainsKey("terminalStatus")
        };

        var result = await Runner(store, new QueueExecutor(Result("final")), audit: audit).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, result.Run!.Status);
        Assert.Equal("final", result.Run.FinalOutput);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.IntegrityWarning);
        Assert.Contains("persistence outcome is uncertain", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_after_deterministic_Exit_outcome_cannot_cancel_its_post_outcome_audit()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeRunStore(Run(Definition(exitPolicy: Policy(Output(false, false)))))
        {
            AfterUpdate = run =>
            {
                if (run.Events[^1].Kind == CustomLoopRunEventKind.ExitDecisionCompleted)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            }
        };

        var result = await Runner(store, new QueueExecutor(Result("final"))).RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web), cancellation.Token);

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal("final", result.Run!.FinalOutput);
        Assert.Contains(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.ExitDecisionCompleted);
    }

    [Fact]
    public async Task Pause_during_an_open_attempt_finishes_that_attempt_commits_a_checkpoint_and_dispatches_nothing_later()
    {
        var definition = Definition(steps:
        [
            Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
            Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
        ]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("first outcome"), Result("must not dispatch"));
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(Now));
        CustomLoopControlResult? pause = null;
        executor.BeforeExecute = async _ => pause = await lifecycle.PauseAsync(new CustomLoopPauseRequest(store.Current.Id, store.Current.LifecycleVersion, "pause-open-attempt", AuditSchema.Actors.Web));

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.PauseRequested, pause!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, result.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, result.Run!.Status);
        Assert.Single(executor.Requests);
        Assert.Null(result.Run.ExecutionClock.ActiveSinceUtc);
        var checkpoint = Assert.Single(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.CheckpointCommitted);
        var paused = result.Run.Events.Last(item => item.Kind == CustomLoopRunEventKind.LifecycleChanged);
        Assert.True(checkpoint.Sequence < paused.Sequence);
        Assert.Equal(checkpoint.Sequence, result.Run.Checkpoint.LastCommittedSequence);
    }

    [Fact]
    public async Task Cancel_during_an_open_attempt_cancels_transport_and_records_NeedsReview_without_later_dispatch()
    {
        var definition = Definition(steps:
        [
            Step("step-first", "First", "First instruction", Output(retain: true, publish: false)),
            Step("step-second", "Second", "Second instruction", Output(retain: false, publish: false))
        ]);
        var store = new FakeRunStore(Run(definition));
        var executor = new QueueExecutor(Result("must be cancelled"), Result("must not dispatch"));
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit);
        var lifecycle = new CustomLoopLifecycleService(store, new FakeControlOperationStore(), runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(Now));
        CustomLoopControlResult? cancel = null;
        executor.BeforeExecute = async _ => cancel = await lifecycle.CancelAsync(new CustomLoopCancelRequest(store.Current.Id, store.Current.LifecycleVersion, "cancel-open-attempt", AuditSchema.Actors.Web));

        var result = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.CancelRequested, cancel!.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("inference_attempt_uncertain", result.Run.FailureCode);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task Explicit_resume_uses_the_paused_checkpoint_while_same_operation_replays_and_changed_content_conflicts()
    {
        var definition = Definition(steps:
        [
            Step("step-first", "First", "First instruction", Output(retain: true, publish: true)),
            Step("step-second", "Second", "Second instruction", Output(retain: false, publish: true))
        ], exitPolicy: Policy(Output(false, false)));
        var store = new FakeRunStore(Run(definition, conversation: new CustomLoopConversationReference("conversation-one", "immutable-admission-version", Now)));
        var executor = new QueueExecutor(Result("first outcome"), Result("second outcome"));
        var audit = new RecordingAuditLog();
        var firstPublisher = new RecordingPublisher();
        var runner = Runner(store, executor, firstPublisher, audit);
        var operations = new FakeControlOperationStore();
        var lifecycle = new CustomLoopLifecycleService(store, operations, runner, new AvailableModel(), runner, audit, new TestExecutionGate(), new FixedTimeProvider(Now));
        var pauseRequest = new CustomLoopPauseRequest(store.Current.Id, 4, "pause-for-resume", AuditSchema.Actors.Web);
        executor.BeforeExecute = async _ =>
        {
            if (executor.Requests.Count == 1)
            {
                Assert.Equal(pauseRequest.ExpectedLifecycleVersion, store.Current.LifecycleVersion);
                await lifecycle.PauseAsync(pauseRequest);
            }
        };

        var paused = await runner.RunAsync(new CustomLoopOrderedRunRequest(store.Current.Id, AuditSchema.Actors.Web));
        var replay = await lifecycle.PauseAsync(pauseRequest);
        var conflict = await lifecycle.PauseAsync(pauseRequest with { Actor = AuditSchema.Actors.Cli });
        executor.BeforeExecute = null;
        var resumedPublisher = new RecordingPublisher();
        var resumedRunner = Runner(store, executor, resumedPublisher, audit);
        var resumedLifecycle = new CustomLoopLifecycleService(store, operations, resumedRunner, new AvailableModel(), resumedRunner, audit, new TestExecutionGate(), new FixedTimeProvider(Now));
        var resumed = await resumedLifecycle.ResumeAsync(new CustomLoopResumeRequest(store.Current.Id, store.Current.LifecycleVersion, "resume-paused-run", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Paused, paused.Status);
        Assert.Equal(CustomLoopControlStatus.PauseRequested, replay.Status);
        Assert.Equal(CustomLoopControlStatus.Conflict, conflict.Status);
        Assert.Equal(CustomLoopControlStatus.Completed, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, resumed.Run!.Status);
        Assert.Equal(["step-first", "step-second"], executor.Requests.Select(item => item.StepId));
        Assert.Contains(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("first outcome", StringComparison.Ordinal));
        Assert.Single(firstPublisher.Requests);
        var resumedPublication = Assert.Single(resumedPublisher.Requests);
        var priorPublication = Assert.Single(resumedPublication.PriorPublications!);
        Assert.Equal("first outcome", priorPublication.CanonicalOutput);
    }

    [Fact]
    public async Task Missing_and_non_runnable_runs_fail_without_dispatch()
    {
        var seed = Run(Definition());
        var missingStore = new FakeRunStore(seed) { ReturnMissing = true };
        var executor = new QueueExecutor(Result("must not run"));
        var missing = await Runner(missingStore, executor).RunAsync(new CustomLoopOrderedRunRequest(seed.Id, AuditSchema.Actors.Web));

        var completedStore = new FakeRunStore(seed with
        {
            Status = CustomLoopRunStatus.Completed,
            CompletedAtUtc = Now,
            FinalOutput = "done",
            ExecutionClock = CustomLoopExecutionClock.NotStarted()
        }, validateSeed: false);
        var invalidState = await Runner(completedStore, executor).RunAsync(new CustomLoopOrderedRunRequest(seed.Id, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NotFound, missing.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, invalidState.Status);
        Assert.Empty(executor.Requests);
    }

    private static CustomLoopOrderedRunner Runner(FakeRunStore store, QueueExecutor executor, RecordingPublisher? publisher = null, RecordingAuditLog? audit = null)
    {
        return new CustomLoopOrderedRunner(store, new CustomLoopContextResolver(), executor, publisher ?? new RecordingPublisher(), audit ?? new RecordingAuditLog(), new FixedTimeProvider(Now));
    }

    private static CustomLoopDefinition Definition(
        CustomLoopInferenceStep[]? steps = null,
        int maxAdditionalIterations = 0,
        CustomLoopContextPolicy? exitPolicy = null,
        CustomLoopToolAssignment[]? tools = null)
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-ordered", "role-workspace", "step-only", "create-loop", Now);
        var definition = seed with
        {
            InferenceSteps = steps ?? [Step("step-only", "Only", "Do the work", Output(retain: false, publish: false))],
            ToolAssignments = tools ?? [],
            ExitPolicy = new CustomLoopExitPolicy(maxAdditionalIterations, CustomLoopDefinition.DefaultExitDecisionInstruction, exitPolicy is null ? CustomLoopNodeContextPolicy.Inherit() : CustomLoopNodeContextPolicy.Override(exitPolicy))
        };
        return CustomLoopDefinitionContentHash.Apply(definition with { ContentHash = string.Empty });
    }

    private static CustomLoopInferenceStep Step(string id, string name, string instruction, CustomLoopContextOutputPolicy output)
    {
        return new CustomLoopInferenceStep(id, name, instruction, CustomLoopNodeContextPolicy.Override(Policy(output)));
    }

    private static CustomLoopContextPolicy Policy(CustomLoopContextOutputPolicy output)
    {
        return new CustomLoopContextPolicy(new CustomLoopContextInputPolicy(true, true, false, true, true), output);
    }

    private static CustomLoopContextOutputPolicy Output(bool retain, bool publish)
    {
        return new CustomLoopContextOutputPolicy(retain, publish);
    }

    private static CustomLoopRunRecord Run(CustomLoopDefinition definition, CustomLoopConversationReference? conversation = null)
    {
        var admission = new CustomLoopRunEvent(1, "event-admitted", Now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var auditCompleted = new CustomLoopRunEvent(2, "event-admission-audit-complete", Now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null);
        var context = CustomLoopContextSnapshot.CreateEmpty(Now);
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            "run-ordered",
            definition.Id,
            2,
            CustomLoopRunStatus.Admitted,
            Now,
            Now,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            "invoke-operation",
            AuditSchema.Actors.Web,
            string.Empty,
            definition,
            "Initial user prompt",
            conversation,
            context,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admission, auditCompleted],
            null,
            null,
            null);
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static CustomLoopInferenceAttemptResult Result(string output, int toolCalls = 0)
    {
        return new CustomLoopInferenceAttemptResult(output, "provider", "model", $"response-{Guid.NewGuid():N}", toolCalls);
    }

    private static string Hash(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class FakeRunStore : ICustomLoopRunStore
    {
        public FakeRunStore(CustomLoopRunRecord current, bool validateSeed = true)
        {
            if (validateSeed)
            {
                Assert.True(CustomLoopRunValidator.Validate(current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(current).Errors));
            }

            Current = current;
        }

        public CustomLoopRunRecord Current { get; private set; }

        public bool ReturnMissing { get; init; }

        public Exception? GetException { get; init; }

        public Exception? AppendTerminalWarningException { get; init; }

        public Func<CustomLoopRunRecord, Task>? AfterUpdate { get; init; }

        public bool ConflictOnOutcomeWrite { get; init; }

        public bool ConcurrentNeedsReviewOnOutcomeConflict { get; init; }

        public bool ReturnMissingAfterOutcomeConflict { get; init; }

        public Exception? GetExceptionAfterOutcomeConflict { get; init; }

        public List<CustomLoopRunRecord> Writes { get; } = [];

        public List<CustomLoopValidationError> ValidationFailures { get; } = [];

        private bool OutcomeConflictInjected { get; set; }

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        {
            if (OutcomeConflictInjected && GetExceptionAfterOutcomeConflict is not null)
            {
                throw GetExceptionAfterOutcomeConflict;
            }

            if (GetException is not null)
            {
                throw GetException;
            }

            return Task.FromResult<CustomLoopRunRecord?>(ReturnMissing || (OutcomeConflictInjected && ReturnMissingAfterOutcomeConflict) ? null : Current);
        }

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CustomLoopRunRecord?>(null);
        }

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<CustomLoopRunRecord?>(Current.IsTerminal ? null : Current);
        }

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);
        }

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CustomLoopRunRecord> runs = Current.IsTerminal ? [] : [Current];
            return Task.FromResult(runs);
        }

        public Task<CustomLoopRunStoreResult> AppendTerminalIntegrityWarningAsync(string runId, int expectedLifecycleVersion, CustomLoopRunEvent warning, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            if (AppendTerminalWarningException is not null)
            {
                throw AppendTerminalWarningException;
            }

            if (Current.LifecycleVersion == expectedLifecycleVersion + 1 && Current.Events[^1] == warning)
            {
                return Task.FromResult(CustomLoopRunStoreResult.Updated(Current));
            }

            if (Current.LifecycleVersion != expectedLifecycleVersion)
            {
                return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion));
            }

            var validation = CustomLoopRunValidator.ValidateTerminalIntegrityWarningAppend(Current, warning);
            if (!validation.IsValid)
            {
                ValidationFailures.AddRange(validation.Errors);
                throw new FormatException("Terminal warning failed validation.");
            }

            Current = Current with { LifecycleVersion = Current.LifecycleVersion + 1, UpdatedAtUtc = warning.TimestampUtc, Events = [.. Current.Events, warning] };
            Writes.Add(Current);
            return Task.FromResult(CustomLoopRunStoreResult.Updated(Current));
        }

        public async Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            if (ConflictOnOutcomeWrite && !OutcomeConflictInjected && run.Events.Skip(Current.Events.Length).Any(item => item.Kind == CustomLoopRunEventKind.NodeOutcomeObserved))
            {
                OutcomeConflictInjected = true;
                var concurrentDetail = ConcurrentNeedsReviewOnOutcomeConflict
                    ? "A concurrent controller required review after provider dispatch."
                    : "A concurrent controller requested pause after provider dispatch.";
                var concurrentEvent = new CustomLoopRunEvent(Current.Events.Length + 1, "event-concurrent-control", Now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, concurrentDetail, [], null, null, null, null, null, null, null, null, null, null);
                var concurrent = ConcurrentNeedsReviewOnOutcomeConflict
                    ? Current with
                    {
                        LifecycleVersion = Current.LifecycleVersion + 1,
                        Status = CustomLoopRunStatus.NeedsReview,
                        UpdatedAtUtc = Now,
                        CompletedAtUtc = Now,
                        ExecutionClock = new CustomLoopExecutionClock(Current.ExecutionClock.AccumulatedRunningMilliseconds, null),
                        Events = [.. Current.Events, concurrentEvent],
                        FailureCode = "concurrent_review",
                        FailureDetail = concurrentDetail
                    }
                    : Current with
                    {
                        LifecycleVersion = Current.LifecycleVersion + 1,
                        Status = CustomLoopRunStatus.PauseRequested,
                        UpdatedAtUtc = Now,
                        Events = [.. Current.Events, concurrentEvent]
                    };
                var concurrentValidation = CustomLoopRunValidator.ValidateUpdate(Current, concurrent);
                Assert.True(concurrentValidation.IsValid, string.Join(Environment.NewLine, concurrentValidation.Errors));
                Current = concurrent;
                Writes.Add(concurrent);
                return CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion);
            }

            if (Current.LifecycleVersion != expectedLifecycleVersion)
            {
                return CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion);
            }

            var validation = CustomLoopRunValidator.ValidateUpdate(Current, run);
            if (!validation.IsValid)
            {
                ValidationFailures.AddRange(validation.Errors);
                throw new FormatException("Candidate run failed validation.");
            }
            Current = run;
            Writes.Add(run);
            if (AfterUpdate is not null)
            {
                await AfterUpdate(run);
            }

            return CustomLoopRunStoreResult.Updated(run);
        }
    }

    private sealed class QueueExecutor : ICustomLoopInferenceAttemptExecutor
    {
        private readonly Queue<object> _outcomes;

        public QueueExecutor(params object[] outcomes)
        {
            _outcomes = new Queue<object>(outcomes);
        }

        public List<CustomLoopInferenceAttemptRequest> Requests { get; } = [];

        public Func<CustomLoopInferenceAttemptRequest, Task>? BeforeExecute { get; set; }

        public Func<CustomLoopInferenceAttemptRequest, Task>? AfterExecute { get; set; }

        public async Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (BeforeExecute is not null)
            {
                await BeforeExecute(request);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var outcome = _outcomes.Dequeue();
            if (outcome is Exception exception)
            {
                throw exception;
            }

            if (AfterExecute is not null)
            {
                await AfterExecute(request);
            }

            return (CustomLoopInferenceAttemptResult)outcome;
        }
    }

    private sealed class FakeControlOperationStore : ICustomLoopControlOperationStore
    {
        private readonly Dictionary<string, CustomLoopControlOperation> _operations = new(StringComparer.Ordinal);

        public Task<CustomLoopControlOperationStoreResult> BeginAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
        {
            if (_operations.TryGetValue(operation.OperationId, out var existing))
            {
                var status = string.Equals(existing.RequestHash, operation.RequestHash, StringComparison.Ordinal) ? CustomLoopControlOperationStoreStatus.Replayed : CustomLoopControlOperationStoreStatus.Conflict;
                return Task.FromResult(new CustomLoopControlOperationStoreResult(status, existing));
            }

            _operations.Add(operation.OperationId, operation);
            return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Created, operation));
        }

        public Task<CustomLoopControlOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _operations.TryGetValue(operationId, out var operation);
            return Task.FromResult(operation);
        }

        public Task<CustomLoopControlOperationStoreResult> CompleteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
        {
            if (!_operations.TryGetValue(operation.OperationId, out var existing))
            {
                return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.NotFound, null));
            }

            if (!string.Equals(existing.RequestHash, operation.RequestHash, StringComparison.Ordinal))
            {
                return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing));
            }

            if (existing.State == CustomLoopControlOperationState.Complete)
            {
                return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Replayed, existing));
            }

            _operations[operation.OperationId] = operation;
            return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Completed, operation));
        }
    }

    private sealed class AvailableModel : ICustomLoopModelAvailability
    {
        public Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingPublisher : ICustomLoopConversationPublisher
    {
        public List<CustomLoopConversationPublicationRequest> Requests { get; } = [];

        public Func<CustomLoopConversationPublicationRequest, Task>? BeforePublish { get; set; }

        public CustomLoopConversationPublicationResult NextResult { get; set; } = new(CustomLoopConversationPublicationOutcome.Published, "publication-one", "Published.");

        public async Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            Requests.Add(request);
            if (BeforePublish is not null)
            {
                await BeforePublish(request);
            }

            return NextResult;
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Func<AuditEvent, bool>? FailPredicate { get; init; }

        public Action<AuditEvent, CancellationToken>? BeforeAppend { get; init; }

        public Func<AuditEvent, Task>? AfterAppend { get; set; }

        public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (FailPredicate?.Invoke(auditEvent) == true)
            {
                throw new IOException("Audit unavailable.");
            }

            BeforeAppend?.Invoke(auditEvent, cancellationToken);
            Assert.False(cancellationToken.IsCancellationRequested);
            Events.Add(auditEvent);
            if (AfterAppend is not null)
            {
                await AfterAppend(auditEvent);
            }
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
        }
    }
}
