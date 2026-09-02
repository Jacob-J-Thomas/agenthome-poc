using System.Diagnostics;
using System.Globalization;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.CancellationHost.Persistence;

/// <summary>Runs one authenticated reconciliation mutation for an external-process crash/restart fixture.</summary>
internal static class GovernedLoopEffectReconciliationCrossProcessHost
{
    private static readonly TimeSpan _gateTimeout = TimeSpan.FromSeconds(60);

    internal static async Task<int> RunAsync(
        string workspace,
        string gate,
        string ready,
        string output,
        string operationId,
        string requestHash,
        string purpose,
        string expectedVersionText,
        string expectedHash,
        string replacementBase64,
        string successorBase64,
        string crashBoundaryText)
    {
        if (string.IsNullOrWhiteSpace(workspace)
            || string.IsNullOrWhiteSpace(gate)
            || string.IsNullOrWhiteSpace(ready)
            || string.IsNullOrWhiteSpace(output)
            || string.IsNullOrWhiteSpace(operationId)
            || string.IsNullOrWhiteSpace(requestHash)
            || string.IsNullOrWhiteSpace(purpose)
            || !TryParseExpectedVersion(expectedVersionText, out var expectedVersion)
            || !TryDecodeCase(replacementBase64, out var replacement)
            || !TryDecodeOptionalAttempt(successorBase64, out var successor)
            || !TryParseOptionalBoundary(crashBoundaryText, out var crashBoundary))
        {
            return 2;
        }

        var options = new GovernedLoopEffectReconciliationCaseStoreOptions
        {
            DurableBoundaryObserver = crashBoundary is null
                ? null
                : observed =>
                {
                    if (observed == crashBoundary)
                    {
                        Process.GetCurrentProcess().Kill();
                    }
                },
        };
        var store = new GovernedLoopEffectReconciliationCaseStore(new WorkspacePaths(workspace), reconciliationOptions: options);
        await File.WriteAllTextAsync(ready, "ready");
        await WaitForGateAsync(gate);

        var replacementCase = replacement!;
        var request = new GovernedLoopEffectReconciliationCaseMutationRequest(
            operationId,
            requestHash,
            purpose,
            expectedVersion,
            string.IsNullOrEmpty(expectedHash) ? null : expectedHash,
            replacementCase.Binding,
            replacementCase,
            successor);
        var result = await store.CompareExchangeAsync(request);
        await File.WriteAllTextAsync(output, result.Status.ToString());
        return 0;
    }

    private static bool TryDecodeCase(string value, out GovernedLoopEffectReconciliationCase? parsed)
    {
        parsed = null;
        try
        {
            return GovernedLoopEffectReconciliationRecordCodec.TryDecode(Convert.FromBase64String(value), out parsed, out _)
                && parsed is not null;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecodeOptionalAttempt(string value, out GovernedLoopEffectAttempt? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        try
        {
            return GovernedLoopEffectAttemptRecordCodec.TryDecode(Convert.FromBase64String(value), out parsed, out _)
                && parsed is not null;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseExpectedVersion(string value, out long? parsed)
    {
        if (string.IsNullOrEmpty(value))
        {
            parsed = null;
            return true;
        }

        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var candidate) && candidate > 0)
        {
            parsed = candidate;
            return true;
        }

        parsed = null;
        return false;
    }

    private static bool TryParseOptionalBoundary(string value, out GovernedLoopEffectReconciliationPersistenceBoundary? parsed)
    {
        if (string.IsNullOrEmpty(value))
        {
            parsed = null;
            return true;
        }

        if (Enum.TryParse<GovernedLoopEffectReconciliationPersistenceBoundary>(value, ignoreCase: false, out var candidate))
        {
            parsed = candidate;
            return true;
        }

        parsed = null;
        return false;
    }

    private static async Task WaitForGateAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (wait.Elapsed >= _gateTimeout)
            {
                throw new TimeoutException($"The reconciliation process did not observe gate `{path}`.");
            }

            await Task.Delay(10);
        }
    }
}
