using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal static class WorkspaceToolAuthorityTestFixture
{
    internal const string NodeId = "infer-01";
    internal const int NodeAttempt = 2;
    internal const string ServerCorrelationId = "attempt-correlation-1";
    internal const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    internal static readonly DateTimeOffset Now = new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);

    internal static (
        GovernedLoopAdmissionReceipt Receipt,
        GovernedLoopExecutionBinding Binding,
        GovernedLoopGraphRevisionArtifact Artifact,
        ToolRequest ToolRequest) Create()
    {
        var artifact = CreateArtifact();
        var binding = GovernedLoopExecutionBinding.Create(1, "run-workspace-tool-1", artifact.RevisionArtifact.Revision, 1);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, binding.Revision, "publish-workspace-tool", Hash('7'));
        Assert.True(AuthorityGrantId.TryParse("grant-workspace-tool", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var grantRevision, out _));
        Assert.True(AuthorityProfileId.TryParse("profile-workspace-tool", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        Assert.True(AuthorityProfileHash.TryParse("sha256:" + Hash('8'), out var profileHash, out _));
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actorId, out _));
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            WorkspaceId,
            "admit-workspace-tool",
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
            [DataClass("workspace-content"), DataClass("workspace-metadata")],
            3,
            CapabilitySideEffectClass.LocalReversible,
            true,
            true,
            true);
        var evidence = EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.EmptyRoutingEvidence(
            intent,
            binding,
            new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, profileRevision!), profileHash!),
            new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            Hash('4'),
            effectiveAuthority,
            capabilityAdmission,
            Now);
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
            intent,
            evidence,
            Now,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);
        var request = new ToolRequest(
            ToolCommand.Read,
            "shared/note.txt",
            CorrelationId: "tool-call-1",
            AuditCorrelation: new ToolAuditCorrelation(
                binding.RunId,
                artifact.Graph.GraphId,
                artifact.Graph.OwningRole.Identity.RoleId,
                1,
                binding.Revision.ExecutableHash,
                1,
                NodeId,
                NodeAttempt,
                ServerCorrelationId,
                "list,read,search",
                "list,read,search",
                "list,read,search",
                Hash('5'),
                Hash('6')));
        return (receipt, binding, artifact, request);
    }

    private static GovernedLoopGraphRevisionArtifact CreateArtifact()
    {
        var owningRole = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("workspace-helper", 1), Hash('b'));
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition(
                "trigger",
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                NodeId,
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create(["org.embodysense/model-inference", "org.embodysense/model-profile/codex", WorkspaceCommandCapabilityId]),
                new Dictionary<string, string> { ["instruction"] = "Read only the exact bounded workspace target." }),
            new GovernedLoopNodeDefinition(
                "exit",
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create(["org.embodysense/conversation-turn"]),
                new Dictionary<string, string>()),
        };
        var graph = GovernedLoopGraphDefinition.Create(
            1,
            "workspace-tool-loop",
            "revision-1",
            "Execute one exact read-only workspace-tool request.",
            owningRole,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create(["org.embodysense/conversation-turn", "org.embodysense/model-inference", "org.embodysense/model-profile/codex", WorkspaceCommandCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", NodeId, GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", NodeId, "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", NodeId, "request"),
                new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", NodeId, "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, NodeId, "result", "exit", "result"),
            ],
            new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Workspace tool loop",
                "Test-only exact authority fixture.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "create-workspace-tool", "user-owner", Now.AddHours(-2));
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    private static CapabilityDependencyManifest CreateManifest()
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/effect-tool-test", out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _));
        return new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            [
                new CapabilityDependency(Capability("org.embodysense/conversation-turn"), range!),
                new CapabilityDependency(Capability("org.embodysense/model-inference"), range!),
                new CapabilityDependency(Capability("org.embodysense/model-profile/codex"), range!),
                new CapabilityDependency(Capability(WorkspaceCommandCapabilityId), range!),
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

    private static string Hash(char value) => new(value, 64);

    private const string WorkspaceId = "workspace-sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
}
