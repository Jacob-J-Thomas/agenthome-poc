using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopWaitOnlyWorkRunnerTests
{
    [Theory]
    [InlineData(GovernedLoopLocalWorkFamily.Schedule)]
    [InlineData(GovernedLoopLocalWorkFamily.Trigger)]
    public async Task Non_wake_families_are_explicitly_empty_without_dispatch(
        GovernedLoopLocalWorkFamily family)
    {
        var inner = new RecordingRunner();
        var runner = new GovernedLoopWaitOnlyWorkRunner(inner);

        var result = await runner.RunOnceAsync(family);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, result?.Status);
        Assert.Equal("family-not-owned", result?.ReasonCode);
        Assert.Empty(inner.Families);
    }

    [Fact]
    public async Task Wake_family_delegates_exactly_once()
    {
        var inner = new RecordingRunner();
        var runner = new GovernedLoopWaitOnlyWorkRunner(inner);

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Same(inner.Result, result);
        Assert.Equal([GovernedLoopLocalWorkFamily.Wake], inner.Families);
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
