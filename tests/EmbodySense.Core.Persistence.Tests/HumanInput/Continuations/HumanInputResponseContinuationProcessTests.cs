using System.Diagnostics;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Persistence.HumanInput.Requests;
using EmbodySense.Core.Persistence.HumanInput.Continuations;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Tests.HumanInput.Requests;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

public sealed class HumanInputResponseContinuationProcessTests
{
    [Theory]
    [InlineData("run", "TargetProven", 1)]
    [InlineData("sleep", "Published", 2)]
    [InlineData("run", "TargetProven", 2)]
    public async Task Process_loss_at_durable_selection_prepared_and_terminal_boundaries_restarts_from_canonical_state(
        string crashPlane,
        string crashBoundary,
        int crashOrdinal)
    {
        await using var scenario = await HumanInputResponseContinuationProcessScenario.CreateAsync();

        using var interrupted = scenario.Start(crashPlane, crashBoundary, crashOrdinal, "interrupted");
        await AssertExitsAsync(interrupted, 86, scenario.Path("interrupted.result"));

        var first = await scenario.RunAsync("first");
        var replay = await scenario.RunAsync("replay");
        var completed = await scenario.ReadRunAsync();

        Assert.True(first is "Submitted" or "Replayed");
        Assert.Equal("Replayed", replay);
        AssertCompletedAcceptedResponse(completed, await scenario.ReadAuditEvidenceAsync());
    }

