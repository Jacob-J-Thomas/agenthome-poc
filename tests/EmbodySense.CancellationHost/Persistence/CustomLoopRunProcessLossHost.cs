using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.CancellationHost.Persistence;

internal static class CustomLoopRunProcessLossHost
{
    private const int ProcessLossExitCode = 174;
    private static readonly DateTimeOffset _timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");

    internal static async Task<int> RunAsync(string workspaceRoot, string boundaryText)
    {
        if (!Enum.TryParse<CustomLoopRunPublicationBoundary>(boundaryText, out var boundary)
            || boundary is not CustomLoopRunPublicationBoundary.CanonicalRenamed and not CustomLoopRunPublicationBoundary.TargetProven)
        {
            return 2;
        }

        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths, null, (currentBoundary, _) => ExitAfterBoundaryAsync(currentBoundary, boundary));
        _ = await store.CreateAsync(CreateRun());
        return 4;
    }

    private static ValueTask ExitAfterBoundaryAsync(CustomLoopRunPublicationBoundary currentBoundary, CustomLoopRunPublicationBoundary requestedBoundary)
    {
        if (currentBoundary == requestedBoundary)
        {
            Console.Error.WriteLine($"The test host process crashed after `{currentBoundary}`.");
            Console.Error.Flush();
            Environment.Exit(ProcessLossExitCode);
        }

        return ValueTask.CompletedTask;
    }

    private static CustomLoopRunRecord CreateRun()
    {
        const string loopId = "loop-process-loss";
        var definition = CustomLoopDefinition.CreateSeed(loopId, "default-role", "step-1", "create-loop", _timestamp);
        var context = CustomLoopContextSnapshot.CreateEmpty(_timestamp);
        var admitted = new CustomLoopRunEvent(1, "event-1", _timestamp, CustomLoopRunEventKind.Admitted, null, null, null, CustomLoopRunEventKind.Admitted.ToString(), [], null, null, null, null, null, null, null, null, null, null);
        var run = new CustomLoopRunRecord(CustomLoopRunRecord.CurrentSchemaVersion, "run-process-loss", loopId, 1, CustomLoopRunStatus.Admitted, _timestamp, _timestamp, null, "web", new CustomLoopModelSnapshot("openai", "gpt-5"), "invoke-process-loss", "test-user", string.Empty, definition, "Initial prompt", null, context, CustomLoopExecutionClock.NotStarted(), CustomLoopRunCheckpoint.Start(), [admitted], null, null, null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, _timestamp)
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }
}
