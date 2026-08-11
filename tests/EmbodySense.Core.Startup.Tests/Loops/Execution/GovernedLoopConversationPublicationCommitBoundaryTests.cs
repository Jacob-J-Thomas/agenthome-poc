using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class GovernedLoopConversationPublicationCommitBoundaryTests
{
    [Fact]
    public async Task Compatible_commit_method_invokes_exact_append_once_for_new_durable_direct_authority()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(ScriptedConversationPublicationAuthorityBehavior.Direct);
        var boundary = CreateBoundary(effect, fixture);
        ConversationPublicationCommitBoundary compatibleBoundary = boundary.CommitAsync;
        var appendCount = 0;

        await compatibleBoundary(_ =>
        {
            appendCount++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(1, appendCount);
        Assert.Equal(1, effect.CallbackInvocations);
        Assert.Equal(GovernedLoopEffectBoundaryKind.ConversationPublication, effect.LastRequest?.BoundaryKind);
        Assert.Equal(ConversationPublicationAuthorityTestFixture.PublicationOperationId, effect.LastRequest?.CorrelationId);
        Assert.Equal(ConversationPublicationAuthorityTestFixture.NodeId, effect.LastRequest?.NodeId);
    }

    [Theory]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.DenyRevoked, GovernedLoopEffectAuthorityDisposition.Deny)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.Pause, GovernedLoopEffectAuthorityDisposition.Pause)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.DenyUnrelatedCeiling, GovernedLoopEffectAuthorityDisposition.Deny)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.DenyExternalPublication, GovernedLoopEffectAuthorityDisposition.Deny)]
    public async Task Durable_deny_pause_and_current_authority_narrowing_stop_with_zero_append(
        ScriptedConversationPublicationAuthorityBehavior behavior,
        GovernedLoopEffectAuthorityDisposition expectedDisposition)
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(behavior);
        var appendCount = 0;

        var stopped = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => CreateBoundary(effect, fixture).CommitAsync(_ =>
        {
            appendCount++;
            return Task.CompletedTask;
        }));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.Decided, stopped.ExecutionStatus);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended, stopped.EvidenceStatus);
        Assert.Equal(expectedDisposition, stopped.Decision?.Disposition);
        Assert.Equal(0, appendCount);
        Assert.Equal(0, effect.CallbackInvocations);
        if (behavior == ScriptedConversationPublicationAuthorityBehavior.DenyUnrelatedCeiling)
        {
            Assert.Equal(GovernedLoopEffectAuthorityReason.CapabilityInactive, stopped.Decision?.Reason);
        }
        else if (behavior == ScriptedConversationPublicationAuthorityBehavior.DenyExternalPublication)
        {
            Assert.Equal(GovernedLoopEffectAuthorityReason.EffectOutsideCeiling, stopped.Decision?.Reason);
        }
    }

    [Theory]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.Invalid, GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.Unavailable, GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable)]
    public async Task Invalid_or_unavailable_authority_stops_with_zero_append(
        ScriptedConversationPublicationAuthorityBehavior behavior,
        GovernedLoopEffectAuthorityExecutionStatus expectedStatus)
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(behavior);
        var appendCount = 0;

        var stopped = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => CreateBoundary(effect, fixture).CommitAsync(_ =>
        {
            appendCount++;
            return Task.CompletedTask;
        }));

        Assert.Equal(expectedStatus, stopped.ExecutionStatus);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, stopped.EvidenceStatus);
        Assert.Null(stopped.Decision);
        Assert.Equal(0, appendCount);
        Assert.Equal(0, effect.CallbackInvocations);
    }

    [Theory]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.Replay, GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.EvidenceUnavailable, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable)]
    public async Task Replay_or_evidence_rejection_requires_reconciliation_with_zero_append(
        ScriptedConversationPublicationAuthorityBehavior behavior,
        GovernedLoopEffectAuthorityEvidenceStoreStatus expectedEvidence)
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(behavior);
        var appendCount = 0;

        var stopped = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => CreateBoundary(effect, fixture).CommitAsync(_ =>
        {
            appendCount++;
            return Task.CompletedTask;
        }));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, stopped.ExecutionStatus);
        Assert.Equal(expectedEvidence, stopped.EvidenceStatus);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Pause, stopped.Decision?.Disposition);
        Assert.Equal(0, appendCount);
        Assert.Equal(0, effect.CallbackInvocations);
    }

    [Fact]
    public async Task Already_present_direct_decision_is_reconciliation_not_permission_to_repeat_append()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(ScriptedConversationPublicationAuthorityBehavior.DirectAlreadyPresent);
        var appendCount = 0;

        var stopped = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => CreateBoundary(effect, fixture).CommitAsync(_ =>
        {
            appendCount++;
            return Task.CompletedTask;
        }));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.Decided, stopped.ExecutionStatus);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, stopped.EvidenceStatus);
        Assert.Equal(GovernedLoopEffectAuthorityDisposition.Direct, stopped.Decision?.Disposition);
        Assert.Equal(0, appendCount);
        Assert.Equal(0, effect.CallbackInvocations);
    }

    [Theory]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.Direct)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.SwallowCallbackFailure)]
    public async Task Publisher_callback_exception_is_preserved_even_when_the_effect_boundary_swallows_it(
        ScriptedConversationPublicationAuthorityBehavior behavior)
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(behavior);
        var expected = new IOException("The durable append failed.");

        var thrown = await Assert.ThrowsAsync<IOException>(() => CreateBoundary(effect, fixture).CommitAsync(_ => Task.FromException(expected)));

        Assert.Same(expected, thrown);
        Assert.Equal(1, effect.CallbackInvocations);
    }

    [Fact]
    public async Task Swallowed_double_callback_is_surfaced_and_only_one_append_crosses()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(ScriptedConversationPublicationAuthorityBehavior.DoubleCallback);
        var appendCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateBoundary(effect, fixture).CommitAsync(_ =>
        {
            appendCount++;
            return Task.CompletedTask;
        }));

        Assert.Equal(1, appendCount);
        Assert.Equal(2, effect.CallbackInvocations);
    }

    [Fact]
    public async Task Callback_captured_by_a_returned_boundary_is_closed_against_late_append()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(ScriptedConversationPublicationAuthorityBehavior.LateCallback);
        var appendCount = 0;

        await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() => CreateBoundary(effect, fixture).CommitAsync(_ =>
        {
            appendCount++;
            return Task.CompletedTask;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => effect.InvokeLateAsync());

        Assert.Equal(0, appendCount);
        Assert.Equal(1, effect.CallbackInvocations);
    }

    [Fact]
    public async Task Unawaited_callback_is_rejected_before_append_and_remains_observable_to_the_boundary()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(ScriptedConversationPublicationAuthorityBehavior.UnawaitedCallback);
        var appendCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateBoundary(effect, fixture).CommitAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            appendCount++;
        }));
        var captured = Assert.IsAssignableFrom<Task>(effect.CapturedCallbackTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => captured);

        Assert.Equal(0, appendCount);
        Assert.Equal(1, effect.CallbackInvocations);
    }

    [Theory]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.NoCallbackDirect)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.NullResult)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.MalformedResult)]
    [InlineData(ScriptedConversationPublicationAuthorityBehavior.MismatchedDecision)]
    public async Task Missing_callback_null_result_or_exact_decision_mismatch_is_a_protocol_failure_with_zero_append(
        ScriptedConversationPublicationAuthorityBehavior behavior)
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(behavior);
        var appendCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateBoundary(effect, fixture).CommitAsync(_ =>
        {
            appendCount++;
            return Task.CompletedTask;
        }));

        Assert.Equal(0, appendCount);
        Assert.Equal(0, effect.CallbackInvocations);
    }

    [Fact]
    public async Task Boundary_is_single_use_and_null_callbacks_fail_before_authority_evaluation()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(ScriptedConversationPublicationAuthorityBehavior.Direct);
        var boundary = CreateBoundary(effect, fixture);

        await Assert.ThrowsAsync<ArgumentNullException>(() => boundary.CommitAsync(null!));
        await boundary.CommitAsync(_ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.CommitAsync(_ => Task.CompletedTask));

        Assert.Equal(1, effect.CallbackInvocations);
    }

    [Fact]
    public void Null_effect_boundary_fails_at_construction()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopConversationPublicationCommitBoundary(
            null!,
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            ConversationPublicationAuthorityTestFixture.NodeId,
            ConversationPublicationAuthorityTestFixture.NodeAttempt,
            ConversationPublicationAuthorityTestFixture.PublicationOperationId));
    }

    private static GovernedLoopConversationPublicationCommitBoundary CreateBoundary(
        ScriptedConversationPublicationEffectAuthorityBoundary effect,
        ConversationPublicationAuthorityTestFixture.Fixture fixture)
        => new(
            effect,
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            ConversationPublicationAuthorityTestFixture.NodeId,
            ConversationPublicationAuthorityTestFixture.NodeAttempt,
            ConversationPublicationAuthorityTestFixture.PublicationOperationId);
}
