using System.Collections.Concurrent;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopCoordinatorRepairDependencyProbeTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', 64);

    [Fact]
    public async Task ReadAsync_collects_all_canonical_families_without_admitting_work()
    {
        var work = new RecordingReadinessProbe();
        var probe = new GovernedLoopCoordinatorRepairDependencyProbe(work, new FixedClock(_now));

        var readiness = await probe.ReadAsync(_workspaceId, "coordinator");

        Assert.NotNull(readiness);
        Assert.True(GovernedLoopSleepContractValidator.Validate(readiness).IsValid);
        Assert.True(GovernedLoopCoordinatorRepairReadinessContract.IsReady(readiness));
        Assert.Equal(_now, readiness.EvaluatedAtUtc);
        Assert.Equal(
            new[]
            {
                GovernedLoopLocalWorkFamily.Schedule,
                GovernedLoopLocalWorkFamily.Trigger,
                GovernedLoopLocalWorkFamily.Wake,
                GovernedLoopLocalWorkFamily.HumanInput,
                GovernedLoopLocalWorkFamily.HumanReview
            },
            work.Families.Order());
    }

    [Fact]
    public async Task ReadAsync_marks_the_exact_faulted_family_not_ready_without_dispatch()
    {
        var work = new RecordingReadinessProbe
        {
            FaultedFamily = GovernedLoopLocalWorkFamily.HumanInput
        };
        var probe = new GovernedLoopCoordinatorRepairDependencyProbe(work, new FixedClock(_now));

        var readiness = await probe.ReadAsync(_workspaceId, "coordinator");

        Assert.NotNull(readiness);
        Assert.True(GovernedLoopSleepContractValidator.Validate(readiness).IsValid);
        Assert.False(readiness.HumanInputReady);
        Assert.False(GovernedLoopCoordinatorRepairReadinessContract.IsReady(readiness));
    }

    [Fact]
    public async Task ReadAsync_marks_human_review_unready_without_authorizing_repair()
    {
        var work = new RecordingReadinessProbe
        {
            FaultedFamily = GovernedLoopLocalWorkFamily.HumanReview
        };
        var probe = new GovernedLoopCoordinatorRepairDependencyProbe(work, new FixedClock(_now));

        var readiness = await probe.ReadAsync(_workspaceId, "coordinator");

        Assert.NotNull(readiness);
        Assert.True(GovernedLoopSleepContractValidator.Validate(readiness).IsValid);
        Assert.False(readiness.HumanReviewReady);
        Assert.False(GovernedLoopCoordinatorRepairReadinessContract.IsReady(readiness));
    }

    [Fact]
    public async Task ReadAsync_fails_closed_for_a_trusted_clock_fault()
    {
        var probe = new GovernedLoopCoordinatorRepairDependencyProbe(new RecordingReadinessProbe(), new ThrowingClock());

        var readiness = await probe.ReadAsync(_workspaceId, "coordinator");

        Assert.Null(readiness);
    }

    [Fact]
    public async Task ReadAsync_propagates_caller_cancellation_without_substituting_readiness()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probe = new GovernedLoopCoordinatorRepairDependencyProbe(new RecordingReadinessProbe(), new FixedClock(_now));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ReadAsync(_workspaceId, "coordinator", cancellation.Token));
    }

    private sealed class RecordingReadinessProbe : IGovernedLoopLocalWorkReadinessProbe
    {
        private readonly ConcurrentBag<GovernedLoopLocalWorkFamily> _families = [];

        internal GovernedLoopLocalWorkFamily? FaultedFamily { get; init; }

        internal IReadOnlyCollection<GovernedLoopLocalWorkFamily> Families => _families;

        public Task<GovernedLoopLocalWorkResult?> ProbeReadinessAsync(
            GovernedLoopLocalWorkFamily family,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _families.Add(family);
            if (family == FaultedFamily)
            {
                throw new IOException("simulated dependency outage");
            }

            return Task.FromResult<GovernedLoopLocalWorkResult?>(new GovernedLoopLocalWorkResult(
                GovernedLoopLocalWorkResultStatus.Empty,
                "dependency-ready"));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("clock unavailable");
    }
}
