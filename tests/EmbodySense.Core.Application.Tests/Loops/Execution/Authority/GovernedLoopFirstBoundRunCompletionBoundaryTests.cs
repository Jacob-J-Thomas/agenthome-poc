using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

public sealed class GovernedLoopFirstBoundRunCompletionBoundaryTests
{
    [Fact]
    public async Task Unconstrained_success_invokes_terminal_commit_once_without_mutating_completion_evidence()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create();
        var usage = new RecordingEffectAuthorityUsageStore();
        var transaction = new RecordingEffectAuthorityTransaction();
        var callbackCount = 0;
        var boundary = new GovernedLoopFirstBoundRunCompletionBoundary(usage, transaction, Clock());

        var result = await boundary.ExecuteAsync(
            fixture.Request.AdmissionReceipt,
            fixture.Request.ExecutionBinding,
            _ =>
            {
                Assert.True(transaction.IsInside);
                callbackCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Allowed, result.Status);
        Assert.Equal(GovernedLoopFirstBoundRunCompletionDisposition.Completed, result.Disposition);
        Assert.True(result.CommitInvoked);
        Assert.Equal(1, callbackCount);
        Assert.Empty(usage.CompletionBegins);
        Assert.Empty(usage.CompletionCompletes);
    }

    [Fact]
    public async Task First_bound_run_completion_is_pending_before_commit_and_completed_after_success()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var usage = new RecordingEffectAuthorityUsageStore();
        var transaction = new RecordingEffectAuthorityTransaction();
        var callbackCount = 0;
        var boundary = new GovernedLoopFirstBoundRunCompletionBoundary(usage, transaction, Clock());

        var result = await boundary.ExecuteAsync(
            fixture.Request.AdmissionReceipt,
            fixture.Request.ExecutionBinding,
            _ =>
            {
                Assert.Single(usage.CompletionBegins);
                Assert.Empty(usage.CompletionCompletes);
                Assert.True(transaction.IsInside);
                callbackCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted, result.Status);
        Assert.Equal(GovernedLoopFirstBoundRunCompletionDisposition.Completed, result.Disposition);
        Assert.True(result.CommitInvoked);
        Assert.Equal(1, callbackCount);
        var begin = Assert.Single(usage.CompletionBegins);
        var complete = Assert.Single(usage.CompletionCompletes);
        Assert.Equal(begin.Grant, complete.Grant);
        Assert.Equal(begin.AdmissionReceiptHash, complete.AdmissionReceiptHash);
        Assert.Equal(begin.RunId, complete.RunId);
        Assert.Equal(begin.CompletionOperationId, complete.CompletionOperationId);
        Assert.StartsWith("run-completion-", begin.CompletionOperationId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_completed_replay_is_idempotent_without_reinvoking_terminal_commit()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var usage = new RecordingEffectAuthorityUsageStore();
        usage.BeginStatuses.Enqueue(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted);
        var callbackCount = 0;

        var result = await new GovernedLoopFirstBoundRunCompletionBoundary(usage, new RecordingEffectAuthorityTransaction(), Clock())
            .ExecuteAsync(
                fixture.Request.AdmissionReceipt,
                fixture.Request.ExecutionBinding,
                _ =>
                {
                    callbackCount++;
                    return Task.CompletedTask;
                });

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyCompleted, result.Status);
        Assert.Equal(GovernedLoopFirstBoundRunCompletionDisposition.AlreadyCompleted, result.Disposition);
        Assert.False(result.CommitInvoked);
        Assert.Contains("reload and authenticate", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, callbackCount);
        Assert.Single(usage.CompletionBegins);
        Assert.Empty(usage.CompletionCompletes);
    }

    [Fact]
    public async Task Callback_failure_leaves_pending_and_a_fresh_boundary_resumes_only_the_exact_idempotent_operation()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var usage = new RecordingEffectAuthorityUsageStore();
        usage.BeginStatuses.Enqueue(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending);
        usage.BeginStatuses.Enqueue(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionAlreadyPending);
        var firstBoundary = new GovernedLoopFirstBoundRunCompletionBoundary(usage, new RecordingEffectAuthorityTransaction(), Clock());
        var failure = new IOException("Injected terminal persistence failure.");

        var thrown = await Assert.ThrowsAsync<IOException>(() => firstBoundary.ExecuteAsync(
            fixture.Request.AdmissionReceipt,
            fixture.Request.ExecutionBinding,
            _ => Task.FromException(failure)));

        Assert.Same(failure, thrown);
        Assert.Single(usage.CompletionBegins);
        Assert.Empty(usage.CompletionCompletes);

