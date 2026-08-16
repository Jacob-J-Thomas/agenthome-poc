using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopRecoveringWaitWorkRunnerTests
{
    [Fact]
    public async Task Wake_recovery_completes_before_ordinary_discovery_and_retries_on_the_next_cycle()
    {
        var inner = new RecordingRunner();
        var recovery = new RecordingRecovery(
            new GovernedLoopWaitRecoveryResult(1, 1, 0),
            new GovernedLoopWaitRecoveryResult(0, 0, 0));
        var runner = new GovernedLoopRecoveringWaitWorkRunner(inner, recovery, 16);

        var recovered = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);
        var delegated = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, recovered!.Status);
        Assert.Equal("wait-recovery-completed", recovered.ReasonCode);
        Assert.Same(inner.Result, delegated);
        Assert.Equal([16, 16], recovery.MaximumCounts);
        Assert.Equal([GovernedLoopLocalWorkFamily.Wake], inner.Families);
    }

    [Fact]
    public async Task Recovery_attention_and_unavailability_fail_closed_without_wake_discovery()
    {
        var inner = new RecordingRunner();
        var attention = new GovernedLoopRecoveringWaitWorkRunner(
            inner,
            new RecordingRecovery(new GovernedLoopWaitRecoveryResult(1, 0, 1)),
            16);
        var unavailable = new GovernedLoopRecoveringWaitWorkRunner(
            inner,
            new RecordingRecovery(new InvalidOperationException("simulated recovery failure")),
            16);

        var attentionResult = await attention.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);
        var unavailableResult = await unavailable.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.AttentionRequired, attentionResult!.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, unavailableResult!.Status);
        Assert.Empty(inner.Families);
    }

    [Fact]
    public async Task Adjacent_families_delegate_without_scanning_wait_recovery()
    {
        var inner = new RecordingRunner();
        var recovery = new RecordingRecovery(new GovernedLoopWaitRecoveryResult(1, 1, 0));
        var runner = new GovernedLoopRecoveringWaitWorkRunner(inner, recovery, 16);

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);

        Assert.Same(inner.Result, result);
        Assert.Empty(recovery.MaximumCounts);
        Assert.Equal([GovernedLoopLocalWorkFamily.Schedule], inner.Families);
    }

    [Fact]
    public void Invalid_recovery_page_limit_is_rejected()
    {
        var inner = new RecordingRunner();
        var recovery = new RecordingRecovery(new GovernedLoopWaitRecoveryResult(0, 0, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopRecoveringWaitWorkRunner(inner, recovery, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopRecoveringWaitWorkRunner(inner, recovery, 257));
    }

    private sealed class RecordingRecovery : IGovernedLoopWaitRecoveryPort
    {
        private readonly Queue<object> _outcomes;

        internal RecordingRecovery(params object[] outcomes)
            => _outcomes = new Queue<object>(outcomes);

        internal List<int> MaximumCounts { get; } = [];

        public Task<GovernedLoopWaitRecoveryResult> RecoverAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumCounts.Add(maximumCount);
            var outcome = _outcomes.Dequeue();
            return outcome is Exception exception
                ? Task.FromException<GovernedLoopWaitRecoveryResult>(exception)
                : Task.FromResult((GovernedLoopWaitRecoveryResult)outcome);
        }
    }

    private sealed class RecordingRunner : IGovernedLoopLocalWorkRunner
    {
        internal GovernedLoopLocalWorkResult Result { get; } = new(
            GovernedLoopLocalWorkResultStatus.Completed,
            "wake-delegated");

        internal List<GovernedLoopLocalWorkFamily> Families { get; } = [];

        public Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
            GovernedLoopLocalWorkFamily family,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Families.Add(family);
            return Task.FromResult<GovernedLoopLocalWorkResult?>(Result);
        }
    }
}