    [Fact]
    public async Task Two_os_process_workers_converge_through_the_generic_sleep_plane_without_a_human_input_claim_ledger()
    {
        await using var scenario = await HumanInputResponseContinuationProcessScenario.CreateAsync();
        var firstReady = scenario.Path("first.ready");
        var secondReady = scenario.Path("second.ready");
        var release = scenario.Path("release.marker");
        using var first = scenario.Start("none", "none", 1, "first", firstReady, release);
        using var second = scenario.Start("none", "none", 1, "second", secondReady, release);
        await WaitForFileAsync(firstReady);
        await WaitForFileAsync(secondReady);
        await File.WriteAllTextAsync(release, "release");

        await AssertExitsAsync(first, 0, scenario.Path("first.result"));
        await AssertExitsAsync(second, 0, scenario.Path("second.result"));
        var results = new[]
        {
            await File.ReadAllTextAsync(scenario.Path("first.result")),
            await File.ReadAllTextAsync(scenario.Path("second.result")),
        };
        var diagnostic = string.Join(
            " | ",
            await File.ReadAllTextAsync(scenario.Path("first.result.diagnostic")),
            await File.ReadAllTextAsync(scenario.Path("second.result.diagnostic")));
        var completed = await scenario.ReadRunAsync();

        Assert.All(results, result => Assert.True(result is "Submitted" or "Replayed"));
        AssertCompletedAcceptedResponse(completed, await scenario.ReadAuditEvidenceAsync(), diagnostic);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire, GovernedLoopHumanInputWaitingCheckpointPosture.Expired)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject, GovernedLoopHumanInputWaitingCheckpointPosture.Rejected)]
    public async Task Process_terminal_no_response_retires_the_checkpoint_without_a_generic_wake(
        HumanInputRequestLifecycleOperationKind terminalOperation,
        GovernedLoopHumanInputWaitingCheckpointPosture expectedPosture)
    {
        await using var scenario = await HumanInputResponseContinuationProcessScenario.CreateAsync(terminalOperation);

        var result = await scenario.RunAsync("no-response");
        var completed = await scenario.ReadRunAsync();

        Assert.Equal("Retired", result);
        AssertNoResponseFailureRoute(completed, terminalOperation, expectedPosture);
        await AssertNoGenericWakePublicationAsync(scenario);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire, GovernedLoopHumanInputWaitingCheckpointPosture.Expired)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject, GovernedLoopHumanInputWaitingCheckpointPosture.Rejected)]
    public async Task Process_loss_after_routed_no_response_terminalization_is_discovered_from_the_canonical_run(
        HumanInputRequestLifecycleOperationKind terminalOperation,
        GovernedLoopHumanInputWaitingCheckpointPosture expectedPosture)
    {
        await using var scenario = await HumanInputResponseContinuationProcessScenario.CreateAsync(terminalOperation);

        using var interrupted = scenario.Start("run", "TargetProven", 1, "routed-no-response");
        await AssertExitsAsync(interrupted, 86, scenario.Path("routed-no-response.result"));
        var routed = await scenario.ReadRunAsync();
        using var source = scenario.OpenRunStore();
        var page = await new HumanInputResponseContinuationRecoveryStore(source).ListCandidatesAsync(1, null, HumanInputResponseContinuationRecoveryFixture.Now.AddMinutes(2));

        Assert.Equal(CustomLoopRunStatus.Running, routed.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Active, routed.Frontier?.Payload.Status);
        var checkpoint = Assert.Single(routed.HumanInputWaitingCheckpoints);
        Assert.Equal(expectedPosture, checkpoint.Posture);
        Assert.Equal([new HumanInputResponseContinuationCandidate(routed.Id, checkpoint.Binding.CheckpointId, checkpoint.CheckpointHash)], page.Candidates);
        await AssertNoGenericWakePublicationAsync(scenario);

        var recovered = await scenario.RunAsync("routed-no-response-recovery");
        Assert.Equal("Retired", recovered);
        Assert.StartsWith("wake=<none>;", await File.ReadAllTextAsync(scenario.Path("routed-no-response-recovery.result.diagnostic")), StringComparison.Ordinal);
        var converged = await scenario.ReadRunAsync();
        var convergedCheckpoint = AssertNoResponseFailureRoute(converged, terminalOperation, expectedPosture);
        await AssertNoGenericWakePublicationAsync(scenario);

        using var replay = scenario.Start("none", "none", 1, "routed-no-response-replay");
        await AssertExitsAsync(replay, 3, scenario.Path("routed-no-response-replay.result"));
        Assert.Equal("Stale", await File.ReadAllTextAsync(scenario.Path("routed-no-response-replay.result")));
        Assert.StartsWith("wake=<none>;", await File.ReadAllTextAsync(scenario.Path("routed-no-response-replay.result.diagnostic")), StringComparison.Ordinal);
        var replayed = await scenario.ReadRunAsync();
        var replayedCheckpoint = AssertNoResponseFailureRoute(replayed, terminalOperation, expectedPosture);
        Assert.Equal(converged.LifecycleVersion, replayed.LifecycleVersion);
        Assert.Equal(converged.Events.Select(item => item.EventId), replayed.Events.Select(item => item.EventId));
        Assert.Equal(convergedCheckpoint.Evidence.Select(item => item.EvidenceHash), replayedCheckpoint.Evidence.Select(item => item.EvidenceHash));
        await AssertNoGenericWakePublicationAsync(scenario);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("corrupted")]
    public async Task Process_response_ledger_loss_or_corruption_fails_closed_without_advancing_the_waiting_run(string damage)
    {
        await using var scenario = await HumanInputResponseContinuationProcessScenario.CreateAsync();
        scenario.DamageResponseStore(damage);

        using var host = scenario.Start("none", "none", 1, "damaged-response");
        await AssertExitsAsync(host, 3);

        Assert.Equal("Unavailable", await File.ReadAllTextAsync(scenario.Path("damaged-response.result")));
        var waiting = await scenario.ReadRunAsync();
        Assert.Equal(CustomLoopRunStatus.Waiting, waiting.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Waiting, waiting.Frontier?.Payload.Status);
        var checkpoint = Assert.Single(waiting.HumanInputWaitingCheckpoints);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, checkpoint.Posture);
        Assert.Single(checkpoint.Evidence);
    }

    private static async Task AssertExitsAsync(Process process, int expectedExitCode, string? resultPath = null)
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var standardError = await process.StandardError.ReadToEndAsync();
        var result = resultPath is not null && File.Exists(resultPath)
            ? await File.ReadAllTextAsync(resultPath)
            : "<result-not-written>";
        var diagnosticPath = resultPath is null ? null : resultPath + ".diagnostic";
        var diagnostic = diagnosticPath is not null && File.Exists(diagnosticPath)
            ? await File.ReadAllTextAsync(diagnosticPath)
            : "<diagnostic-not-written>";
        Assert.True(process.ExitCode == expectedExitCode, $"Expected exit code {expectedExitCode}, actual {process.ExitCode}. result: {result}; diagnostic: {diagnostic}. stderr: {standardError}");
    }

    private static async Task WaitForFileAsync(string path)
    {
        var startedAt = TimeProvider.System.GetTimestamp();
        while (!File.Exists(path))
        {
            if (TimeProvider.System.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException($"The process readiness marker `{path}` was not published.");
            }

            await Task.Delay(10);
        }
    }

    private static void AssertCompletedAcceptedResponse(CustomLoopRunRecord completed, string auditEvidence, string orderedDiagnostic = "")
    {
        var frontier = completed.Frontier?.Payload.Nodes.Select(node => $"{node.NodeId}:{node.Status}:{node.ControlOutcome}:{string.Join(',', node.SelectedControlEdgeIds)}") ?? [];
        var events = completed.Events.Select(item => $"{item.Sequence}:{item.Kind}:{item.StepId}");
        Assert.True(completed.Status == CustomLoopRunStatus.Completed, $"{completed.Status}: {completed.FailureCode}; {completed.FailureDetail}; frontier: {string.Join('|', frontier)}; events: {string.Join('|', events)}; ordered: {orderedDiagnostic}");
        Assert.Equal(GovernedLoopFrontierStatus.Completed, completed.Frontier?.Payload.Status);
        var condition = Assert.Single(completed.Frontier!.Payload.Nodes, node => string.Equals(node.NodeId, "confirmation-gate", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, condition.Status);
        Assert.Equal(GovernedLoopControlCondition.True, condition.ControlOutcome);
        Assert.Equal(["confirmation-true"], condition.SelectedControlEdgeIds);
        Assert.Equal(
            GovernedLoopNodeExecutionStatus.Completed,
            Assert.Single(completed.Frontier.Payload.Nodes, node => string.Equals(node.NodeId, "safe-result", StringComparison.Ordinal)).Status);
        Assert.Equal("Continue the exact waiting Human Input request.", completed.FinalOutput);
        var checkpoint = Assert.Single(completed.HumanInputWaitingCheckpoints);
        Assert.Equal(3, checkpoint.Evidence.Length);
        Assert.DoesNotContain("Accepted response.", System.Text.Json.JsonSerializer.Serialize(checkpoint));
        Assert.DoesNotContain("Accepted response.", System.Text.Json.JsonSerializer.Serialize(completed.Events));
        Assert.DoesNotContain("Accepted response.", auditEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("Accepted response.", completed.ToString(), StringComparison.Ordinal);
    }

    private static GovernedLoopHumanInputWaitingCheckpoint AssertNoResponseFailureRoute(
        CustomLoopRunRecord completed,
        HumanInputRequestLifecycleOperationKind terminalOperation,
        GovernedLoopHumanInputWaitingCheckpointPosture expectedPosture)
    {
        var failureCode = terminalOperation == HumanInputRequestLifecycleOperationKind.Expire
            ? "human-input-expired"
            : "human-input-rejected";
        var terminalEvidenceKind = expectedPosture == GovernedLoopHumanInputWaitingCheckpointPosture.Expired
            ? GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired
            : GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected;
        Assert.Equal(CustomLoopRunStatus.Failed, completed.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Failed, completed.Frontier?.Payload.Status);
        Assert.Equal(failureCode, completed.FailureCode);
        Assert.Equal($"The loop terminated with classified failure `{failureCode}`.", completed.FailureDetail);
        var failedHumanInput = Assert.Single(completed.Frontier!.Payload.Nodes, node => string.Equals(node.NodeId, "human-input", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopNodeExecutionStatus.Failed, failedHumanInput.Status);
        Assert.Equal(["human-input-to-fail"], failedHumanInput.SelectedControlEdgeIds);
        Assert.Single(completed.Events, item => string.Equals(item.StepId, "human-input", StringComparison.Ordinal) && item.Kind == CustomLoopRunEventKind.NodeAttemptFailed);
        var checkpoint = Assert.Single(completed.HumanInputWaitingCheckpoints);
        Assert.Equal(expectedPosture, checkpoint.Posture);
        Assert.Equal(2, checkpoint.Evidence.Length);
        Assert.Single(checkpoint.Evidence, evidence => evidence.Kind == terminalEvidenceKind);
        Assert.Equal(terminalEvidenceKind, checkpoint.Evidence[^1].Kind);
        return checkpoint;
    }

    private static async Task AssertNoGenericWakePublicationAsync(HumanInputResponseContinuationProcessScenario scenario)
    {
        var sleep = new GovernedLoopSleepStore(new EmbodySense.Core.Common.Workspace.WorkspacePaths(scenario.Path(string.Empty)));
        var catalog = await sleep.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1));

        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Empty, catalog.Status);
        Assert.Empty(catalog.Items);
    }

}
