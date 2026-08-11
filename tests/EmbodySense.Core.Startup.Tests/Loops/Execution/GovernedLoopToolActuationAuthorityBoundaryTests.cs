using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class GovernedLoopToolActuationAuthorityBoundaryTests
{
    [Fact]
    public async Task Direct_authority_invokes_once_and_returns_the_same_execution_instance()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var effect = new ScriptedGovernedLoopEffectAuthorityBoundary(ScriptedEffectAuthorityBehavior.Direct);
        var boundary = CreateBoundary(effect, fixture);
        ToolActuationAuthorityExecution? supplied = null;
        var callbackCount = 0;

        var result = await boundary.ExecuteAsync(
            fixture.ToolRequest,
            (execution, _) =>
            {
                callbackCount++;
                supplied = execution;
                return Task.FromResult(true);
            });

        Assert.Equal(ToolActuationAuthorityDisposition.Direct, result.Disposition);
        Assert.Same(supplied, result);
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, effect.CallbackInvocations);
        Assert.Equal(GovernedLoopEffectBoundaryKind.WorkspaceActuation, effect.LastRequest?.BoundaryKind);
        Assert.Equal(effect.LastRequest?.EffectOperationId, result.AuditMetadata["effect_operation_id"]);
        Assert.Equal("direct", result.AuditMetadata["effect_authority_disposition"]);
    }

    [Theory]
    [InlineData(ScriptedEffectAuthorityBehavior.Deny, ToolActuationAuthorityDisposition.Denied)]
    [InlineData(ScriptedEffectAuthorityBehavior.Pause, ToolActuationAuthorityDisposition.ReviewRequired)]
    public async Task Durable_deny_or_pause_preserves_typed_posture_with_zero_actuation(
        ScriptedEffectAuthorityBehavior behavior,
        ToolActuationAuthorityDisposition expected)
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var effect = new ScriptedGovernedLoopEffectAuthorityBoundary(behavior);
        var callbackCount = 0;

        var result = await CreateBoundary(effect, fixture).ExecuteAsync(
            fixture.ToolRequest,
            (_, _) => Task.FromResult(++callbackCount));

        Assert.Equal(expected, result.Disposition);
        Assert.Equal(0, callbackCount);
        Assert.Equal(0, effect.CallbackInvocations);
        Assert.Equal(behavior == ScriptedEffectAuthorityBehavior.Deny ? "deny" : "pause", result.AuditMetadata["effect_authority_disposition"]);
        Assert.NotNull(result.AuditMetadata["effect_authority_decision_hash"]);
    }

    [Theory]
    [InlineData(ScriptedEffectAuthorityBehavior.Invalid)]
    [InlineData(ScriptedEffectAuthorityBehavior.Unavailable)]
    [InlineData(ScriptedEffectAuthorityBehavior.ReplayAmbiguous)]
    public async Task Unresolved_replayed_or_mismatched_authority_is_ambiguous_with_zero_actuation(ScriptedEffectAuthorityBehavior behavior)
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var effect = new ScriptedGovernedLoopEffectAuthorityBoundary(behavior);
        var callbackCount = 0;

        var result = await CreateBoundary(effect, fixture).ExecuteAsync(
            fixture.ToolRequest,
            (_, _) => Task.FromResult(++callbackCount));

        Assert.Equal(ToolActuationAuthorityDisposition.Ambiguous, result.Disposition);
        Assert.Equal(0, callbackCount);
        Assert.Equal(0, effect.CallbackInvocations);
        if (behavior == ScriptedEffectAuthorityBehavior.ReplayAmbiguous)
        {
            Assert.Equal("alreadypresent", result.AuditMetadata["effect_authority_evidence_status"]);
            Assert.Equal("pause", result.AuditMetadata["effect_authority_disposition"]);
        }
    }

    [Theory]
    [InlineData(ScriptedEffectAuthorityBehavior.MismatchedOperation)]
    [InlineData(ScriptedEffectAuthorityBehavior.ForgedAdmittedProof)]
    public async Task Structurally_valid_but_inexact_decisions_are_protocol_failures_with_zero_actuation(
        ScriptedEffectAuthorityBehavior behavior)
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var effect = new ScriptedGovernedLoopEffectAuthorityBoundary(behavior);
        var callbackCount = 0;

        await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => CreateBoundary(effect, fixture).ExecuteAsync(
            fixture.ToolRequest,
            (_, _) => Task.FromResult(++callbackCount)));

        Assert.Equal(0, callbackCount);
        Assert.Equal(0, effect.CallbackInvocations);
    }

    [Fact]
    public async Task Swallowed_double_callback_violation_is_still_surfaced_and_second_actuation_is_blocked()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var effect = new ScriptedGovernedLoopEffectAuthorityBoundary(ScriptedEffectAuthorityBehavior.DoubleCallback);
        var callbackCount = 0;

        await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => CreateBoundary(effect, fixture).ExecuteAsync(
            fixture.ToolRequest,
            (_, _) => Task.FromResult(++callbackCount)));

        Assert.Equal(1, callbackCount);
        Assert.Equal(2, effect.CallbackInvocations);
    }

    [Fact]
    public async Task Callback_captured_by_a_returned_boundary_is_closed_against_late_actuation()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var effect = new ScriptedGovernedLoopEffectAuthorityBoundary(ScriptedEffectAuthorityBehavior.LateCallback);
        var callbackCount = 0;
        var result = await CreateBoundary(effect, fixture).ExecuteAsync(
            fixture.ToolRequest,
            (_, _) => Task.FromResult(++callbackCount));

        Assert.Equal(ToolActuationAuthorityDisposition.ReviewRequired, result.Disposition);
        await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => effect.InvokeLateAsync());
        Assert.Equal(0, callbackCount);
        Assert.Equal(1, effect.CallbackInvocations);
    }

    [Fact]
    public async Task Callback_protocol_violation_is_never_translated_into_an_authority_posture()
    {
        var fixture = WorkspaceToolAuthorityTestFixture.Create();
        var effect = new ScriptedGovernedLoopEffectAuthorityBoundary(ScriptedEffectAuthorityBehavior.Direct);
        var violation = new ToolActuationAuthorityProtocolException("The broker rejected the direct callback protocol.");

        var thrown = await Assert.ThrowsAsync<ToolActuationAuthorityProtocolException>(() => CreateBoundary(effect, fixture).ExecuteAsync<bool>(
            fixture.ToolRequest,
            (_, _) => Task.FromException<bool>(violation)));

        Assert.Same(violation, thrown);
        Assert.Equal(1, effect.CallbackInvocations);
    }

    private static GovernedLoopToolActuationAuthorityBoundary CreateBoundary(
        ScriptedGovernedLoopEffectAuthorityBoundary effect,
        (
            EmbodySense.Core.Common.Loops.Admission.Models.GovernedLoopAdmissionReceipt Receipt,
            EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionBinding Binding,
            EmbodySense.Core.Common.Loops.Revisions.Models.GovernedLoopGraphRevisionArtifact Artifact,
            EmbodySense.Core.Common.Governance.Tools.Models.ToolRequest ToolRequest) fixture)
        => new(
            effect,
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            WorkspaceToolAuthorityTestFixture.NodeId,
            WorkspaceToolAuthorityTestFixture.NodeAttempt,
            WorkspaceToolAuthorityTestFixture.ServerCorrelationId);
}
