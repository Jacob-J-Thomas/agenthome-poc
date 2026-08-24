using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution.Effects;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

public sealed class GovernedLoopCommandActionExecutorTests
{
    [Theory]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopCommandActionExecutionStatus.Completed, CommandActionResultStatus.Committed, CommandActionResultOutcome.Succeeded)]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.Replayed, GovernedLoopEffectOutcome.Failed, GovernedLoopCommandActionExecutionStatus.Failed, CommandActionResultStatus.Replayed, CommandActionResultOutcome.Failed)]
    public async Task ExecuteAsync_projects_one_valid_exact_command_action_to_a_durable_canonical_result(
        GovernedLoopEffectAttemptExecutionStatus effectStatus,
        GovernedLoopEffectOutcome effectOutcome,
        GovernedLoopCommandActionExecutionStatus expectedStatus,
        CommandActionResultStatus expectedResultStatus,
        CommandActionResultOutcome expectedResultOutcome)
    {
        var fixture = CommandActionExecutionTestFixture.Create();
        var service = new CapturingCommandActionEffectService(request => ConclusiveResult(request, effectStatus, effectOutcome));
        var executor = new GovernedLoopCommandActionExecutor(
            new GovernedLoopEffectAttemptFacade(UnusedCommandActionCatalog.Instance, service),
            new CommandActionRegistrationRegistry([fixture.Registration]));

        var result = await executor.ExecuteAsync(fixture.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.True(CommandActionResultContract.TryParse(result.CanonicalOutput, out var canonical));
        Assert.Equal(expectedResultStatus, canonical!.Status);
        Assert.Equal(expectedResultOutcome, canonical.Outcome);
        Assert.Equal("outcome-evidence", canonical.OutcomeEvidenceId);
        Assert.Equal(1, canonical.EffectGeneration);
        var captured = Assert.IsType<GovernedLoopEffectAttemptRequest>(service.Request);
        Assert.Equal(GovernedCommandActionOperation.CreateOperationId(fixture.Registration.Template), captured.ActuatorOperationId);
        Assert.Equal(fixture.Registration.Template.Capability, captured.CapabilityPin.DescriptorIdentity);
        Assert.Equal(fixture.Registration.Template.Implementation, captured.CapabilityPin.Implementation);
        Assert.Equal(fixture.Request.AttemptOperationId, captured.CorrelationId);
        Assert.Equal(1, captured.EffectGeneration);
        Assert.StartsWith("effect-", captured.EffectId, StringComparison.Ordinal);
        Assert.StartsWith("operation-", captured.IdempotencyOperationId, StringComparison.Ordinal);
        Assert.True(CommandActionInputContract.TryParse(captured.InputJson, fixture.Registration.Template, out var input, out _));
        Assert.Empty(input!.Values);
    }

    [Theory]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, GovernedLoopCommandActionExecutionStatus.Rejected)]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, GovernedLoopCommandActionExecutionStatus.Rejected)]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable, GovernedLoopCommandActionExecutionStatus.Rejected)]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.AuthorityStopped, GovernedLoopCommandActionExecutionStatus.Rejected)]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.Conflict, GovernedLoopCommandActionExecutionStatus.Rejected)]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.Backpressured, GovernedLoopCommandActionExecutionStatus.Rejected)]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.ApprovalRequired, GovernedLoopCommandActionExecutionStatus.Rejected)]
    [InlineData(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, GovernedLoopCommandActionExecutionStatus.NeedsReview)]
    public async Task ExecuteAsync_maps_nonconclusive_effect_postures_without_a_canonical_output(
        GovernedLoopEffectAttemptExecutionStatus effectStatus,
        GovernedLoopCommandActionExecutionStatus expectedStatus)
    {
        var fixture = CommandActionExecutionTestFixture.Create();
        var service = new CapturingCommandActionEffectService(_ => new GovernedLoopEffectAttemptExecutionResult(effectStatus, null, "test posture"));
        var executor = new GovernedLoopCommandActionExecutor(
            new GovernedLoopEffectAttemptFacade(UnusedCommandActionCatalog.Instance, service),
            new CommandActionRegistrationRegistry([fixture.Registration]));

        var result = await executor.ExecuteAsync(fixture.Request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.CanonicalOutput);
        Assert.NotNull(service.Request);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_a_stale_attempt_identity_before_the_effect_service()
    {
        var fixture = CommandActionExecutionTestFixture.Create();
        var service = new CapturingCommandActionEffectService(_ => throw new InvalidOperationException("The stale request must not reach the effect service."));
        var executor = new GovernedLoopCommandActionExecutor(
            new GovernedLoopEffectAttemptFacade(UnusedCommandActionCatalog.Instance, service),
            new CommandActionRegistrationRegistry([fixture.Registration]));

        var result = await executor.ExecuteAsync(fixture.Request with { AttemptOperationId = "stale-command-action-1" });

        Assert.Equal(GovernedLoopCommandActionExecutionStatus.Rejected, result.Status);
        Assert.Null(result.CanonicalOutput);
        Assert.Null(service.Request);
        Assert.Contains("invalid or stale", result.Detail, StringComparison.Ordinal);
    }

    private static GovernedLoopEffectAttemptExecutionResult ConclusiveResult(
        GovernedLoopEffectAttemptRequest request,
        GovernedLoopEffectAttemptExecutionStatus status,
        GovernedLoopEffectOutcome outcome)
    {
        var prepared = GovernedLoopEffectAttemptContract.Prepare(
            request.ExecutionBinding,
            request.NodeId,
            request.NodeAttempt,
            request.CapabilityPin.DescriptorIdentity,
            request.CapabilityPin.Implementation,
            request.ActuatorOperationId,
            GovernedLoopEffectAttemptTestFixture.Hash('a'),
            request.EffectId,
            request.IdempotencyOperationId,
            request.EffectGeneration,
            GovernedLoopEffectAttemptTestFixture.HashInput(request.InputJson),
            GovernedLoopEffectAttemptTestFixture.HashInput("command-target"),
            GovernedLoopEffectAttemptTestFixture.Hash('b'),
            request.AdmissionReceipt.ContentHash,
            null,
            CommandActionExecutionTestFixture.Now);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(
            prepared,
            GovernedLoopEffectAttemptTestFixture.Hash('c'),
            CommandActionExecutionTestFixture.Now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(
            authorized,
            GovernedLoopEffectPhase.DispatchBoundaryReached,
            GovernedLoopEffectOutcome.OutcomeUnknown,
            GovernedLoopEffectEvidenceStatus.Pending,
            null,
            null,
            CommandActionExecutionTestFixture.Now.AddSeconds(2));
        var observed = GovernedLoopEffectAttemptContract.Advance(
            crossed,
            GovernedLoopEffectPhase.OutcomeObserved,
            outcome,
            GovernedLoopEffectEvidenceStatus.Complete,
            "outcome-evidence",
            "after-evidence",
            CommandActionExecutionTestFixture.Now.AddSeconds(3));
        var terminal = GovernedLoopEffectAttemptContract.Advance(
            observed,
            GovernedLoopEffectPhase.Committed,
            outcome,
            GovernedLoopEffectEvidenceStatus.Complete,
            "outcome-evidence",
            "after-evidence",
            CommandActionExecutionTestFixture.Now.AddSeconds(4));
        return new GovernedLoopEffectAttemptExecutionResult(status, terminal, "durable test result");
    }
}
