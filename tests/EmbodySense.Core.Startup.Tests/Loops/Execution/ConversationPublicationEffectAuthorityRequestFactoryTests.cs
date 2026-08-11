using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class ConversationPublicationEffectAuthorityRequestFactoryTests
{
    [Fact]
    public void Exact_admission_and_success_exit_derive_one_non_granting_publication_target()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();

        var request = Create(fixture);

        Assert.Same(fixture.Receipt, request.AdmissionReceipt);
        Assert.Same(fixture.Binding, request.ExecutionBinding);
        Assert.Same(fixture.Artifact, request.GraphArtifact);
        Assert.Equal(ConversationPublicationAuthorityTestFixture.NodeId, request.NodeId);
        Assert.Equal(ConversationPublicationAuthorityTestFixture.NodeAttempt, request.NodeAttempt);
        Assert.Equal(ConversationPublicationAuthorityTestFixture.PublicationOperationId, request.CorrelationId);
        Assert.Equal(GovernedLoopEffectBoundaryKind.ConversationPublication, request.BoundaryKind);
        Assert.StartsWith("conversation-publication-", request.EffectOperationId, StringComparison.Ordinal);
        var expectedPin = Assert.Single(
            fixture.Receipt.Evidence.CapabilityAdmission.Pins,
            pin => pin.DescriptorIdentity.Id.Value == ConversationPublicationAuthorityTestFixture.ConversationTurnCapabilityId);
        Assert.Equal(expectedPin, Assert.Single(request.RequiredCapabilityPins));
        Assert.Equal(expectedPin.DescriptorIdentity, Assert.Single(request.RequiredAuthority.Capabilities));
        Assert.Equal(fixture.Receipt.Evidence.EffectiveAuthority.DataClasses, request.RequiredAuthority.DataClasses);
        Assert.Equal(1, request.RequiredAuthority.MaxTargetCount);
        Assert.Equal(CapabilitySideEffectClass.None, request.RequiredAuthority.MaxSideEffectClass);
        Assert.False(request.RequiredAuthority.AllowsRecurrence);
        Assert.True(request.RequiredAuthority.AllowsExternalPublication);
        Assert.False(request.RequiredAuthority.AllowsIrreversibleAction);
    }

    [Fact]
    public void Effect_identity_is_stable_and_changes_with_exact_attempt_publication_run_and_revision()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var first = Create(fixture);

        Assert.Equal(first.EffectOperationId, Create(fixture).EffectOperationId);
        Assert.NotEqual(first.EffectOperationId, Create(fixture, nodeAttempt: 3).EffectOperationId);
        Assert.NotEqual(first.EffectOperationId, Create(fixture, publicationOperationId: "conversation-publication-2").EffectOperationId);
        Assert.NotEqual(
            first.EffectOperationId,
            Create(ConversationPublicationAuthorityTestFixture.Create(runId: "run-publication-2")).EffectOperationId);
        Assert.NotEqual(
            first.EffectOperationId,
            Create(ConversationPublicationAuthorityTestFixture.Create(revisionId: "revision-2")).EffectOperationId);
    }

    [Fact]
    public void Target_fingerprint_is_stable_bounded_and_changes_with_immutable_admission_intent()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();

        var first = GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublicationTargetFingerprint(fixture.Receipt);

        Assert.Equal(64, first.Length);
        Assert.All(first, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.Equal(first, GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublicationTargetFingerprint(fixture.Receipt));
        Assert.NotEqual(
            first,
            GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublicationTargetFingerprint(
                ConversationPublicationAuthorityTestFixture.Create(revisionId: "revision-target-other").Receipt));
    }

    [Fact]
    public void Operation_identity_rejects_missing_or_noncanonical_target_fingerprints()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();

        Assert.Throws<ArgumentNullException>(() =>
            GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublicationTargetFingerprint(null!));
        Assert.Throws<ArgumentException>(() =>
            GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublicationTargetFingerprint(
                fixture.Receipt with { ContentHash = string.Empty }));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublication(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            ConversationPublicationAuthorityTestFixture.NodeId,
            ConversationPublicationAuthorityTestFixture.NodeAttempt,
            ConversationPublicationAuthorityTestFixture.PublicationOperationId,
            new string('A', 64)));
    }

    [Fact]
    public void Unrelated_capability_or_external_publication_narrowing_fails_closed()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var modelIdentity = Assert.Single(
            fixture.Receipt.Evidence.CapabilityAdmission.Pins,
            pin => pin.DescriptorIdentity.Id.Value == ConversationPublicationAuthorityTestFixture.ModelInferenceCapabilityId).DescriptorIdentity;
        var unrelatedReceipt = ConversationPublicationAuthorityTestFixture.WithEffectiveAuthority(
            fixture,
            ConversationPublicationAuthorityTestFixture.EffectiveAuthorityWith(fixture, capabilities: [modelIdentity]));
        var noPublicationReceipt = ConversationPublicationAuthorityTestFixture.WithEffectiveAuthority(
            fixture,
            ConversationPublicationAuthorityTestFixture.EffectiveAuthorityWith(fixture, allowsExternalPublication: false));
        var noTargetReceipt = ConversationPublicationAuthorityTestFixture.WithEffectiveAuthority(
            fixture,
            ConversationPublicationAuthorityTestFixture.EffectiveAuthorityWith(fixture, maxTargetCount: 0));

        Assert.Throws<ArgumentException>(() => Create(fixture with { Receipt = unrelatedReceipt }));
        Assert.Throws<ArgumentException>(() => Create(fixture with { Receipt = noPublicationReceipt }));
        Assert.Throws<ArgumentException>(() => Create(fixture with { Receipt = noTargetReceipt }));
    }

    [Fact]
    public void Confused_deputy_binding_artifact_and_non_exit_node_fail_before_identity_creation()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();
        var replacementBinding = GovernedLoopExecutionBinding.Create(
            fixture.Binding.SchemaVersion,
            fixture.Binding.RunId,
            fixture.Binding.Revision,
            fixture.Binding.ExecutionGeneration + 1);
        var unrelated = ConversationPublicationAuthorityTestFixture.Create(graphId: "other-publication-loop");

        Assert.Throws<ArgumentException>(() => Create(fixture with { Binding = replacementBinding }));
        Assert.Throws<ArgumentException>(() => Create(fixture with { Artifact = unrelated.Artifact }));
        Assert.Throws<ArgumentException>(() => Create(fixture, nodeId: ConversationPublicationAuthorityTestFixture.InferenceNodeId));
    }

    [Fact]
    public void Invalid_attempt_or_unbounded_publication_identity_fails_closed()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => Create(fixture, nodeAttempt: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(fixture, nodeAttempt: GovernedLoopEffectAuthorityContractLimits.MaxNodeAttempt + 1));
        Assert.Throws<ArgumentException>(() => Create(fixture, publicationOperationId: ""));
        Assert.Throws<ArgumentException>(() => Create(fixture, publicationOperationId: "Publication-1"));
        Assert.Throws<ArgumentException>(() => Create(
            fixture,
            publicationOperationId: new string('a', GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters + 1)));
        Assert.Throws<ArgumentException>(() => Create(fixture, publicationOperationId: "publication\0identity"));
    }

    [Fact]
    public void Null_retained_evidence_fails_at_the_public_boundary()
    {
        var fixture = ConversationPublicationAuthorityTestFixture.Create();

        Assert.Throws<ArgumentNullException>(() => ConversationPublicationEffectAuthorityRequestFactory.Create(
            null!, fixture.Binding, fixture.Artifact, ConversationPublicationAuthorityTestFixture.NodeId, 1, ConversationPublicationAuthorityTestFixture.PublicationOperationId));
        Assert.Throws<ArgumentNullException>(() => ConversationPublicationEffectAuthorityRequestFactory.Create(
            fixture.Receipt, null!, fixture.Artifact, ConversationPublicationAuthorityTestFixture.NodeId, 1, ConversationPublicationAuthorityTestFixture.PublicationOperationId));
        Assert.Throws<ArgumentNullException>(() => ConversationPublicationEffectAuthorityRequestFactory.Create(
            fixture.Receipt, fixture.Binding, null!, ConversationPublicationAuthorityTestFixture.NodeId, 1, ConversationPublicationAuthorityTestFixture.PublicationOperationId));
    }

    private static EmbodySense.Core.Application.Loops.Execution.Authority.Models.GovernedLoopEffectAuthorityRequest Create(
        ConversationPublicationAuthorityTestFixture.Fixture fixture,
        string nodeId = ConversationPublicationAuthorityTestFixture.NodeId,
        int nodeAttempt = ConversationPublicationAuthorityTestFixture.NodeAttempt,
        string publicationOperationId = ConversationPublicationAuthorityTestFixture.PublicationOperationId)
        => ConversationPublicationEffectAuthorityRequestFactory.Create(
            fixture.Receipt,
            fixture.Binding,
            fixture.Artifact,
            nodeId,
            nodeAttempt,
            publicationOperationId);
}
