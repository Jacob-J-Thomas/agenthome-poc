using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
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
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Startup.Capabilities;
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
        ToolRequest ToolRequest) Create(bool actionNode = false)
    {
        var result = CreateDetailed(actionNode);
        return (result.Receipt, result.Binding, result.Artifact, result.ToolRequest);
    }

    internal static (
        GovernedLoopAdmissionReceipt Receipt,
        GovernedLoopExecutionBinding Binding,
        GovernedLoopGraphRevisionArtifact Artifact,
        ToolRequest ToolRequest,
        GovernedLoopSequentialRunAnchor Anchor,
        GovernedLoopSequentialPlan Plan,
        string ActionInputJson) CreateAction()
        => CreateDetailed(actionNode: true);

    private static (
        GovernedLoopAdmissionReceipt Receipt,
        GovernedLoopExecutionBinding Binding,
        GovernedLoopGraphRevisionArtifact Artifact,
        ToolRequest ToolRequest,
        GovernedLoopSequentialRunAnchor Anchor,
        GovernedLoopSequentialPlan Plan,
        string ActionInputJson) CreateDetailed(bool actionNode)
    {
        var artifact = CreateArtifact(actionNode);
        var binding = GovernedLoopExecutionBinding.Create(1, "run-workspace-tool-1", artifact.RevisionArtifact.Revision, 1);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, binding.Revision, "publish-workspace-tool", Hash('7'));
        Assert.True(AuthorityGrantId.TryParse("grant-workspace-tool", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var grantRevision, out _));
        Assert.True(AuthorityProfileId.TryParse("profile-workspace-tool", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        Assert.True(AuthorityProfileHash.TryParse("sha256:" + Hash('8'), out var profileHash, out _));
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actorId, out _));
        var context = CustomLoopContextSnapshot.CreateEmpty(Now);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            "Execute one exact workspace Action.",
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            context.CapturedAtUtc,
            context.SourceManifest,
            string.Empty));
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            "admit-workspace-tool",
            invocation.ContentHash,
            string.Empty,
            publication,
            new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + Hash('a')),
            actorId!,
            "web"));
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            WorkspaceId,
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            publication,
            admissionRequest.AuthorityGrant,
            artifact.Graph.OwningRole,
            admissionRequest.ActorId,
            admissionRequest.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var capabilityAdmission = TestCapabilityAdmissionFactory.Create(CreateManifest(), Now) with { WorkspaceScopeId = WorkspaceId };
        if (actionNode)
        {
            capabilityAdmission = WithExactBuiltInWorkspacePin(capabilityAdmission);
        }
        var effectiveAuthority = new AuthorityCeiling(
            capabilityAdmission.Pins.Select(item => item.DescriptorIdentity).ToArray(),
            [DataClass("workspace-content"), DataClass("workspace-metadata")],
            3,
            CapabilitySideEffectClass.LocalReversible,
            true,
            true,
            true);
        var grantProfile = new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, profileRevision!), profileHash!);
        var grantBoundary = new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var evidence = actionNode
            ? EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.RoutingEvidenceForInference(
                intent,
                binding,
                grantProfile,
                grantBoundary,
                Hash('4'),
                effectiveAuthority,
                capabilityAdmission,
                Now,
                nodeId: "infer-00")
            : EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.EmptyRoutingEvidence(
                intent,
                binding,
                grantProfile,
                grantBoundary,
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
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            WorkspaceId,
            binding,
            admissionRequest.OperationId,
            receipt,
            receipt.ContentHash,
            admissionRequest.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [],
            string.Empty));
        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(adapterBinding, admissionRequest, receipt, invocation, artifact);
        Assert.True(anchorResult.Anchor is not null, anchorResult.Status.ToString());
        var anchor = Assert.IsType<GovernedLoopSequentialRunAnchor>(anchorResult.Anchor);
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(planResult.Plan);
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
        return (receipt, binding, artifact, request, anchor, plan, ActionInputJson);
    }

    private static GovernedLoopGraphRevisionArtifact CreateArtifact(bool actionNode)
    {
        var owningRole = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("workspace-helper", 1), Hash('b'));
        var operationNode = actionNode
            ? new GovernedLoopNodeDefinition(
                NodeId,
                WorkspaceActionNodeDescriptors.Write,
                [Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([WorkspaceCommandCapabilityId]),
                new Dictionary<string, string> { ["input"] = ActionInputJson })
            : new GovernedLoopNodeDefinition(
                NodeId,
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create(["org.embodysense/model-inference", "org.embodysense/model-profile/codex", WorkspaceCommandCapabilityId]),
                new Dictionary<string, string> { ["instruction"] = "Read only the exact bounded workspace target." });
        var trigger = new GovernedLoopNodeDefinition(
            "trigger",
            GovernedLoopSequentialNodeDescriptors.ManualTrigger,
            [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        var inference = new GovernedLoopNodeDefinition(
            "infer-00",
            GovernedLoopSequentialNodeDescriptors.ProviderInference,
            [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create(["org.embodysense/model-inference", "org.embodysense/model-profile/codex"]),
            new Dictionary<string, string> { ["instruction"] = "Produce one bounded result before the exact Action." });
        var exit = new GovernedLoopNodeDefinition(
            "exit",
            GovernedLoopSequentialNodeDescriptors.SuccessExit,
            [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create(["org.embodysense/conversation-turn"]),
            new Dictionary<string, string>());
        GovernedLoopNodeDefinition[] nodes = actionNode ? [trigger, inference, operationNode, exit] : [trigger, operationNode, exit];
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
            actionNode
                ? [
                    new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer-00", GovernedLoopControlCondition.Always),
                    new GovernedLoopControlEdgeDefinition("infer-to-action", "infer-00", NodeId, GovernedLoopControlCondition.Success),
                    new GovernedLoopControlEdgeDefinition("action-to-exit", NodeId, "exit", GovernedLoopControlCondition.Success),
                ]
                : [
                    new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", NodeId, GovernedLoopControlCondition.Always),
                    new GovernedLoopControlEdgeDefinition("infer-to-exit", NodeId, "exit", GovernedLoopControlCondition.Success),
                ],
            actionNode
                ? [
                    new GovernedLoopBindingDefinition("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer-00", "request"),
                    new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-00", "invocation-context"),
                    new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, NodeId, "result", "exit", "result"),
                ]
                : [
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

    private static CapabilityAdmissionSnapshot WithExactBuiltInWorkspacePin(CapabilityAdmissionSnapshot snapshot)
    {
        var descriptor = BuiltInCapabilityCatalog.Descriptors.Single(candidate =>
            string.Equals(candidate.Id.Value, WorkspaceCommandCapabilityId, StringComparison.Ordinal));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _));
        var replacement = new CapabilityAdmissionPin(
            identity!,
            descriptor.Kind,
            descriptor.Implementation,
            descriptor.Provenance,
            new CapabilityDependencyArtifactMetadata(null, null),
            descriptor.Purpose);
        return snapshot with
        {
            Pins = snapshot.Pins
                .Select(pin => string.Equals(pin.DescriptorIdentity.Id.Value, WorkspaceCommandCapabilityId, StringComparison.Ordinal) ? replacement : pin)
                .ToArray(),
            Evidence = snapshot.Evidence
                .Select(evidence => string.Equals(evidence.DependencyId.Value, WorkspaceCommandCapabilityId, StringComparison.Ordinal)
                    ? evidence with { SelectedIdentity = identity }
                    : evidence)
                .ToArray(),
        };
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

    private const string ActionInputJson = "{\"precondition\":{\"kind\":\"expectedAbsent\"},\"schemaVersion\":1,\"scopeId\":\"workspace\",\"segments\":[{\"kind\":\"literalUtf8\",\"literal\":\"replacement\"}],\"target\":\"shared/note.txt\"}";

    private const string WorkspaceId = "workspace-sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
}
