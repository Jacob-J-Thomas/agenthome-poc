using System.Diagnostics;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Publication;

public sealed class HumanInputRequestPublicationProcessTests
{
    [Fact]
    public async Task Process_loss_after_canonical_checkpoint_durability_before_request_create_acknowledgement_recovers_one_publication()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();

        using var interrupted = scenario.Start("TrustInitialized", "checkpoint-durable-before-create");
        await AssertExitsAsync(interrupted, 86, scenario.Path("checkpoint-durable-before-create.result"));

        var recovered = await scenario.RunAsync("checkpoint-durable-recovery");
        var replayed = await scenario.RunAsync("checkpoint-durable-replay");

        Assert.Equal("Published", recovered);
        Assert.Equal("Replayed", replayed);
        await AssertExactlyOneCreateAsync(scenario);
    }

    [Fact]
    public async Task Process_loss_after_request_create_durability_before_caller_acknowledgement_replays_without_a_second_delivery_opportunity()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();

        using var interrupted = scenario.Start("TrustAdvanced", "create-durable-before-acknowledgement");
        await AssertExitsAsync(interrupted, 86, scenario.Path("create-durable-before-acknowledgement.result"));
        await AssertExactlyOneCreateAsync(scenario);

        var recovered = await scenario.RunAsync("create-durable-recovery");
        var replayed = await scenario.RunAsync("create-durable-replay");

        Assert.Equal("Replayed", recovered);
        Assert.Equal("Replayed", replayed);
        await AssertExactlyOneCreateAsync(scenario);
    }

    [Fact]
    public async Task Process_loss_after_durable_run_cancellation_request_replays_human_input_convergence_without_reopening_or_duplicate_cancel()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();
        Assert.Equal("Published", await scenario.RunAsync("publish-before-run-cancellation-crash"));

        using var interrupted = scenario.StartCancellation("RunCancellationCommitted", "run-cancellation-committed");
        await AssertExitsAsync(interrupted, 86, scenario.Path("run-cancellation-committed.result"));
        var requested = Assert.IsType<CustomLoopRunRecord>(await scenario.ReadRunAsync());

        Assert.Equal(CustomLoopRunStatus.CancelRequested, requested.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(requested.HumanInputWaitingCheckpoints).Posture);
        await AssertExactlyOneCreateAsync(scenario);

        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("run-cancellation-recovery"));
        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("run-cancellation-replay"));

        await AssertExactlyOneCreateAndCancelAsync(scenario);
        var terminal = Assert.IsType<CustomLoopRunRecord>(await scenario.ReadRunAsync());
        Assert.Equal(CustomLoopRunStatus.Cancelled, terminal.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(terminal.HumanInputWaitingCheckpoints).Posture);
    }

    [Fact]
    public async Task Process_loss_after_durable_request_cancel_replays_the_parent_control_operation_without_a_second_cancel()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();
        Assert.Equal("Published", await scenario.RunAsync("publish-before-request-cancellation-crash"));

        using var interrupted = scenario.StartCancellation("TrustAdvanced", "request-cancellation-committed");
        await AssertExitsAsync(interrupted, 86, scenario.Path("request-cancellation-committed.result"));
        var requested = Assert.IsType<CustomLoopRunRecord>(await scenario.ReadRunAsync());

        Assert.Equal(CustomLoopRunStatus.CancelRequested, requested.Status);
        await AssertExactlyOneCreateAndCancelAsync(scenario);

        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("request-cancellation-recovery"));
        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("request-cancellation-replay"));

        await AssertExactlyOneCreateAndCancelAsync(scenario);
        var terminal = Assert.IsType<CustomLoopRunRecord>(await scenario.ReadRunAsync());
        Assert.Equal(CustomLoopRunStatus.Cancelled, terminal.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(terminal.HumanInputWaitingCheckpoints).Posture);
    }

    [Fact]
    public async Task Process_loss_after_durable_checkpoint_retirement_replays_without_reopening_or_duplicate_cancel()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();
        Assert.Equal("Published", await scenario.RunAsync("publish-before-checkpoint-retirement-crash"));

        using var interrupted = scenario.StartCancellation("CheckpointRetiredCommitted", "checkpoint-retirement-committed");
        await AssertExitsAsync(interrupted, 86, scenario.Path("checkpoint-retirement-committed.result"));
        var requested = Assert.IsType<CustomLoopRunRecord>(await scenario.ReadRunAsync());

        Assert.Equal(CustomLoopRunStatus.CancelRequested, requested.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(requested.HumanInputWaitingCheckpoints).Posture);
        await AssertExactlyOneCreateAndCancelAsync(scenario);

        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("checkpoint-retirement-recovery"));
        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("checkpoint-retirement-replay"));

        await AssertExactlyOneCreateAndCancelAsync(scenario);
        await AssertCompletedCancellationReceiptAsync(scenario);
        var terminal = Assert.IsType<CustomLoopRunRecord>(await scenario.ReadRunAsync());
        Assert.Equal(CustomLoopRunStatus.Cancelled, terminal.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(terminal.HumanInputWaitingCheckpoints).Posture);
    }

    [Fact]
    public async Task Process_loss_after_final_cancelled_run_commit_replays_the_pending_parent_receipt_without_redispatch()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();
        Assert.Equal("Published", await scenario.RunAsync("publish-before-final-run-crash"));

        using var interrupted = scenario.StartCancellation("FinalRunCancelledCommitted", "final-run-cancelled-committed");
        await AssertExitsAsync(interrupted, 86, scenario.Path("final-run-cancelled-committed.result"));
        var terminalBeforeReceipt = Assert.IsType<CustomLoopRunRecord>(await scenario.ReadRunAsync());

        Assert.Equal(CustomLoopRunStatus.Cancelled, terminalBeforeReceipt.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(terminalBeforeReceipt.HumanInputWaitingCheckpoints).Posture);
        await AssertExactlyOneCreateAndCancelAsync(scenario);

        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("final-run-cancelled-recovery"));
        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("final-run-cancelled-replay"));

        await AssertExactlyOneCreateAndCancelAsync(scenario);
        await AssertCompletedCancellationReceiptAsync(scenario);
    }

    [Fact]
    public async Task Process_loss_after_parent_cancellation_receipt_completion_replays_the_completed_canonical_result_without_redispatch()
    {
        await using var scenario = await HumanInputRequestPublicationProcessScenario.CreateAsync();
        Assert.Equal("Published", await scenario.RunAsync("publish-before-parent-receipt-crash"));

        using var interrupted = scenario.StartCancellation("ParentReceiptCompleted", "parent-receipt-completed");
        await AssertExitsAsync(interrupted, 86, scenario.Path("parent-receipt-completed.result"));
        var terminal = Assert.IsType<CustomLoopRunRecord>(await scenario.ReadRunAsync());

        Assert.Equal(CustomLoopRunStatus.Cancelled, terminal.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(terminal.HumanInputWaitingCheckpoints).Posture);
        await AssertExactlyOneCreateAndCancelAsync(scenario);
        await AssertCompletedCancellationReceiptAsync(scenario);

        Assert.Equal("Cancelled", await scenario.RunCancellationAsync("parent-receipt-replay"));

        await AssertExactlyOneCreateAndCancelAsync(scenario);
        await AssertCompletedCancellationReceiptAsync(scenario);
    }

    private static async Task AssertExactlyOneCreateAsync(HumanInputRequestPublicationProcessScenario scenario)
    {
        var read = await scenario.ReadRequestAsync();

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, read.Status);
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(read.PrimarySnapshot);
        Assert.Single(snapshot.RequestVersions);
        var create = Assert.Single(snapshot.Operations);
        Assert.Equal(HumanInputRequestLifecycleOperationKind.Create, create.Kind);
        Assert.Equal(HumanInputRequestLifecycleOperationOutcome.Committed, create.Outcome);
        Assert.Equal(snapshot.RequestVersions[0].RequestId, create.TargetRequestId);
        var candidate = Assert.IsType<HumanInputRequestReference>(create.CandidateRequest);
        Assert.Equal(snapshot.RequestVersions[0].RequestHash, candidate.RequestHash);
    }

    private static async Task AssertExactlyOneCreateAndCancelAsync(HumanInputRequestPublicationProcessScenario scenario)
    {
        var read = await scenario.ReadRequestAsync();

        Assert.Equal(HumanInputRequestLifecycleStoreReadStatus.Ready, read.Status);
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(read.PrimarySnapshot);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, snapshot.Head.Status);
        Assert.Single(snapshot.RequestVersions);
        Assert.Equal(
            [HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationKind.Cancel],
            snapshot.Operations.Select(operation => operation.Kind));
        Assert.Single(snapshot.Operations, operation => operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    private static async Task AssertCompletedCancellationReceiptAsync(HumanInputRequestPublicationProcessScenario scenario)
    {
        var operation = Assert.IsType<CustomLoopControlOperation>(await scenario.ReadCancellationOperationAsync());

        Assert.Equal(CustomLoopControlOperationState.Complete, operation.State);
        Assert.Equal(CustomLoopControlStatus.Cancelled, operation.Outcome);
        Assert.Equal(CustomLoopRunStatus.Cancelled, operation.ResultRunStatus);
    }

    private static async Task AssertExitsAsync(Process process, int expectedExitCode, string resultPath)
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var standardError = await process.StandardError.ReadToEndAsync();
        var result = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath).ConfigureAwait(false) : "<result-not-written>";
        Assert.True(process.ExitCode == expectedExitCode, $"Expected exit code {expectedExitCode}, actual {process.ExitCode}. result: {result}; stderr: {standardError}");
    }
}
