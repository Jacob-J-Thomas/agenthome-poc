using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Failures;
using EmbodySense.Core.Application.Loops.Failures.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.HumanInput.Policies;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

/// <summary>Exercises Human Input admission through the canonical ordered-runner boundary.</summary>
public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Human_input_admission_atomically_persists_the_waiting_frontier_and_published_checkpoint_before_any_delivery()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        CustomLoopRunRecord? observedCheckpointCandidate = null;
        store.BeforeUpdate = (candidate, _) =>
        {
            if (candidate.HumanInputWaitingCheckpoints.Count != 1)
            {
                return;
            }

            observedCheckpointCandidate = candidate;
            var checkpoint = Assert.Single(candidate.HumanInputWaitingCheckpoints);
            var frontier = Assert.IsType<GovernedLoopFrontierPosture>(candidate.Frontier);
            var activation = frontier.Payload.Nodes[checkpoint.Binding.ActivationOrdinal];
            Assert.Equal(CustomLoopRunStatus.Waiting, candidate.Status);
            Assert.Equal(GovernedLoopFrontierStatus.Waiting, frontier.Payload.Status);
            Assert.Equal(GovernedLoopNodeExecutionStatus.Waiting, activation.Status);
            Assert.Equal(checkpoint.Binding.FrontierVersion, frontier.Payload.FrontierVersion);
            Assert.Equal(checkpoint.Binding.FrontierHash, frontier.Payload.ContentHash);
            Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, checkpoint.Posture);
            Assert.Equal(GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, Assert.Single(checkpoint.Evidence).Kind);
            Assert.Empty(executor.Requests);
            Assert.Empty(publisher.Requests);
        };

        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));
        var parked = await runtime.RunAsync(Request(context));

        Assert.True(parked.Status == CustomLoopOrderedRunStatus.Waiting, parked.Detail);
        Assert.NotNull(observedCheckpointCandidate);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        var checkpoint = Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(context.Anchor.AdapterBinding.ExecutionBinding.RunId, checkpoint.Binding.Execution.RunId);
        Assert.Equal(context.Anchor.AdapterBinding.ExecutionBinding.ExecutionGeneration, checkpoint.Binding.Execution.ExecutionGeneration);
        Assert.Equal(context.Anchor.AdapterBinding.ExecutionBinding.Revision, checkpoint.Binding.Execution.Revision);
        Assert.Equal(context.Anchor.AdapterBinding.AdmissionReceiptHash, checkpoint.Binding.AdmissionReceiptHash);
        Assert.True(ContextualRoleWorkspaceId.IsValid(context.Anchor.AdapterBinding.WorkspaceId));
        Assert.Equal(context.Anchor.AdapterBinding.WorkspaceId, checkpoint.Binding.WorkspaceId);
        Assert.Equal(context.Anchor.AdapterBinding.WorkspaceId, checkpoint.Request.Binding.WorkspaceId);
        Assert.Equal(context.Anchor.AdapterBinding.WorkspaceId, checkpoint.ResolvedPolicy.WorkspaceId);
        Assert.Equal("human-input", checkpoint.Binding.NodeId);
        Assert.Equal(context.Run.AdmissionActor, checkpoint.ResolvedPolicy.ActorId);
        Assert.Equal("timeout-policy-one@revision-one", checkpoint.ResolvedPolicy.TimeoutPolicy.Reference.ToString());
        Assert.Equal("failure-policy-one@revision-one", checkpoint.ResolvedPolicy.FailurePolicy.Reference.ToString());
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);

        var writesBeforeReplay = store.Writes.Count;
        var replay = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, replay.Status);
        Assert.Equal(writesBeforeReplay, store.Writes.Count);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
    }

    [Fact]
    public async Task Human_input_admission_reconciles_an_uncertain_post_commit_checkpoint_write_without_republication()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var threwAfterCommit = false;
        store.AfterUpdate = run =>
        {
            if (!threwAfterCommit && run.HumanInputWaitingCheckpoints.Count == 1)
            {
                threwAfterCommit = true;
                throw new InvalidOperationException("The checkpoint write committed before the client observed its acknowledgement.");
            }

            return Task.CompletedTask;
        };

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.True(threwAfterCommit);
        Assert.True(result.Status == CustomLoopOrderedRunStatus.Waiting, result.Detail);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Single(store.Writes, run => run.HumanInputWaitingCheckpoints.Count == 1);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_reconciles_a_conflict_when_the_exact_checkpoint_is_already_durable()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run)
        {
            RawConflictSuccessorFactory = (_, candidate) => candidate.HumanInputWaitingCheckpoints.Count == 1 ? candidate : null,
        };
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Waiting, result.Detail);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Single(store.Writes, run => run.HumanInputWaitingCheckpoints.Count == 1);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_does_not_assume_an_uncertain_checkpoint_write_when_exact_reconciliation_is_unavailable()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run)
        {
            GetException = new IOException("The durable checkpoint cannot be re-read."),
        };
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        store.AfterUpdate = run => run.HumanInputWaitingCheckpoints.Count == 1
            ? throw new IOException("The checkpoint acknowledgement was lost after persistence.")
            : Task.CompletedTask;

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_when_a_post_commit_checkpoint_acknowledgement_cannot_be_reconciled()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var postCommitAcknowledgementLost = false;
        store.AfterUpdate = run =>
        {
            if (!postCommitAcknowledgementLost && run.HumanInputWaitingCheckpoints.Count == 1)
            {
                postCommitAcknowledgementLost = true;
                store.GetException = new IOException("The committed checkpoint cannot be read for acknowledgement reconciliation.");
                throw new IOException("The checkpoint acknowledgement was lost after the durable write.");
            }

            return Task.CompletedTask;
        };

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.True(postCommitAcknowledgementLost);
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_reconciles_a_durable_checkpoint_superseded_by_cancellation_without_delivery()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run)
        {
            RawConflictSuccessorFactory = (_, candidate) => candidate.HumanInputWaitingCheckpoints.Count == 1
                ? CreatePureCancellationTerminalSuccessor(candidate, includePause: false, includeTerminalWarning: false)
                : null,
        };
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, store.Current.Status);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_when_exact_policy_resolution_source_throws()
    {
        var context = await HumanInputContextAsync();
        var source = new HumanInputPolicyResolutionTestSource
        {
            BeforeRead = (_, _) => throw new IOException("The exact policy source is unavailable."),
        };
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context, source: source)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_policy_resolution_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_propagates_cancellation_during_exact_policy_resolution_without_checkpoint_or_delivery()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();

        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Empty(interrupted.Store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_propagates_cancellation_while_parking_an_interrupted_human_input_admission()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();
        var writesBeforeRecovery = interrupted.Store.Writes.Count;
        CustomLoopRunRecord? attemptedRecovery = null;
        using var cancellation = new CancellationTokenSource();
        interrupted.Store.BeforeUpdate = (candidate, _) =>
        {
            attemptedRecovery = candidate;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        var recovery = new CustomLoopRecoveryService(
            interrupted.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.Store.Current.UpdatedAtUtc.AddSeconds(1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecoverAsync(AuditSchema.Actors.Web, cancellation.Token));

        AssertInterruptedHumanInputRecoveryCandidate(attemptedRecovery);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Equal(writesBeforeRecovery, interrupted.Store.Writes.Count);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_fails_closed_when_parking_an_interrupted_human_input_admission_cannot_be_persisted()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();
        var writesBeforeRecovery = interrupted.Store.Writes.Count;
        CustomLoopRunRecord? attemptedRecovery = null;
        interrupted.Store.BeforeUpdate = (candidate, _) =>
        {
            attemptedRecovery = candidate;
            throw new IOException("The recovery write is unavailable.");
        };

        var result = Assert.Single(await new CustomLoopRecoveryService(
            interrupted.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.Store.Current.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, result.Status);
        AssertInterruptedHumanInputRecoveryCandidate(attemptedRecovery);
        Assert.Same(interrupted.Store.Current, result.Run);
        Assert.Contains("recovery transition failed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Equal(writesBeforeRecovery, interrupted.Store.Writes.Count);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_reports_conflict_when_an_interrupted_human_input_admission_changes_and_cannot_be_reloaded()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();
        var writesBeforeRecovery = interrupted.Store.Writes.Count;
        CustomLoopRunRecord? attemptedRecovery = null;
        interrupted.Store.RawConflictSuccessorFactory = (current, candidate) =>
        {
            attemptedRecovery = candidate;
            return current;
        };
        interrupted.Store.GetException = new IOException("The concurrent recovery state cannot be reloaded.");

        var result = Assert.Single(await new CustomLoopRecoveryService(
            interrupted.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.Store.Current.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Conflict, result.Status);
        AssertInterruptedHumanInputRecoveryCandidate(attemptedRecovery);
        Assert.Same(interrupted.Store.Current, result.Run);
        Assert.Contains("changed concurrently", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Equal(writesBeforeRecovery + 1, interrupted.Store.Writes.Count);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_fails_closed_when_parking_an_interrupted_human_input_admission_is_rejected()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();
        var writesBeforeRecovery = interrupted.Store.Writes.Count;
        CustomLoopRunRecord? attemptedRecovery = null;
        interrupted.Store.BeforeUpdate = (candidate, _) => attemptedRecovery = candidate;
        interrupted.Store.UpdateResultOverride = CustomLoopRunStoreResult.NotFound();

        var result = Assert.Single(await new CustomLoopRecoveryService(
            interrupted.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.Store.Current.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, result.Status);
        AssertInterruptedHumanInputRecoveryCandidate(attemptedRecovery);
        Assert.Same(interrupted.Store.Current, result.Run);
        Assert.Contains("transition was rejected", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Equal(writesBeforeRecovery, interrupted.Store.Writes.Count);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_when_trusted_policy_resolution_time_is_unavailable()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context, timeProvider: new FixedTimeProvider(default))).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_policy_resolution_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_when_the_trusted_policy_clock_throws()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context, timeProvider: new ThrowingEffectAuthorityTimeProvider())).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_policy_resolution_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_never_persists_a_frontier_before_its_trusted_policy_resolution_time()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var resolutionAtUtc = _now.AddMinutes(1);

        var result = await Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context, resolutionAtUtc),
            new FixedTimeProvider(_now)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, result.Status);
        Assert.Equal(resolutionAtUtc, store.Current.UpdatedAtUtc);
        var checkpoint = Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(resolutionAtUtc, checkpoint.ResolvedPolicy.ResolvedAtUtc);
        Assert.Equal(resolutionAtUtc, checkpoint.Request.Timing.RequestedAtUtc);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_when_the_exact_policy_authority_is_unavailable()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(context, store, executor, publisher, null).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_policy_resolution_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Human_input_admission_fails_closed_for_missing_or_stale_exact_policy_revisions(bool staleRevision)
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var source = new HumanInputPolicyResolutionTestSource();
        if (staleRevision)
        {
            var binding = context.Anchor.AdapterBinding;
            source.Results.Add(
                new HumanInputPolicyReference("timeout-policy-one", "revision-one"),
                new HumanInputPolicySourceReadResult(
                    HumanInputPolicySourceReadStatus.Ready,
                    HumanInputPolicyArtifactHash.Apply(TimeoutPolicy(binding, context.Run.AdmissionActor) with
                    {
                        RevisionId = "revision-two",
                        ContentHash = string.Empty,
                    }),
                    1));
            source.Results.Add(
                new HumanInputPolicyReference("failure-policy-one", "revision-one"),
                new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, FailurePolicy(binding, context.Run.AdmissionActor), 1));
        }

        var result = await Runtime(context, store, new QueueExecutor(), new RecordingPublisher(), new HumanInputPolicyResolutionService(source, new FixedTimeProvider(_now))).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(staleRevision ? "human_input_policy_resolution_divergent" : "human_input_policy_resolution_notfound", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.DoesNotContain(store.Writes, run => run.HumanInputWaitingCheckpoints.Count > 0);
    }

    [Fact]
    public async Task Human_input_terminal_resume_rehydrates_the_exact_response_for_a_non_retaining_condition_without_leaking_it_into_continuation_evidence()
    {
        const string PrivateResponse = "response-private-condition-token";
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value));
        var runtime = Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context),
            audit: audit,
            humanInputBindingSource: source);

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);

        var resumed = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal(1, source.ReadCount);
        var condition = Assert.Single(store.Current.Frontier!.Payload.Nodes, node => node.NodeId == "response-condition");
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, condition.Status);
        Assert.Equal(GovernedLoopControlCondition.False, condition.ControlOutcome);
        Assert.Empty(executor.Requests);
        Assert.DoesNotContain(PrivateResponse, Assert.Single(store.Current.HumanInputWaitingCheckpoints).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateResponse, source.LastBinding!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateResponse, store.Current.Events.Select(item => item.Detail ?? string.Empty));
        Assert.DoesNotContain(PrivateResponse, audit.Events.Select(item => item.Detail));
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_resume_leaves_the_durable_run_unchanged_for_binding_source_unavailability_then_rereads_on_the_next_entry()
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value)) { IsUnavailable = true };
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writesBeforeUnavailable = store.Writes.Count;
        var unavailable = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, unavailable.Status);
        Assert.Same(terminal.Run, store.Current);
        Assert.Equal(writesBeforeUnavailable, store.Writes.Count);
        Assert.Equal(1, source.ReadCount);
        source.IsUnavailable = false;

        var recovered = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, recovered.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal(2, source.ReadCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Human_input_terminal_condition_escalates_to_review_when_the_exact_binding_is_invalid_or_malformed(bool malformed)
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value))
        {
            IsInvalid = !malformed,
            ReturnMalformedReady = malformed,
        };
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);

        var result = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_binding_invalid", store.Current.FailureCode);
        Assert.Equal(1, source.ReadCount);
        Assert.Null(source.LastBinding);
        Assert.DoesNotContain("response-private-condition-token", store.Current.Events.Select(item => item.Detail ?? string.Empty));
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_condition_leaves_the_run_unchanged_when_the_binding_source_throws()
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value))
        {
            ResolveException = new IOException("simulated response-store outage"),
        };
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writesBeforeResolution = store.Writes.Count;

        var result = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Same(terminal.Run, store.Current);
        Assert.Equal(writesBeforeResolution, store.Writes.Count);
        Assert.Equal(1, source.ReadCount);
        Assert.Null(source.LastBinding);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_condition_propagates_caller_cancellation_from_the_exact_binding_source()
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        using var cancellation = new CancellationTokenSource();
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value))
        {
            BeforeResolve = cancellation.Cancel,
            ResolveException = new OperationCanceledException("simulated caller cancellation", cancellation.Token),
        };
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writesBeforeResolution = store.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ResumeHumanInputAsync(
            new GovernedLoopSequentialOrderedHumanInputResumeRequest(
                GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
                context.Anchor,
                context.Plan,
                context.Artifact,
                terminal.Checkpoint.Binding.CheckpointId,
                terminal.ReceiptHash,
                terminal.Run.AdmissionActor),
            cancellation.Token));

        Assert.Same(terminal.Run, store.Current);
        Assert.Equal(writesBeforeResolution, store.Writes.Count);
        Assert.Equal(1, source.ReadCount);
        Assert.Null(source.LastBinding);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_condition_leaves_the_run_unchanged_without_a_configured_binding_source()
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writesBeforeResolution = store.Writes.Count;

        var result = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Same(terminal.Run, store.Current);
        Assert.Equal(writesBeforeResolution, store.Writes.Count);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_resume_reenters_the_canonical_ordered_runner_and_completes_the_exit()
    {
        var context = await HumanInputSuccessFallbackContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        var terminalValidation = CustomLoopRunValidator.ValidateUpdate(store.Current, terminal.Run);
        Assert.True(terminalValidation.IsValid, string.Join(Environment.NewLine, terminalValidation.Errors));
        store.ReplaceCurrent(terminal.Run);

        var resumed = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.True(resumed.Status == CustomLoopOrderedRunStatus.Completed, $"{resumed.Status}: {resumed.Detail}; {store.Current.FailureCode}: {store.Current.FailureDetail}");
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Completed, store.Current.Frontier?.Payload.Status);
        Assert.Empty(executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_resume_rejects_a_receipt_mismatch_without_ordered_dispatch()
    {
        var context = await HumanInputSuccessFallbackContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writes = store.Writes.Count;

        var resumed = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            new string('c', 64),
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, resumed.Status);
        Assert.Equal(writes, store.Writes.Count);
        Assert.Empty(executor.Requests);
        Assert.Equal(CustomLoopRunStatus.Running, store.Current.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_failure_resume_reenters_the_canonical_ordered_runner_on_the_routed_failure_edge()
    {
        var context = await HumanInputFailureContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var retirement = RejectHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        Assert.True(CustomLoopRunValidator.ValidateUpdate(store.Current, retirement.Run).IsValid);
        store.ReplaceCurrent(retirement.Run);

        var resumed = await runtime.ResumeHumanInputFailureAsync(new GovernedLoopSequentialOrderedHumanInputFailureResumeRequest(
            GovernedLoopSequentialOrderedHumanInputFailureResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            retirement.Checkpoint.Binding.CheckpointId,
            retirement.RetirementEvidenceHash,
            retirement.EventId,
            retirement.FailureEvidenceHash,
            retirement.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Failed, store.Current.Frontier?.Payload.Status);
        Assert.Empty(executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_failure_resume_rejects_a_failure_evidence_mismatch_without_ordered_dispatch()
    {
        var context = await HumanInputFailureContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var retirement = RejectHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(retirement.Run);
        var writes = store.Writes.Count;

        var resumed = await runtime.ResumeHumanInputFailureAsync(new GovernedLoopSequentialOrderedHumanInputFailureResumeRequest(
            GovernedLoopSequentialOrderedHumanInputFailureResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            retirement.Checkpoint.Binding.CheckpointId,
            retirement.RetirementEvidenceHash,
            retirement.EventId,
            new string('c', 64),
            retirement.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, resumed.Status);
        Assert.Equal(writes, store.Writes.Count);
        Assert.Empty(executor.Requests);
        Assert.Equal(CustomLoopRunStatus.Running, store.Current.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Restart_recovery_preserves_an_exact_pending_human_input_checkpoint_without_delivery_or_continuation()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var retained = store.Current;
        var checkpoint = Assert.Single(retained.HumanInputWaitingCheckpoints);
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(retained.Frontier);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var recovery = Assert.Single(await new CustomLoopRecoveryService(
            store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.True(recovery.Status == CustomLoopRecoveryStatus.Unchanged, recovery.Detail);
        Assert.Same(retained, store.Current);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Equal(checkpoint.CheckpointHash, Assert.Single(store.Current.HumanInputWaitingCheckpoints).CheckpointHash);
        Assert.Equal(frontier.Payload.FrontierVersion, store.Current.Frontier!.Payload.FrontierVersion);
        Assert.Equal(frontier.Payload.ContentHash, store.Current.Frontier.Payload.ContentHash);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_recovery_fails_closed_for_malformed_or_future_divergent_pending_human_input_checkpoint(bool futureDivergence)
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var malformed = MutateHumanInputWaitingCheckpoint(store.Current, futureDivergence);
        store.ReplaceCurrent(malformed, validate: false);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.False(CustomLoopRunValidator.Validate(store.Current).IsValid);
        var recovery = Assert.Single(await new CustomLoopRecoveryService(
            store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, recovery.Status);
        Assert.Same(malformed, store.Current);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_preserves_parallel_pending_human_input_checkpoints_across_aggregate_frontier_evolution()
    {
        var parked = await ParkParallelHumanInputAsync();
        var retained = parked.Store.Current;
        var checkpoints = retained.HumanInputWaitingCheckpoints.OrderBy(checkpoint => checkpoint.Binding.FrontierVersion).ToArray();
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(retained.Frontier);

        Assert.Equal(2, checkpoints.Length);
        Assert.True(checkpoints[0].Binding.FrontierVersion < checkpoints[1].Binding.FrontierVersion);
        Assert.True(checkpoints[1].Binding.FrontierVersion == frontier.Payload.FrontierVersion);
        Assert.NotEqual(frontier.Payload.ContentHash, checkpoints[0].Binding.FrontierHash);
        var inferenceDispatches = parked.Executor.Requests.Count;
        var recovery = Assert.Single(await new CustomLoopRecoveryService(
            parked.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.True(recovery.Status == CustomLoopRecoveryStatus.Unchanged, recovery.Detail);
        Assert.Same(retained, parked.Store.Current);
        Assert.Equal(checkpoints.Select(checkpoint => checkpoint.CheckpointHash), parked.Store.Current.HumanInputWaitingCheckpoints.Select(checkpoint => checkpoint.CheckpointHash));
        Assert.Equal(inferenceDispatches, parked.Executor.Requests.Count);
        Assert.Empty(parked.Publisher.Requests);
    }

    [Fact]
    public async Task Pause_and_resume_rearm_an_exact_pending_human_input_checkpoint_without_ordered_dispatch()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var operationStore = new FakeControlOperationStore();
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(store.Current);
        var service = new CustomLoopLifecycleService(
            store,
            operationStore,
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            "pause-durable-human-input-before-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var checkpoint = Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(store.Current.Frontier);

        var request = new CustomLoopResumeRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            "resume-durable-human-input",
            AuditSchema.Actors.Web);
        var resumed = await service.ResumeAsync(request);
        var replayed = await service.ResumeAsync(request);

        Assert.Equal(CustomLoopControlStatus.Waiting, resumed.Status);
        Assert.Equal(CustomLoopControlStatus.Waiting, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Waiting, store.Current.Frontier!.Payload.Status);
        Assert.Equal(checkpoint.CheckpointHash, Assert.Single(store.Current.HumanInputWaitingCheckpoints).CheckpointHash);
        Assert.Equal(frontier.Payload.FrontierVersion, store.Current.Frontier.Payload.FrontierVersion);
        Assert.Equal(frontier.Payload.ContentHash, store.Current.Frontier.Payload.ContentHash);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_rejects_a_malformed_or_future_divergent_pending_human_input_checkpoint_without_ordered_dispatch(bool futureDivergence)
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(store.Current);
        var service = new CustomLoopLifecycleService(
            store,
            new FakeControlOperationStore(),
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            "pause-human-input-before-invalid-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var malformed = MutateHumanInputWaitingCheckpoint(store.Current, futureDivergence);
        store.ReplaceCurrent(malformed, validate: false);
        var writesBeforeResume = store.Writes.Count;

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            futureDivergence ? "resume-future-human-input" : "resume-malformed-human-input",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.InvalidState, result.Status);
        Assert.Same(malformed, store.Current);
        Assert.Equal(writesBeforeResume, store.Writes.Count);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Resume_rejects_a_paused_human_input_checkpoint_with_a_malformed_unconverged_expired_posture_without_ordered_dispatch()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(store.Current);
        var service = new CustomLoopLifecycleService(
            store,
            new FakeControlOperationStore(),
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            "pause-before-expired-human-input-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var expired = ExpireHumanInputWaitingCheckpoint(store.Current);
        Assert.False(CustomLoopRunValidator.Validate(expired).IsValid);
        store.ReplaceCurrent(expired, validate: false);
        var writesBeforeResume = store.Writes.Count;

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(
            expired.Id,
            expired.LifecycleVersion,
            "resume-expired-human-input",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.InvalidState, result.Status);
        Assert.Same(expired, store.Current);
        Assert.Equal(writesBeforeResume, store.Writes.Count);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Theory]
    [InlineData(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, CustomLoopControlStatus.WorkspaceHostUnavailable, 0)]
    [InlineData(CustomLoopExecutionLeaseStatus.WorkspaceBusy, CustomLoopControlStatus.WorkspaceExecutionBusy, 1)]
    [InlineData(CustomLoopExecutionLeaseStatus.OperationInProgress, CustomLoopControlStatus.OperationInProgress, 1)]
    [InlineData(CustomLoopExecutionLeaseStatus.OperationConflict, CustomLoopControlStatus.Conflict, 1)]
    public async Task Resume_fails_closed_when_a_paused_human_input_checkpoint_cannot_reacquire_execution_ownership(
        CustomLoopExecutionLeaseStatus leaseStatus,
        CustomLoopControlStatus expectedStatus,
        int expectedAcquisitionCount)
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(store.Current);
        var gate = new TestExecutionGate(leaseStatus);
        var service = new CustomLoopLifecycleService(
            store,
            new FakeControlOperationStore(),
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            gate,
            new FixedTimeProvider(_now.AddSeconds(1)));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            $"pause-before-{leaseStatus.ToString().ToLowerInvariant()}-human-input-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var paused = store.Current;
        var writesBeforeResume = store.Writes.Count;

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(
            paused.Id,
            paused.LifecycleVersion,
            $"resume-{leaseStatus.ToString().ToLowerInvariant()}-human-input",
            AuditSchema.Actors.Web));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Same(paused, store.Current);
        Assert.Equal(writesBeforeResume, store.Writes.Count);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(expectedAcquisitionCount, gate.AcquisitionCount);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Pause_and_resume_rearm_parallel_pending_human_input_checkpoints_across_aggregate_frontier_evolution()
    {
        var parked = await ParkParallelHumanInputAsync();
        var operationStore = new FakeControlOperationStore();
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(parked.Store.Current);
        var service = new CustomLoopLifecycleService(
            parked.Store,
            operationStore,
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));

        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            parked.Store.Current.Id,
            parked.Store.Current.LifecycleVersion,
            "pause-parallel-human-input-before-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var checkpoints = parked.Store.Current.HumanInputWaitingCheckpoints.OrderBy(checkpoint => checkpoint.Binding.FrontierVersion).ToArray();
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(parked.Store.Current.Frontier);
        Assert.NotEqual(frontier.Payload.ContentHash, checkpoints[0].Binding.FrontierHash);
        var inferenceDispatches = parked.Executor.Requests.Count;

        var resumed = await service.ResumeAsync(new CustomLoopResumeRequest(
            parked.Store.Current.Id,
            parked.Store.Current.LifecycleVersion,
            "resume-parallel-human-input",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.Waiting, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, parked.Store.Current.Status);
        Assert.Equal(checkpoints.Select(checkpoint => checkpoint.CheckpointHash), parked.Store.Current.HumanInputWaitingCheckpoints.Select(checkpoint => checkpoint.CheckpointHash));
        Assert.Equal(frontier.Payload.FrontierVersion, parked.Store.Current.Frontier!.Payload.FrontierVersion);
        Assert.Equal(frontier.Payload.ContentHash, parked.Store.Current.Frontier.Payload.ContentHash);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Equal(inferenceDispatches, parked.Executor.Requests.Count);
        Assert.Empty(parked.Publisher.Requests);
        Assert.True(CustomLoopRunValidator.Validate(parked.Store.Current).IsValid);
    }

    private static CustomLoopRunRecord MutateHumanInputWaitingCheckpoint(CustomLoopRunRecord run, bool futureDivergence)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        if (!futureDivergence)
        {
            return run with { HumanInputWaitingCheckpoints = [checkpoint with { CheckpointHash = new string('F', 64) }] };
        }

        var futureBinding = checkpoint.Binding with
        {
            FrontierVersion = checkpoint.Binding.FrontierVersion + 1,
            FrontierHash = new string('a', 64),
        };
        var futureCheckpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            futureBinding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            checkpoint.Posture,
            checkpoint.Evidence,
            string.Empty));
        return run with { HumanInputWaitingCheckpoints = [futureCheckpoint] };
    }

    private static (CustomLoopRunRecord Run, GovernedLoopHumanInputWaitingCheckpoint Checkpoint, string ReceiptHash) CompleteHumanInputCheckpointForOrderedReentry(
        CustomLoopRunRecord run,
        SequentialTestContext context,
        DateTimeOffset completedAtUtc)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        var activation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal));
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(context.Plan.Nodes.ElementAtOrDefault(activation.PlanOrdinal));
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, checkpoint.Posture);
        var receiptHash = new string('b', 64);
        var terminalEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized,
            completedAtUtc,
            null,
            null,
            null,
            "human-input-ordered-reentry-receipt",
            receiptHash,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        var terminalCheckpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Terminal,
            [.. checkpoint.Evidence, terminalEvidence],
            string.Empty));
        var baseEvent = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 2,
            "human-input-ordered-reentry-terminal",
            completedAtUtc,
            CustomLoopRunEventKind.NodeAttemptCompleted,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "The exact accepted Human Input response terminalized before ordered re-entry.",
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
        var completed = GovernedLoopSequentialFrontierMachine.CompleteWaitingHumanInput(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            node,
            activation,
            activation.Attempt!.Value,
            activation.AttemptOperationId,
            baseEvent.EventId,
            CustomLoopSequentialOutcomeArtifactHash.Compute(baseEvent),
            completedAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, completed.Status);
        var terminalActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(completed.Frontier?.Payload.Nodes.ElementAtOrDefault(activation.ActivationOrdinal));
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            context.Anchor.AdapterBinding.WorkspaceId,
            context.Anchor.AdapterBinding.ExecutionBinding.RunId,
            context.Anchor.AdapterBinding.ExecutionBinding.Revision,
            context.Anchor.AdapterBinding.ExecutionBinding.ExecutionGeneration,
            terminalActivation.ActivationOrdinal,
            terminalActivation.VisitOrdinal,
            terminalActivation.NodeId,
            terminalActivation.Attempt,
            terminalActivation.CycleId,
            terminalActivation.CycleIteration,
            GovernedLoopControlCondition.Success,
            terminalActivation.SelectedControlEdgeIds,
            terminalActivation.SkippedControlEdgeIds,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Completed,
            CustomLoopSequentialOutcomeArtifactHash.Compute(baseEvent),
            string.Empty));
        var lifecycle = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 1,
            "human-input-ordered-reentry-frontier",
            completedAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "The exact accepted Human Input response advanced the canonical frontier before ordered re-entry.",
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
        var terminal = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = completedAtUtc,
            ExecutionClock = run.ExecutionClock with { ActiveSinceUtc = completedAtUtc },
            Frontier = completed.Frontier,
            Events = [.. run.Events, lifecycle, baseEvent with { SequentialNodeEvidence = evidence }],
            HumanInputWaitingCheckpoints = [terminalCheckpoint],
        };
        Assert.True(CustomLoopRunValidator.Validate(terminal).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(terminal).Errors));
        return (terminal, terminalCheckpoint, receiptHash);
    }

    private static CustomLoopRunRecord AnswerHumanInputCheckpointForOrderedReentry(CustomLoopRunRecord run, DateTimeOffset selectedAtUtc)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        var request = new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
        var response = new HumanInputResponseReference(
            HumanInputResponseReference.CurrentSchemaVersion,
            "human-input-ordered-reentry-response",
            request,
            new string('a', 64),
            new string('b', 64));
        var selection = HumanInputResponseSelectionReference.Create(HumanInputResponseSelectionHash.Apply(new HumanInputResponseSelection(
            HumanInputResponseSelection.CurrentSchemaVersion,
            "human-input-ordered-reentry-selection",
            request,
            HumanInputResponsePolicyKind.FirstValid,
            ImmutableArray.Create(response),
            null,
            null,
            selectedAtUtc,
            string.Empty)));
        var answeredEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered,
            selectedAtUtc,
            selection,
            null,
            null,
            null,
            null,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        var answered = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed,
            [.. checkpoint.Evidence, answeredEvidence],
            string.Empty));
        var candidate = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            UpdatedAtUtc = selectedAtUtc,
            HumanInputWaitingCheckpoints = [answered],
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(run, candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(run, candidate).Errors));
        return candidate;
    }

    private static (CustomLoopRunRecord Run, GovernedLoopHumanInputWaitingCheckpoint Checkpoint, string RetirementEvidenceHash, string EventId, string FailureEvidenceHash) RejectHumanInputCheckpointForOrderedReentry(
        CustomLoopRunRecord run,
        SequentialTestContext context,
        DateTimeOffset rejectedAtUtc)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        var activation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal));
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(context.Plan.Nodes.ElementAtOrDefault(activation.PlanOrdinal));
        const string LifecycleOperationId = "human-input-rejected-operation";
        var operationHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(LifecycleOperationId)));
        var eventId = "human-input-human-input-rejected-" + operationHash[..24];
        var baseEvent = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 2,
            eventId,
            rejectedAtUtc,
            CustomLoopRunEventKind.NodeAttemptFailed,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "The exact Human Input lifecycle operation rejected the request without an accepted selection.",
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
        var causal = new GovernedLoopFailureEvidenceReference(LifecycleOperationId, checkpoint.Request.RequestHash);
        var classified = new GovernedLoopFailureClassifier().Classify(
            new GovernedLoopFailureClassificationContext(
                "failure-human-input-rejected",
                context.Anchor.AdapterBinding.WorkspaceId,
                context.Anchor.AdapterBinding.ExecutionBinding.RunId,
                context.Anchor.AdapterBinding.ExecutionBinding.Revision,
                context.Anchor.AdapterBinding.ExecutionBinding.ExecutionGeneration,
                activation.ActivationOrdinal,
                activation.VisitOrdinal,
                activation.NodeId,
                activation.Attempt!.Value,
                causal),
            [new GovernedLoopFailureObservation(
                GovernedLoopFailureObservationKind.TerminalFailure,
                GovernedLoopFailureSource.User,
                "human-input-rejected",
                causal)],
            rejectedAtUtc);
        Assert.Equal(GovernedLoopFailureClassificationStatus.Classified, classified.Status);
        var failure = Assert.IsType<GovernedLoopFailureEvidence>(classified.Evidence);
        var classifiedEvent = baseEvent with { FailureEvidence = failure };
        var failed = GovernedLoopSequentialFrontierMachine.FailWaiting(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            node,
            activation,
            activation.Attempt.Value,
            activation.AttemptOperationId,
            classifiedEvent.EventId,
            CustomLoopSequentialOutcomeArtifactHash.Compute(classifiedEvent),
            GovernedLoopControlCondition.Failure,
            rejectedAtUtc);
        Assert.True(failed.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied, failed.Detail);
        var failedActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(failed.Frontier?.Payload.Nodes.ElementAtOrDefault(activation.ActivationOrdinal));
        var sequentialEvidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            context.Anchor.AdapterBinding.WorkspaceId,
            context.Anchor.AdapterBinding.ExecutionBinding.RunId,
            context.Anchor.AdapterBinding.ExecutionBinding.Revision,
            context.Anchor.AdapterBinding.ExecutionBinding.ExecutionGeneration,
            failedActivation.ActivationOrdinal,
            failedActivation.VisitOrdinal,
            failedActivation.NodeId,
            failedActivation.Attempt,
            failedActivation.CycleId,
            failedActivation.CycleIteration,
            GovernedLoopControlCondition.Failure,
            failedActivation.SelectedControlEdgeIds,
            failedActivation.SkippedControlEdgeIds,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Rejected,
            CustomLoopSequentialOutcomeArtifactHash.Compute(classifiedEvent),
            string.Empty)
        {
            FailureEvidenceId = failure.EvidenceId,
            FailureEvidenceHash = failure.ContentHash,
        });
        var retirementEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected,
            rejectedAtUtc,
            null,
            null,
            null,
            null,
            null,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        var retiredCheckpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Rejected,
            [.. checkpoint.Evidence, retirementEvidence],
            string.Empty));
        var lifecycle = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 1,
            "human-input-rejected-frontier",
            rejectedAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "The exact Human Input no-response disposition advanced the canonical frontier without an accepted response.",
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
        var retired = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = rejectedAtUtc,
            ExecutionClock = run.ExecutionClock with { ActiveSinceUtc = rejectedAtUtc },
            Frontier = failed.Frontier,
            Events = [.. run.Events, lifecycle, classifiedEvent with { SequentialNodeEvidence = sequentialEvidence }],
            HumanInputWaitingCheckpoints = [retiredCheckpoint],
        };
        Assert.True(CustomLoopRunValidator.Validate(retired).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(retired).Errors));
        return (retired, retiredCheckpoint, retirementEvidence.EvidenceHash, eventId, failure.ContentHash);
    }

    private static CustomLoopRunRecord ExpireHumanInputWaitingCheckpoint(CustomLoopRunRecord run)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        var expiredEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion,
            2,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired,
            checkpoint.Request.Timing.ExpiresAtUtc.AddTicks(1),
            null,
            null,
            null,
            null,
            null,
            Assert.Single(checkpoint.Evidence).EvidenceHash,
            string.Empty));
        var expired = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Expired,
            [.. checkpoint.Evidence, expiredEvidence],
            string.Empty));
        return run with { HumanInputWaitingCheckpoints = [expired] };
    }

    private static async Task<(FakeRunStore Store, QueueExecutor Executor, RecordingPublisher Publisher)> InterruptHumanInputDuringPolicyResolutionAsync()
    {
        var context = await HumanInputContextAsync();
        using var cancellation = new CancellationTokenSource();
        var source = new HumanInputPolicyResolutionTestSource
        {
            BeforeRead = (_, _) => cancellation.Cancel(),
        };
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context, source: source)).RunAsync(Request(context), cancellation.Token));
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));
        return (store, executor, publisher);
    }

    private static void AssertInterruptedHumanInputRecoveryCandidate(CustomLoopRunRecord? candidate)
    {
        var attempted = Assert.IsType<CustomLoopRunRecord>(candidate);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, attempted.Status);
        Assert.Equal("recovery_open_attempt", attempted.FailureCode);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, attempted.Frontier?.Payload.Status);
    }

    private static async Task<(FakeRunStore Store, QueueExecutor Executor, RecordingPublisher Publisher)> ParkParallelHumanInputAsync()
    {
        var context = await ParallelHumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("parallel Human Input source"));
        var publisher = new RecordingPublisher();

        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Equal(2, store.Current.HumanInputWaitingCheckpoints.Count);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        return (store, executor, publisher);
    }

    private static async Task<SequentialTestContext> HumanInputContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static async Task<SequentialTestContext> HumanInputFailureContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputFailureArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static async Task<SequentialTestContext> HumanInputSuccessFallbackContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputSuccessFallbackArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static async Task<SequentialTestContext> HumanInputConditionContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputConditionArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static async Task<SequentialTestContext> ParallelHumanInputContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = ParallelHumanInputArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static GovernedLoopSequentialOrderedRuntimeAdapter Runtime(
        SequentialTestContext context,
        FakeRunStore store,
        QueueExecutor executor,
        RecordingPublisher publisher,
        HumanInputPolicyResolutionService? resolver,
        TimeProvider? runnerTimeProvider = null,
        RecordingAuditLog? audit = null,
        IGovernedLoopSequentialHumanInputBindingSource? humanInputBindingSource = null)
    {
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        return new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                store,
                executor,
                publisher,
                audit,
                timeProvider: runnerTimeProvider ?? new FixedTimeProvider(_now),
                humanInputPolicyResolutionService: resolver,
                humanInputBindingSource: humanInputBindingSource),
            evidence,
            evidence);
    }

    private static GovernedLoopSequentialOrderedRunRequest Request(SequentialTestContext context)
        => new(GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web);

    private static GovernedLoopGraphRevisionArtifact HumanInputArtifact(ContextualRoleRevisionPin role)
    {
        var configuration = HumanInputConfiguration();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-input",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                    [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(),
                    null,
                    null,
                    null,
                    configuration),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-exit", "human-input", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            role,
            bindings: [new GovernedLoopBindingDefinition("response-to-exit", GovernedLoopBindingKind.Data, "human-input", GovernedLoopHumanInputVocabulary.ResponsePortId, "exit", "result")],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopGraphRevisionArtifact HumanInputFailureArtifact(ContextualRoleRevisionPin role)
    {
        var configuration = HumanInputConfiguration();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-input",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                    [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(),
                    null,
                    null,
                    null,
                    configuration),
                new GovernedLoopNodeDefinition(
                    "success-fallback",
                    GovernedLoopSequentialNodeDescriptors.IdentityTransform,
                    [
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                    ],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
                GovernedLoopSequentialApplicationTestFixture.Node("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-fallback-success", "human-input", "success-fallback", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-input-to-fail-failure", "human-input", "fail", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("fallback-to-exit", "success-fallback", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit", "fail"],
            role,
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-success-fallback", GovernedLoopBindingKind.Data, "trigger", "request", "success-fallback", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("success-fallback-to-exit", GovernedLoopBindingKind.Data, "success-fallback", GovernedLoopPureNodeVocabulary.OutputPort, "exit", "result"),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopGraphRevisionArtifact HumanInputSuccessFallbackArtifact(ContextualRoleRevisionPin role)
    {
        var configuration = HumanInputConfiguration();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-input",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                    [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(),
                    null,
                    null,
                    null,
                    configuration),
                new GovernedLoopNodeDefinition(
                    "success-fallback",
                    GovernedLoopSequentialNodeDescriptors.IdentityTransform,
                    [
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                    ],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-fallback-success", "human-input", "success-fallback", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("fallback-to-exit", "success-fallback", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            role,
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-fallback", GovernedLoopBindingKind.Data, "trigger", "request", "success-fallback", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("fallback-to-exit", GovernedLoopBindingKind.Data, "success-fallback", GovernedLoopPureNodeVocabulary.OutputPort, "exit", "result"),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopGraphRevisionArtifact HumanInputConditionArtifact(ContextualRoleRevisionPin role)
    {
        var configuration = HumanInputConfiguration();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-input",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                    [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(),
                    null,
                    null,
                    null,
                    configuration),
                new GovernedLoopNodeDefinition(
                    "response-condition",
                    GovernedLoopSequentialNodeDescriptors.ExactTextCondition,
                    [new GovernedLoopPortDefinition(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string> { [GovernedLoopTopologyNodeVocabulary.ExpectedParameter] = "allow" }),
                new GovernedLoopNodeDefinition(
                    "approved",
                    GovernedLoopSequentialNodeDescriptors.FailTerminal,
                    [],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>
                    {
                        [GovernedLoopFailNodeVocabulary.CodeParameter] = "response-approved",
                        [GovernedLoopFailNodeVocabulary.ExplanationParameter] = "The approved route terminates without retaining the response value.",
                    }),
                new GovernedLoopNodeDefinition(
                    "denied-fallback",
                    GovernedLoopSequentialNodeDescriptors.IdentityTransform,
                    [
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                    ],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-condition", "human-input", "response-condition", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("condition-approved", "response-condition", "approved", GovernedLoopControlCondition.True),
                new GovernedLoopControlEdgeDefinition("condition-denied", "response-condition", "denied-fallback", GovernedLoopControlCondition.False),
                new GovernedLoopControlEdgeDefinition("denied-fallback-to-exit", "denied-fallback", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit", "approved"],
            role,
            bindings:
            [
                new GovernedLoopBindingDefinition("response-to-condition", GovernedLoopBindingKind.Data, "human-input", GovernedLoopHumanInputVocabulary.ResponsePortId, "response-condition", GovernedLoopTopologyNodeVocabulary.ValuePort),
                new GovernedLoopBindingDefinition("request-to-denied-fallback", GovernedLoopBindingKind.Data, "trigger", "request", "denied-fallback", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("denied-fallback-to-exit", GovernedLoopBindingKind.Data, "denied-fallback", GovernedLoopPureNodeVocabulary.OutputPort, "exit", "result"),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopGraphRevisionArtifact ParallelHumanInputArtifact(ContextualRoleRevisionPin role)
    {
        var source = GovernedLoopSequentialApplicationTestFixture.ParallelAllJoinArtifact(role).Graph;
        var configuration = HumanInputConfiguration();
        var nodes = source.Nodes.Select(node => node.Id is "branch-a" or "branch-b"
            ? new GovernedLoopNodeDefinition(
                node.Id,
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>(),
                null,
                null,
                null,
                configuration)
            : node)
            .ToArray();
        var bindings = source.Bindings
            .Where(binding => binding.ToNodeId is not "branch-a" and not "branch-b")
            .ToArray();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes,
            source.ControlEdges,
            source.TerminalNodeIds,
            role,
            bindings,
            source.ValueSchemas,
            source.OutputContract,
            source.AuthorityCeiling);
    }

    private static GovernedLoopHumanInputNodeConfiguration HumanInputConfiguration()
        => new(
            GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
            "text",
            "Collect bounded untrusted data.",
            "Provide the requested bounded response.",
            new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")],
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            "timeout-policy-one@revision-one",
            "failure-policy-one@revision-one");

    private static HumanInputPolicyResolutionService HumanInputPolicyResolver(
        SequentialTestContext context,
        DateTimeOffset? resolvedAtUtc = null,
        HumanInputPolicyResolutionTestSource? source = null,
        TimeProvider? timeProvider = null)
    {
        source ??= new HumanInputPolicyResolutionTestSource();
        var binding = context.Anchor.AdapterBinding;
        var timeout = TimeoutPolicy(binding, context.Run.AdmissionActor);
        var failure = FailurePolicy(binding, context.Run.AdmissionActor);
        source.Results.TryAdd(timeout.Reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, timeout, 1));
        source.Results.TryAdd(failure.Reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, failure, 1));
        return new HumanInputPolicyResolutionService(source, timeProvider ?? new FixedTimeProvider(resolvedAtUtc ?? _now));
    }

    private static HumanInputPolicyArtifact TimeoutPolicy(GovernedLoopSequentialAdapterBinding binding, string actorId)
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(
            HumanInputPolicyArtifact.CurrentSchemaVersion,
            "timeout-policy-one",
            "revision-one",
            HumanInputPolicyKind.ResponseWindow,
            binding.WorkspaceId,
            binding.ExecutionBinding.Revision.GraphId,
            actorId,
            60_000,
            HumanInputTerminalDisposition.Unknown,
            string.Empty));

    private static HumanInputPolicyArtifact FailurePolicy(GovernedLoopSequentialAdapterBinding binding, string actorId)
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(
            HumanInputPolicyArtifact.CurrentSchemaVersion,
            "failure-policy-one",
            "revision-one",
            HumanInputPolicyKind.DeadlineDisposition,
            binding.WorkspaceId,
            binding.ExecutionBinding.Revision.GraphId,
            actorId,
            null,
            HumanInputTerminalDisposition.Expired,
            string.Empty));
}
