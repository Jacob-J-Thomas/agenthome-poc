using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Reconciliation_required_workspace_action_opens_attention_only_after_the_terminal_run_is_durable()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => GovernedLoopSequentialApplicationTestFixture.WorkspaceActionArtifact(owningRole: role));
        var store = new FakeRunStore(context.Run);
        var action = new ReconciliationRequiredWorkspaceActionExecutor();
        var admission = new RecordingGovernedLoopEffectReconciliationAdmissionService(GovernedLoopEffectReconciliationAdmissionStatus.Opened, () => store.Current);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("bounded provider output")), workspaceActionExecutor: action, effectReconciliationAdmissionService: admission),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run?.Status);
        Assert.Equal("workspace_action_reconciliation_required", result.Run?.FailureCode);
        Assert.True(admission.ObservedExactDurableRun);
        var request = Assert.Single(admission.Requests);
        var ambiguity = Assert.Single(result.Run!.Events, item => item.EffectReconciliationBinding is not null);
        Assert.Equal(request.Binding, ambiguity.EffectReconciliationBinding);
        Assert.Equal(request.Run, result.Run);
        Assert.Single(action.Requests);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.IntegrityWarning);
        Assert.True(CustomLoopRunValidator.Validate(result.Run).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(result.Run).Errors));
    }

    [Fact]
    public async Task Reconciliation_attention_admission_failure_preserves_command_ambiguity_and_appends_one_warning()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => GovernedLoopSequentialApplicationTestFixture.CommandActionOnlyArtifact(owningRole: role));
        var store = new FakeRunStore(context.Run);
        var action = new ReconciliationRequiredCommandActionExecutor();
        var admission = new RecordingGovernedLoopEffectReconciliationAdmissionService(GovernedLoopEffectReconciliationAdmissionStatus.Denied, () => store.Current);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(), commandActionExecutor: action, effectReconciliationAdmissionService: admission),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run?.Status);
        Assert.Equal("command_action_reconciliation_required", result.Run?.FailureCode);
        Assert.True(admission.ObservedExactDurableRun);
        Assert.Single(admission.Requests);
        Assert.Single(action.Requests);
        var warning = Assert.Single(result.Run!.Events, item => item.Kind == CustomLoopRunEventKind.IntegrityWarning);
        Assert.Contains("could not be published (Denied)", warning.Detail, StringComparison.Ordinal);
        Assert.True(CustomLoopRunValidator.Validate(result.Run).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(result.Run).Errors));
    }
}
