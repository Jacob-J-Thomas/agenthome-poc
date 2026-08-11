using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal static class ConversationPublicationAuthorityTestFixture
{
    internal const string NodeId = "exit";
    internal const string InferenceNodeId = "infer-01";
    internal const int NodeAttempt = 2;
    internal const string PublicationOperationId = "conversation-publication-1";
    internal const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    internal const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    internal static readonly DateTimeOffset Now = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    internal static Fixture Create(
        string runId = "run-publication-1",
        string graphId = "publication-loop",
        string revisionId = "revision-1")
    {
        var artifact = CreateArtifact(graphId, revisionId);
        var binding = GovernedLoopExecutionBinding.Create(1, runId, artifact.RevisionArtifact.Revision, 1);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, binding.Revision, $"publish-{revisionId}", Hash('7'));
        Assert.True(AuthorityGrantId.TryParse("grant-publication", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var grantRevision, out _));
        Assert.True(AuthorityProfileId.TryParse("profile-publication", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        Assert.True(AuthorityProfileHash.TryParse("sha256:" + Hash('8'), out var profileHash, out _));
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actorId, out _));
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            WorkspaceId,
            $"admit-{runId}",
            Hash('1'),
            publication,
            new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + Hash('a')),
            artifact.Graph.OwningRole,
            actorId!,
            "web",
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var capabilityAdmission = TestCapabilityAdmissionFactory.Create(CreateManifest(), Now) with { WorkspaceScopeId = WorkspaceId };
        var effectiveAuthority = new AuthorityCeiling(
            capabilityAdmission.Pins.Select(item => item.DescriptorIdentity).ToArray(),
            [DataClass("conversation-content"), DataClass("conversation-metadata")],
            3,
            CapabilitySideEffectClass.LocalReversible,
            true,
            true,
            true);
        var receipt = CreateReceipt(intent, binding, capabilityAdmission, effectiveAuthority);
        return new Fixture(receipt, binding, artifact);
    }

    internal static GovernedLoopAdmissionReceipt WithEffectiveAuthority(Fixture fixture, AuthorityCeiling effectiveAuthority)
        => CreateReceipt(
            fixture.Receipt.Intent,
            fixture.Binding,
            fixture.Receipt.Evidence.CapabilityAdmission,
            effectiveAuthority);

    internal static AuthorityCeiling EffectiveAuthorityWith(
        Fixture fixture,
        IReadOnlyList<CapabilityDescriptorIdentity>? capabilities = null,
        int? maxTargetCount = null,
        bool? allowsExternalPublication = null)
    {
        var admitted = fixture.Receipt.Evidence.EffectiveAuthority;
        return new AuthorityCeiling(
            capabilities ?? admitted.Capabilities,
            admitted.DataClasses,
            maxTargetCount ?? admitted.MaxTargetCount,
            admitted.MaxSideEffectClass,
            admitted.AllowsRecurrence,
            allowsExternalPublication ?? admitted.AllowsExternalPublication,
            admitted.AllowsIrreversibleAction);
    }

    internal static string Hash(char value) => new(value, 64);

    private static GovernedLoopAdmissionReceipt CreateReceipt(
        GovernedLoopAdmissionIntent intent,
        GovernedLoopExecutionBinding binding,
        CapabilityAdmissionSnapshot capabilityAdmission,
        AuthorityCeiling effectiveAuthority)
    {
        Assert.True(AuthorityProfileId.TryParse("profile-publication", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        Assert.True(AuthorityProfileHash.TryParse("sha256:" + Hash('8'), out var profileHash, out _));
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionEvidence.CurrentSchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            binding,
            new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, profileRevision!), profileHash!),
            new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            Hash('4'),
            effectiveAuthority,
            capabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilityAdmission),
            Now,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
            intent,
            evidence,
            Now,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);
        return receipt;
    }

    private static GovernedLoopGraphRevisionArtifact CreateArtifact(string graphId, string revisionId)
    {
        var owningRole = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("conversation-publisher", 1), Hash('b'));
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition(
                "trigger",
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                InferenceNodeId,
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]),
                new Dictionary<string, string> { ["instruction"] = "Produce one bounded result." }),
            new GovernedLoopNodeDefinition(
                NodeId,
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>()),
        };
        var graph = GovernedLoopGraphDefinition.Create(
            1,
            graphId,
            revisionId,
            "Publish one exact governed-loop result.",
            owningRole,
            "trigger",
            [NodeId],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", InferenceNodeId, GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", InferenceNodeId, NodeId, GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", InferenceNodeId, "request"),
                new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", InferenceNodeId, "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, InferenceNodeId, "result", NodeId, "result"),
            ],
            new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", NodeId, "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Conversation publication loop",
                "Test-only exact publication-authority fixture.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()));
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, $"create-{revisionId}", "user-owner", Now.AddHours(-2));
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    private static CapabilityDependencyManifest CreateManifest()
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/publication-authority-test", out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _));
        return new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            [
                new CapabilityDependency(Capability(ConversationTurnCapabilityId), range!),
                new CapabilityDependency(Capability(ModelInferenceCapabilityId), range!),
            ],
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
    }

    private static GovernedLoopPortDefinition Port(string id, GovernedLoopPortDirection direction, GovernedLoopBindingKind kind)
        => new(id, direction, kind, "text", true);

    private static CapabilityId Capability(string value)
    {
        Assert.True(CapabilityId.TryParse(value, out var result, out _));
        return result!;
    }

    private static CapabilityDataClass DataClass(string value)
    {
        Assert.True(CapabilityDataClass.TryParse(value, out var result, out _));
        return result!;
    }

    private const string WorkspaceId = "workspace-sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    internal sealed record Fixture(
        GovernedLoopAdmissionReceipt Receipt,
        GovernedLoopExecutionBinding Binding,
        GovernedLoopGraphRevisionArtifact Artifact);
}