        var callbackCount = 0;
        var restartedBoundary = new GovernedLoopFirstBoundRunCompletionBoundary(usage, new RecordingEffectAuthorityTransaction(), Clock());
        var resumed = await restartedBoundary.ExecuteAsync(
            fixture.Request.AdmissionReceipt,
            fixture.Request.ExecutionBinding,
            _ =>
            {
                callbackCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted, resumed.Status);
        Assert.Equal(GovernedLoopFirstBoundRunCompletionDisposition.Completed, resumed.Disposition);
        Assert.True(resumed.CommitInvoked);
        Assert.Equal(1, callbackCount);
        Assert.Equal(2, usage.CompletionBegins.Count);
        Assert.Equal(usage.CompletionBegins[0].CompletionOperationId, usage.CompletionBegins[1].CompletionOperationId);
        Assert.Single(usage.CompletionCompletes);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Conflict)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable)]
    public async Task Nonadmitting_completion_posture_never_invokes_terminal_commit(
        GovernedLoopEffectAuthorityUsageStoreStatus status)
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var usage = new RecordingEffectAuthorityUsageStore();
        usage.BeginStatuses.Enqueue(status);
        var callbackCount = 0;

        var result = await new GovernedLoopFirstBoundRunCompletionBoundary(usage, new RecordingEffectAuthorityTransaction(), Clock())
            .ExecuteAsync(
                fixture.Request.AdmissionReceipt,
                fixture.Request.ExecutionBinding,
                _ =>
                {
                    callbackCount++;
                    return Task.CompletedTask;
                });

        Assert.Equal(status, result.Status);
        Assert.Equal(GovernedLoopFirstBoundRunCompletionDisposition.Rejected, result.Disposition);
        Assert.False(result.CommitInvoked);
        Assert.Equal(0, callbackCount);
        Assert.Empty(usage.CompletionCompletes);
    }

    [Fact]
    public async Task Caller_cancellation_after_terminal_commit_cannot_strand_the_claim_pending()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var usage = new RecordingEffectAuthorityUsageStore();
        using var cancellation = new CancellationTokenSource();
        var callbackCount = 0;

        var result = await new GovernedLoopFirstBoundRunCompletionBoundary(usage, new RecordingEffectAuthorityTransaction(), Clock())
            .ExecuteAsync(
                fixture.Request.AdmissionReceipt,
                fixture.Request.ExecutionBinding,
                _ =>
                {
                    callbackCount++;
                    cancellation.Cancel();
                    return Task.CompletedTask;
                },
                cancellation.Token);

        Assert.Equal(GovernedLoopFirstBoundRunCompletionDisposition.Completed, result.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted, result.Status);
        Assert.True(result.CommitInvoked);
        Assert.Equal(1, callbackCount);
        Assert.False(usage.CompleteObservedCancellation);
        Assert.Single(usage.CompletionCompletes);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Allowed)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous)]
    [InlineData(GovernedLoopEffectAuthorityUsageStoreStatus.Conflict)]
    public async Task Unconfirmed_postcallback_finalize_result_requires_integrity_review(
        GovernedLoopEffectAuthorityUsageStoreStatus status)
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var usage = new RecordingEffectAuthorityUsageStore();
        usage.CompleteStatuses.Enqueue(status);

        var result = await new GovernedLoopFirstBoundRunCompletionBoundary(usage, new RecordingEffectAuthorityTransaction(), Clock())
            .ExecuteAsync(fixture.Request.AdmissionReceipt, fixture.Request.ExecutionBinding, _ => Task.CompletedTask);

        Assert.Equal(GovernedLoopFirstBoundRunCompletionDisposition.NeedsReview, result.Disposition);
        Assert.Equal(status, result.Status);
        Assert.True(result.CommitInvoked);
        Assert.Contains("integrity warning", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalize_failure_after_terminal_commit_surfaces_needs_review_without_rewriting_truthful_completion()
    {
        var fixture = GovernedLoopEffectAuthorityTestFixture.Create(
            completionConstraint: AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);
        var usage = new RecordingEffectAuthorityUsageStore
        {
            CompleteException = new IOException("Injected process loss after terminal persistence."),
        };
        var callbackCount = 0;

        var result = await new GovernedLoopFirstBoundRunCompletionBoundary(usage, new RecordingEffectAuthorityTransaction(), Clock())
            .ExecuteAsync(
                fixture.Request.AdmissionReceipt,
                fixture.Request.ExecutionBinding,
                _ =>
                {
                    callbackCount++;
                    return Task.CompletedTask;
                });

        Assert.Equal(GovernedLoopFirstBoundRunCompletionDisposition.NeedsReview, result.Disposition);
        Assert.Equal(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous, result.Status);
        Assert.True(result.CommitInvoked);
        Assert.Equal(1, callbackCount);
        Assert.Contains("ambiguous", result.Detail, StringComparison.Ordinal);
    }

    private static TimeProvider Clock()
        => new FixedEffectAuthorityTimeProvider(GovernedLoopEffectAuthorityTestFixture.Now);
}
