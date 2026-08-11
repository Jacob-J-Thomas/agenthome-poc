using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class GovernedLoopConversationPublicationAuthorityBoundaryProviderTests
{
    [Fact]
    public async Task Provider_projects_complete_exact_success_exit_proof_into_one_direct_commit_boundary()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(
            ScriptedConversationPublicationAuthorityBehavior.Direct);
        var provider = new GovernedLoopConversationPublicationAuthorityBoundaryProvider(effect);
        var request = Request(fixture);
        var appendCount = 0;

        var boundary = provider.CreateCommitBoundary(request);
        await boundary(
            _ =>
            {
                appendCount++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, appendCount);
        Assert.Equal(1, effect.CallbackInvocations);
        Assert.Equal(request.AdmissionReceipt, effect.LastRequest?.AdmissionReceipt);
        Assert.Equal(request.ExecutionBinding, effect.LastRequest?.ExecutionBinding);
        Assert.Equal(request.GraphArtifact, effect.LastRequest?.GraphArtifact);
        Assert.Equal(request.NodeId, effect.LastRequest?.NodeId);
        Assert.Equal(request.NodeAttempt, effect.LastRequest?.NodeAttempt);
        Assert.Equal(request.PublicationOperationId, effect.LastRequest?.CorrelationId);
        Assert.Equal(GovernedLoopEffectBoundaryKind.ConversationPublication, effect.LastRequest?.BoundaryKind);
    }

    [Fact]
    public async Task Provider_preserves_replay_as_review_evidence_without_crossing_the_append()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(
            ScriptedConversationPublicationAuthorityBehavior.Replay);
        var provider = new GovernedLoopConversationPublicationAuthorityBoundaryProvider(effect);
        var appendCount = 0;

        var stopped = await Assert.ThrowsAsync<GovernedLoopEffectAuthorityStoppedException>(() =>
            provider.CreateCommitBoundary(Request(fixture))(
                _ =>
                {
                    appendCount++;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, stopped.ExecutionStatus);
        Assert.Equal(GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent, stopped.EvidenceStatus);
        Assert.Equal(0, appendCount);
        Assert.Equal(0, effect.CallbackInvocations);
    }

    [Fact]
    public void Provider_rejects_non_success_exit_proof_before_effect_authority_evaluation()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(
            ScriptedConversationPublicationAuthorityBehavior.Direct);
        var provider = new GovernedLoopConversationPublicationAuthorityBoundaryProvider(effect);

        Assert.Throws<ArgumentException>(() => provider.CreateCommitBoundary(
            Request(fixture) with { NodeId = ConversationPublicationAuthorityTestFixture.InferenceNodeId }));
        Assert.Null(effect.LastRequest);
        Assert.Equal(0, effect.CallbackInvocations);
    }

    [Fact]
    public void Provider_rejects_missing_dependencies_and_request()
    {
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopConversationPublicationAuthorityBoundaryProvider(null!));

        var effect = new ScriptedConversationPublicationEffectAuthorityBoundary(
            ScriptedConversationPublicationAuthorityBehavior.Direct);
        var provider = new GovernedLoopConversationPublicationAuthorityBoundaryProvider(effect);
        Assert.Throws<ArgumentNullException>(() => provider.CreateCommitBoundary(null!));
        Assert.Null(effect.LastRequest);
    }

    private static GovernedLoopConversationPublicationAuthorityRequest Request(
        ConversationPublicationAuthorityTestFixture.Fixture fixture)
        => new(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            ConversationPublicationAuthorityTestFixture.NodeId,
            ConversationPublicationAuthorityTestFixture.NodeAttempt,
            ConversationPublicationAuthorityTestFixture.PublicationOperationId);
}
