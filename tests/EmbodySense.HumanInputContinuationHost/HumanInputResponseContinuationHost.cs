using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Execution.Authority;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.HumanInputContinuationHost;

internal static class HumanInputResponseContinuationHost
{
    private static readonly TimeSpan _gateTimeout = TimeSpan.FromSeconds(60);

    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var workspaceRoot, var runId, var checkpointId, var utcTicksText, var crashPlane, var crashBoundaryText, var crashOrdinalText, var readyPath, var releasePath, var resultPath]
            || !long.TryParse(utcTicksText, out var utcTicks)
            || !int.TryParse(crashOrdinalText, out var crashOrdinal)
            || crashOrdinal < 1)
        {
            return 2;
        }

        var now = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        var clock = new HumanInputResponseContinuationHostClock(now);
        var paths = new WorkspacePaths(workspaceRoot);
        var crash = HumanInputResponseContinuationHostCrashObserver.Create(crashPlane, crashBoundaryText, crashOrdinal);
        using var runs = new CustomLoopRunStore(paths, clock, crash.ObserveRunAsync);
        var responses = new HumanInputRequestStore(paths);
        var sleepStore = new GovernedLoopSleepStore(paths, new GovernedLoopSleepStoreOptions
        {
            DurableBoundaryObserver = crash.ObserveSleep
        });
        var posture = new HumanInputResponseContinuationHostCurrentPosturePort(runs, clock);
        var contexts = new HumanInputResponseContinuationHostContextPort();
        var authorityUsage = new GovernedLoopEffectAuthorityEvidenceStore(paths);
        var completionTransaction = new HumanInputResponseContinuationHostCompletionTransaction();
        var ordered = new CustomLoopOrderedRunner(
            runs,
            new CustomLoopContextResolver(),
            new HumanInputResponseContinuationHostInferenceExecutor(),
            new HumanInputResponseContinuationHostConversationPublisher(),
            new AuditLog(paths),
            new HumanInputResponseContinuationHostAuthorityProvider(clock),
            clock,
            capabilityAdmissionService: new HumanInputResponseContinuationHostCapabilityAdmissionService(),
            firstBoundRunCompletionBoundary: new GovernedLoopFirstBoundRunCompletionBoundary(authorityUsage, completionTransaction, clock),
            humanInputBindingSource: new HumanInputResponseContinuationBindingSource(responses));
        var orderedRuntime = new HumanInputResponseContinuationHostOrderedRuntime(
            new GovernedLoopSequentialOrderedRuntimeAdapter(ordered, runs, runs, new AuditLog(paths)));
        var continuation = new HumanInputResponseContinuationService(
            runs,
            responses,
            sleepStore,
            posture,
            contexts,
            orderedRuntime,
            clock);
        var sleep = new GovernedLoopSleepService(sleepStore, posture, continuation, continuation, clock);
        continuation.BindSleep(sleep);

        SignalReadyAndWaitForRelease(readyPath, releasePath);
        var result = await continuation.WakeAsync(new HumanInputResponseContinuationCandidate(runId, checkpointId)).ConfigureAwait(false);
        await File.WriteAllTextAsync(resultPath, result.Status.ToString()).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            resultPath + ".diagnostic",
            result.Wake is { } wake
                ? $"status={wake.Status}; disposition={wake.Evidence?.Disposition}; reference={wake.Evidence?.DispositionEvidenceReference}; invoked={wake.ContinuationInvoked}; {OrderedDiagnostic(orderedRuntime.LastResult, completionTransaction.LastFailureDetail)}"
                : $"wake=<none>; {OrderedDiagnostic(orderedRuntime.LastResult, completionTransaction.LastFailureDetail)}").ConfigureAwait(false);
        return result.Status is HumanInputResponseContinuationWakeStatus.Submitted or HumanInputResponseContinuationWakeStatus.Replayed or HumanInputResponseContinuationWakeStatus.Retired ? 0 : 3;
    }

    private static string OrderedDiagnostic(EmbodySense.Core.Application.Loops.Execution.Custom.Models.CustomLoopOrderedRunResult? result, string? completionFailure)
        => result is null
            ? $"ordered=<none>; completionFailure={completionFailure}"
            : $"ordered={result.Status}; orderedDetail={result.Detail}; completionFailure={completionFailure}";

    private static void SignalReadyAndWaitForRelease(string readyPath, string releasePath)
    {
        if (readyPath == "-" && releasePath == "-")
        {
            return;
        }

        File.WriteAllText(readyPath, "ready");
        var startedAt = TimeProvider.System.GetTimestamp();
        while (!File.Exists(releasePath))
        {
            if (TimeProvider.System.GetElapsedTime(startedAt) >= _gateTimeout)
            {
                throw new TimeoutException("The Human Input continuation host release marker was not published.");
            }

            Thread.Sleep(10);
        }
    }
}
