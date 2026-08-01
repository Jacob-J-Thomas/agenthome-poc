using Xunit.Abstractions;
using EmbodySense.Core.Persistence.Tests.Verification.Models;

namespace EmbodySense.Core.Persistence.Tests.Verification;

public sealed class VerificationPhaseProbeTests
{
    [Fact]
    public void Over_budget_phase_fails_without_announcing_completion_or_advancing_last_completed_phase()
    {
        var output = new RecordingTestOutputHelper();
        var probe = new VerificationPhaseProbe(output, nameof(Over_budget_phase_fails_without_announcing_completion_or_advancing_last_completed_phase), VerificationTier.PullRequest);
        var withinBoundBudget = new VerificationPhaseBudget("within-bound", VerificationPhaseClassification.ProductionBoundary, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
        var overBoundBudget = new VerificationPhaseBudget("over-bound", VerificationPhaseClassification.ProductionBoundary, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));

        probe.Run(withinBoundBudget, static () => { });

        var exception = Assert.Throws<TimeoutException>(() => probe.Run(overBoundBudget, static () => Thread.Sleep(25)));

        Assert.Contains("Last completed phase: `within-bound`.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(output.Lines, line => line.StartsWith("VERIFY_TEST_PHASE_COMPLETE=") && line.Contains("\"phase\":\"over-bound\"", StringComparison.Ordinal));
        Assert.Contains(output.Lines, line => line.StartsWith("VERIFY_TEST_PHASE_FAILED=") && line.Contains("\"phase\":\"over-bound\"", StringComparison.Ordinal) && line.Contains("\"lastCompletedPhase\":\"within-bound\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Over_allocation_phase_fails_without_announcing_completion()
    {
        var output = new RecordingTestOutputHelper();
        var probe = new VerificationPhaseProbe(output, nameof(Over_allocation_phase_fails_without_announcing_completion), VerificationTier.PullRequest);
        var budget = new VerificationPhaseBudget("allocation-bound", VerificationPhaseClassification.ProductionBoundary, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1024);

        var exception = Assert.Throws<InvalidOperationException>(() => probe.Run(budget, static () => GC.AllocateUninitializedArray<byte>(1024 * 1024)));

        Assert.Contains("exceeding its 1,024-byte maximum", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(output.Lines, line => line.StartsWith("VERIFY_TEST_PHASE_COMPLETE=") && line.Contains("\"phase\":\"allocation-bound\"", StringComparison.Ordinal));
        Assert.Contains(output.Lines, line => line.StartsWith("VERIFY_TEST_PHASE_FAILED=") && line.Contains("\"phase\":\"allocation-bound\"", StringComparison.Ordinal));
    }

    private sealed class RecordingTestOutputHelper : ITestOutputHelper
    {
        public List<string> Lines { get; } = [];

        public void WriteLine(string message)
        {
            Lines.Add(message);
        }

        public void WriteLine(string format, params object[] args)
        {
            Lines.Add(string.Format(format, args));
        }
    }
}
