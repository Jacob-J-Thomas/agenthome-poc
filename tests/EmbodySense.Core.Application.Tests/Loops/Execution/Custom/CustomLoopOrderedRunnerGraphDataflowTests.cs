using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Canonical_graph_dataflow_binds_the_exact_non_immediate_source_and_projects_that_non_last_source_at_Exit()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition(inferenceCount: 3)),
            artifactFactory: ExactNonImmediateDataflowArtifact);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(
            Result("exact-source-a"),
            Result("unrelated-middle-output"),
            Result("unrelated-last-output"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, $"{result.Detail} Provider requests: {executor.Requests.Count}.");
        var completed = Assert.IsType<CustomLoopRunRecord>(result.Run);
        Assert.Equal(3, executor.Requests.Count);
        var exactConsumerRequest = Assert.Single(executor.Requests, item => item.StepId == "infer-03");
        Assert.Single(exactConsumerRequest.InferenceRequest.Messages, item => item.Content.Contains("exact-source-a", StringComparison.Ordinal));
        Assert.DoesNotContain(exactConsumerRequest.InferenceRequest.Messages, item => item.Content.Contains("unrelated-middle-output", StringComparison.Ordinal));
        Assert.Equal("exact-source-a", completed.FinalOutput);
        Assert.Equal("exact-source-a", completed.Checkpoint.CurrentIterationResult!.Content);
        Assert.Empty(completed.Checkpoint.EarlierRetainedOutputs);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Canonical_graph_cycle_revisits_inference_with_distinct_identity_and_bounded_legacy_projection()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: InferenceConditionCycleArtifact);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("continue"), Result("stop"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, $"{result.Detail} Provider requests: {executor.Requests.Count}.");
        var completed = Assert.IsType<CustomLoopRunRecord>(result.Run);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Contains(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("Initial user prompt", StringComparison.Ordinal));
        Assert.DoesNotContain(executor.Requests[1].InferenceRequest.Messages, item => item.Content.Contains("continue", StringComparison.Ordinal));
        var inferenceCompletions = completed.Events
            .Where(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && item.StepId == "infer-01")
            .OrderBy(item => item.Sequence)
            .ToArray();
        Assert.Equal(2, inferenceCompletions.Length);
        var firstVisit = Assert.IsType<CustomLoopSequentialNodeEvidence>(inferenceCompletions[0].SequentialNodeEvidence);
        var secondVisit = Assert.IsType<CustomLoopSequentialNodeEvidence>(inferenceCompletions[1].SequentialNodeEvidence);
        Assert.NotEqual(firstVisit.ActivationOrdinal, secondVisit.ActivationOrdinal);
        Assert.Equal(1, inferenceCompletions[0].Iteration);
        Assert.Equal(2, inferenceCompletions[1].Iteration);
        Assert.Equal(1, executor.Requests[0].Iteration);
        Assert.Equal(2, executor.Requests[1].Iteration);
        Assert.Equal(1, firstVisit.VisitOrdinal);
        Assert.Equal(2, secondVisit.VisitOrdinal);
        Assert.NotNull(firstVisit.CycleId);
        Assert.Equal(firstVisit.CycleId, secondVisit.CycleId);
        Assert.Equal(1, firstVisit.CycleIteration);
        Assert.Equal(2, secondVisit.CycleIteration);
        Assert.Equal(1, completed.Checkpoint.NextStepIndex);
        Assert.Empty(completed.Checkpoint.EarlierRetainedOutputs);
        Assert.Equal("stop", completed.Checkpoint.CurrentIterationResult!.Content);
        Assert.Equal("stop", completed.FinalOutput);
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Canonical_graph_cycle_resume_reconciles_the_second_visit_without_provider_redispatch()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: InferenceConditionCycleArtifact);
        CustomLoopRunRecord? retainedSecondVisit = null;
        var crashingStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                var completedInferenceVisits = candidate.Events.Count(item =>
                    item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted
                    && item.StepId == "infer-01");
                if (retainedSecondVisit is null && completedInferenceVisits == 2)
                {
                    retainedSecondVisit = candidate;
                    throw new IOException("Simulated process loss after the second cyclic inference outcome was retained.");
                }

                return Task.CompletedTask;
            },
        };
        var firstExecutor = new QueueExecutor(Result("continue"), Result("stop"));
        var firstEvidence = new SequentialEvidenceHarness(crashingStore, context.Evidence);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(crashingStore, firstExecutor), firstEvidence, firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        var retained = Assert.IsType<CustomLoopRunRecord>(retainedSecondVisit);
        Assert.Equal(1, retained.Checkpoint.NextStepIndex);
        Assert.Empty(retained.Checkpoint.EarlierRetainedOutputs);
        var resumeOperationId = "resume-second-cycle-visit";
        var resumable = ResumeReady(retained, resumeOperationId);
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(resumedStore, resumedExecutor), resumedEvidence, resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            resumeOperationId,
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, result.Detail);
        Assert.Equal(2, firstExecutor.Requests.Count);
        Assert.Empty(resumedExecutor.Requests);
        var completed = Assert.IsType<CustomLoopRunRecord>(result.Run);
        Assert.Equal(2, completed.Events.Count(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && item.StepId == "infer-01"));
        Assert.Equal(1, completed.Checkpoint.NextStepIndex);
        Assert.Empty(completed.Checkpoint.EarlierRetainedOutputs);
        Assert.Equal("stop", completed.FinalOutput);
        Assert.Empty(resumedStore.ValidationFailures);
    }

    private static GovernedLoopGraphRevisionArtifact ExactNonImmediateDataflowArtifact(ContextualRoleRevisionPin owningRole)
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer-01", "Produce the exact source value."),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer-02", "Produce an unrelated middle value."),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer-03", "Consume only the exact bound source value."),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            new GovernedLoopControlEdgeDefinition("trigger-to-infer-01", "trigger", "infer-01", GovernedLoopControlCondition.Always),
            new GovernedLoopControlEdgeDefinition("infer-01-to-infer-02", "infer-01", "infer-02", GovernedLoopControlCondition.Success),
            new GovernedLoopControlEdgeDefinition("infer-02-to-infer-03", "infer-02", "infer-03", GovernedLoopControlCondition.Success),
            new GovernedLoopControlEdgeDefinition("infer-03-to-exit", "infer-03", "exit", GovernedLoopControlCondition.Success),
        };
        var bindings = new GovernedLoopBindingDefinition[]
        {
            new("trigger-request-to-infer-01", GovernedLoopBindingKind.Data, "trigger", "request", "infer-01", "request"),
            new("trigger-context-to-infer-01", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-01", "invocation-context"),
            new("infer-01-result-to-infer-02", GovernedLoopBindingKind.Data, "infer-01", "result", "infer-02", "request"),
            new("trigger-context-to-infer-02", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-02", "invocation-context"),
            new("infer-01-result-to-infer-03", GovernedLoopBindingKind.Data, "infer-01", "result", "infer-03", "request"),
            new("trigger-context-to-infer-03", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-03", "invocation-context"),
            new("infer-01-result-to-exit", GovernedLoopBindingKind.Data, "infer-01", "result", "exit", "result"),
        };
        return GovernedLoopSequentialApplicationTestFixture.Artifact(nodes, edges, ["exit"], owningRole, bindings);
    }

    private static GovernedLoopGraphRevisionArtifact InferenceConditionCycleArtifact(ContextualRoleRevisionPin owningRole)
    {
        var inference = GovernedLoopSequentialApplicationTestFixture.Inference("infer-01", "Return continue once, then stop.") with
        {
            Parameters = new Dictionary<string, string>
            {
                ["instruction"] = "Return continue once, then stop.",
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "3",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "4000",
            },
        };
        var condition = new GovernedLoopNodeDefinition(
            "condition",
            GovernedLoopSequentialNodeDescriptors.ModelDecisionCondition,
            [GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopTopologyNodeVocabulary.DecisionPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>
            {
                [GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter] = "continue",
                [GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter] = "stop",
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "3",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "4000",
            });
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            inference,
            condition,
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer-01", GovernedLoopControlCondition.Always),
            new GovernedLoopControlEdgeDefinition("infer-to-condition", "infer-01", "condition", GovernedLoopControlCondition.Success),
            new GovernedLoopControlEdgeDefinition("condition-continue", "condition", "infer-01", GovernedLoopControlCondition.True),
            new GovernedLoopControlEdgeDefinition("condition-stop", "condition", "exit", GovernedLoopControlCondition.False),
        };
        var bindings = new GovernedLoopBindingDefinition[]
        {
            new("trigger-request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer-01", "request"),
            new("trigger-context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-01", "invocation-context"),
            new("inference-result-to-condition", GovernedLoopBindingKind.Data, "infer-01", "result", "condition", GovernedLoopTopologyNodeVocabulary.DecisionPort),
            new("inference-result-to-exit", GovernedLoopBindingKind.Data, "infer-01", "result", "exit", "result"),
        };
        return GovernedLoopSequentialApplicationTestFixture.Artifact(nodes, edges, ["exit"], owningRole, bindings);
    }
}
