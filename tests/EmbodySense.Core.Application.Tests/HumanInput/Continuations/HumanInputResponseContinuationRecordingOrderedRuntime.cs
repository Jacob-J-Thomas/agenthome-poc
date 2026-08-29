using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Sleep;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

internal sealed class HumanInputResponseContinuationRecordingOrderedRuntime(
    HumanInputResponseContinuationInMemoryRunStore runs,
    bool advancesCanonicalRun) : IGovernedLoopSequentialOrderedRuntime
{
    internal int ResumeHumanInputCount { get; private set; }

    internal int ResumeHumanInputFailureCount { get; private set; }

    internal Exception? Failure { get; private set; }

    internal Exception? HumanInputResumeException { get; set; }

    internal Exception? HumanInputFailureResumeException { get; set; }

    internal bool ReturnNullHumanInputResume { get; set; }

    internal Action? BeforeHumanInputResume { get; set; }

    internal bool ReturnNullHumanInputFailureResume { get; set; }

    public Task<CustomLoopOrderedRunResult> RunAsync(GovernedLoopSequentialOrderedRunRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopSequentialOrderedResumeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<CustomLoopOrderedRunResult> ResumeHumanInputAsync(GovernedLoopSequentialOrderedHumanInputResumeRequest request, CancellationToken cancellationToken = default)
    {
        ResumeHumanInputCount++;
        BeforeHumanInputResume?.Invoke();
        if (HumanInputResumeException is not null)
        {
            throw HumanInputResumeException;
        }
        if (ReturnNullHumanInputResume)
        {
            return null!;
        }
        if (advancesCanonicalRun)
        {
            try
            {
                await runs.AdvanceFromOrderedHumanInputReentryAsync(
                    request.Anchor.AdapterBinding,
                    request.Plan,
                    GovernedLoopSleepApplicationTestFixture.Now.AddMinutes(1),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                Failure = exception;
                return new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Failed, null, exception.Message);
            }
        }
        return new CustomLoopOrderedRunResult(
            advancesCanonicalRun ? CustomLoopOrderedRunStatus.Completed : CustomLoopOrderedRunStatus.InvalidState,
            runs.Current,
            "The test ordered runtime returned a public result after the exact terminal Human Input checkpoint.");
    }

    public async Task<CustomLoopOrderedRunResult> ResumeHumanInputFailureAsync(GovernedLoopSequentialOrderedHumanInputFailureResumeRequest request, CancellationToken cancellationToken = default)
    {
        ResumeHumanInputFailureCount++;
        if (HumanInputFailureResumeException is not null)
        {
            throw HumanInputFailureResumeException;
        }
        if (ReturnNullHumanInputFailureResume)
        {
            return null!;
        }
        if (advancesCanonicalRun)
        {
            try
            {
                await runs.AdvanceFromOrderedHumanInputReentryAsync(
                    request.Anchor.AdapterBinding,
                    request.Plan,
                    GovernedLoopSleepApplicationTestFixture.Now.AddMinutes(1),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                Failure = exception;
                return new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Failed, runs.Current, exception.Message);
            }
        }
        return new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Failed, runs.Current, "The test ordered runtime returned a public result after the exact routed Human Input failure.");
    }
}
