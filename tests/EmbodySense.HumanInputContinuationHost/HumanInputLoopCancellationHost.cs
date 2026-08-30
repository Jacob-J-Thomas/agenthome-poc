using System.Globalization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.HumanInputContinuationHost;

/// <summary>Executes one durable Human Input-aware loop cancellation in an external process, with named crash boundaries for restart tests.</summary>
internal static class HumanInputLoopCancellationHost
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var workspaceRoot, var runId, var expectedLifecycleVersionText, var utcTicksText, var grantPath, var crashBoundary, var operationId, var resultPath]
            || !int.TryParse(expectedLifecycleVersionText, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedLifecycleVersion)
            || !long.TryParse(utcTicksText, NumberStyles.None, CultureInfo.InvariantCulture, out var utcTicks)
            || expectedLifecycleVersion < 0
            || !IsCrashBoundary(crashBoundary)
            || !AuthorityGrantJson.TryDeserialize(await File.ReadAllTextAsync(grantPath).ConfigureAwait(false), out var grant, out _)
            || grant is null)
        {
            return 2;
        }

        var now = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        var clock = new HumanInputResponseContinuationHostClock(now);
        var paths = new WorkspacePaths(workspaceRoot);
        var dependencies = new HumanInputLoopCancellationHostDependencies(crashBoundary == "RunCancellationCommitted");
        var runCrashObserver = new HumanInputLoopCancellationHostRunCrashObserver(crashBoundary);
        using var runs = new CustomLoopRunStore(paths, clock, runCrashObserver.ObserveAsync);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
        var controls = new CustomLoopControlOperationStore(paths, dependencies, clock);
        var requests = new HumanInputRequestStore(paths, new HumanInputRequestStoreOptions
        {
            DurableBoundaryObserver = new HumanInputRequestPublicationHostCrashObserver(
                Enum.TryParse<HumanInputRequestPersistenceBoundary>(crashBoundary, ignoreCase: false, out _) ? crashBoundary : "none",
                1).ObserveAsync
        });
        var authorityTransaction = new CapabilityAuthorityTransaction(paths);
        var convergence = new CustomLoopHumanInputCancellationConvergenceService(
            runs,
            controls,
            requests,
            new HumanInputRequestPublicationHostGrantResolver(grant, now),
            authorityTransaction,
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            clock);
        var lifecycle = new CustomLoopLifecycleService(
            runs,
            controls,
            dependencies,
            dependencies,
            dependencies,
            dependencies,
            gate,
            clock,
            cancellationAuthorityTransaction: authorityTransaction,
            humanInputCancellationConvergence: convergence);
        var current = await runs.GetAsync(runId).ConfigureAwait(false);
        if (current is null)
        {
            return 3;
        }

        var result = await lifecycle.CancelAsync(new CustomLoopCancelRequest(
            current.Id,
            expectedLifecycleVersion,
            operationId,
            AuditSchema.Actors.Web)).ConfigureAwait(false);
        if (crashBoundary == "ParentReceiptCompleted" && result.Status == CustomLoopControlStatus.Cancelled)
        {
            Environment.Exit(86);
        }
        await File.WriteAllTextAsync(resultPath, result.Status.ToString()).ConfigureAwait(false);
        return result.Status is CustomLoopControlStatus.Cancelled ? 0 : 3;
    }

    private static bool IsCrashBoundary(string crashBoundary)
        => crashBoundary is "RunCancellationCommitted" or "CheckpointRetiredCommitted" or "FinalRunCancelledCommitted" or "ParentReceiptCompleted" or "none"
            || Enum.TryParse<HumanInputRequestPersistenceBoundary>(crashBoundary, ignoreCase: false, out _);
}
