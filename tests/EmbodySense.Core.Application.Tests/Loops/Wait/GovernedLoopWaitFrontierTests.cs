using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Wait;

public sealed class GovernedLoopWaitFrontierTests
{
    [Fact]
    public void Plan_retains_exact_wait_parameters_and_frontier_parks_then_resumes_contiguously()
    {
        var context = GovernedLoopWaitApplicationTestFixture.CreateTimestampContext();
        var node = context.DispatchRequest.Node;

        Assert.Same(context.Plan.Nodes[node.Ordinal], node);
        Assert.Equal(GovernedLoopSequentialNodeDescriptors.TimestampWait, node.Descriptor);
        Assert.Equal(
            context.DeadlineUtc!.Value.ToString(
                EmbodySense.Core.Common.Loops.Execution.Wait.GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat,
                System.Globalization.CultureInfo.InvariantCulture),
            node.Parameters[EmbodySense.Core.Common.Loops.Execution.Wait.GovernedLoopWaitVocabulary.DeadlineUtcParameter]);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IDictionary<string, string>>(node.Parameters).Add("other", "value"));

        var parked = GovernedLoopSequentialFrontierMachine.ParkRunning(
            context.RunningFrontier,
            context.Binding,
            context.Plan,
            node,
            context.DispatchRequest.Activation,
            context.DispatchRequest.Attempt,
            context.DispatchRequest.Activation.AttemptOperationId,
            GovernedLoopWaitApplicationTestFixture.Now.AddSeconds(1));
        var waiting = Assert.IsType<EmbodySense.Core.Common.Loops.Execution.GovernedLoopFrontierPosture>(parked.Frontier);

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, parked.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Waiting, waiting.Payload.Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Waiting, waiting.Payload.Nodes[1].Status);
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Waiting, GovernedLoopSequentialFrontierMachine.Select(waiting, context.Binding, context.Plan).Status);

        var resumed = GovernedLoopSequentialFrontierMachine.ResumeWaiting(
            waiting,
            context.Binding,
            context.Plan,
            waiting.Payload.Nodes[1],
            1,
            "wait-attempt-1",
            GovernedLoopWaitApplicationTestFixture.Now.AddSeconds(2));

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, resumed.Status);
        Assert.Equal(waiting.Payload.FrontierVersion + 1, resumed.Frontier!.Payload.FrontierVersion);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, resumed.Frontier.Payload.Nodes[1].Status);
    }

    [Fact]
    public void Frontier_rejects_wrong_descriptor_activation_operation_and_substituted_wait()
    {
        var context = GovernedLoopWaitApplicationTestFixture.CreateTimestampContext();
        var wrongNode = context.Plan.Nodes[0];
        var activation = context.DispatchRequest.Activation;

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, GovernedLoopSequentialFrontierMachine.ParkRunning(
            context.RunningFrontier,
            context.Binding,
            context.Plan,
            wrongNode,
            activation,
            1,
            activation.AttemptOperationId,
            GovernedLoopWaitApplicationTestFixture.Now).Status);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, GovernedLoopSequentialFrontierMachine.ParkRunning(
            context.RunningFrontier,
            context.Binding,
            context.Plan,
            context.DispatchRequest.Node,
            activation,
            1,
            "other-operation",
            GovernedLoopWaitApplicationTestFixture.Now).Status);

        var parked = GovernedLoopSequentialFrontierMachine.ParkRunning(
            context.RunningFrontier,
            context.Binding,
            context.Plan,
            context.DispatchRequest.Node,
            activation,
            1,
            activation.AttemptOperationId,
            GovernedLoopWaitApplicationTestFixture.Now).Frontier!;
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, GovernedLoopSequentialFrontierMachine.ResumeWaiting(
            parked,
            context.Binding,
            context.Plan,
            parked.Payload.Nodes[1],
            1,
            "other-operation",
            GovernedLoopWaitApplicationTestFixture.Now.AddSeconds(1)).Status);
    }

    [Fact]
    public void Waiting_frontier_enters_review_without_completion_or_route_evidence()
    {
        var context = GovernedLoopWaitApplicationTestFixture.CreateTimestampContext();
        var parked = Assert.IsType<EmbodySense.Core.Common.Loops.Execution.GovernedLoopFrontierPosture>(
            GovernedLoopSequentialFrontierMachine.ParkRunning(
                context.RunningFrontier,
                context.Binding,
                context.Plan,
                context.DispatchRequest.Node,
                context.DispatchRequest.Activation,
                context.DispatchRequest.Attempt,
                context.DispatchRequest.Activation.AttemptOperationId,
                GovernedLoopWaitApplicationTestFixture.Now).Frontier);

        var blocked = GovernedLoopSequentialFrontierMachine.ReviewBlockWaiting(
            parked,
            context.Binding,
            context.Plan,
            parked.Payload.Nodes[1],
            "wait-attention-1",
            GovernedLoopWaitApplicationTestFixture.Hash('8'),
            GovernedLoopWaitApplicationTestFixture.Now.AddSeconds(1));

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, blocked.Status);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, blocked.Frontier!.Payload.Status);
        var activation = blocked.Frontier.Payload.Nodes[1];
        Assert.Equal(GovernedLoopNodeExecutionStatus.ReviewBlocked, activation.Status);
        Assert.Equal("wait-attention-1", activation.OutcomeEvidenceId);
        Assert.Equal(GovernedLoopWaitApplicationTestFixture.Hash('8'), activation.OutcomeEvidenceHash);
        Assert.Null(activation.ControlOutcome);
        Assert.Empty(activation.SelectedControlEdgeIds);
        Assert.Empty(activation.SkippedControlEdgeIds);
    }
}
