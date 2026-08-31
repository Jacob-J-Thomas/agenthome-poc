using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Tests.Support;

internal static class HumanReviewOrderedReleaseGraphFixture
{
    internal const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    internal static readonly DateTimeOffset CreatedAtUtc = new(2026, 8, 10, 11, 0, 0, TimeSpan.Zero);

    internal static GovernedLoopGraphDefinition Graph()
    {
        var role = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("sequential-role", 1), Hash('a'));
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition(
                "trigger",
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                "step-1",
                GovernedLoopSequentialNodeDescriptors.HumanReview,
                [],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GovernedLoopHumanReviewNodeCatalogContract.ReviewPolicyIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerPolicyId,
                    [GovernedLoopHumanReviewNodeCatalogContract.ReviewerRoleIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
                    [GovernedLoopHumanReviewNodeCatalogContract.ApprovalScopeIdParameter] = "review-scope-one",
                }),
            new GovernedLoopNodeDefinition(
                "exit",
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>()),
        };
        return GovernedLoopGraphDefinition.Create(
            1,
            "sequential-loop",
            "revision-1",
            "Persist one exact Human Review release fixture.",
            role,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-step", "trigger", "step-1", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("step-to-exit", "step-1", "exit", GovernedLoopControlCondition.Success),
            ],
            [new GovernedLoopBindingDefinition("request-to-exit", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result")],
            new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Sequential loop",
                "Display metadata is not execution order.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            DefaultModelRoutingPolicy());
    }

    internal static GovernedLoopGraphRevisionArtifact Artifact()
    {
        var graph = Graph();
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "create-sequential", "user-owner", CreatedAtUtc);
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    internal static GovernedLoopGraphDefinition PreDispatchEffectGraph()
    {
        var role = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("sequential-role", 1), Hash('a'));
        const string InputJson = "{\"precondition\":{\"kind\":\"expectedAbsent\"},\"schemaVersion\":1,\"scopeId\":\"workspace\",\"segments\":[{\"kind\":\"literalUtf8\",\"literal\":\"marker\"}],\"target\":\"process-observable-marker.txt\"}";
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition(
                "trigger",
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                "workspace-action",
                GovernedLoopSequentialNodeDescriptors.WorkspaceWrite,
                [Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([WorkspaceCommandCapabilityId]),
                new Dictionary<string, string> { ["input"] = InputJson }),
            new GovernedLoopNodeDefinition(
                "exit",
                GovernedLoopSequentialNodeDescriptors.SuccessExit,
                [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                new Dictionary<string, string>()),
        };
        return GovernedLoopGraphDefinition.Create(
            1,
            "sequential-loop",
            "revision-1",
            "Prove one reviewed process-observable effect through the canonical ordered runtime.",
            role,
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, WorkspaceCommandCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-action", "trigger", "workspace-action", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("action-to-exit", "workspace-action", "exit", GovernedLoopControlCondition.Success),
            ],
            [new GovernedLoopBindingDefinition("action-result-to-exit", GovernedLoopBindingKind.Data, "workspace-action", "result", "exit", "result")],
            new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Reviewed process effect",
                "Display metadata is not execution order.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            DefaultModelRoutingPolicy());
    }

    internal static GovernedLoopGraphRevisionArtifact PreDispatchEffectArtifact()
    {
        var graph = PreDispatchEffectGraph();
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "create-sequential", "user-owner", CreatedAtUtc);
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    internal static CapabilityDescriptor WorkspaceCapability()
    {
        _ = CapabilityId.TryParse(WorkspaceCommandCapabilityId, out var id, out _);
        _ = CapabilityProviderId.TryParse("org.embodysense", out var provider, out _);
        _ = CapabilityVersion.TryParse("1.0.0", out var version, out _);
        _ = CapabilityVersionRange.TryParse("*", out var hostRange, out _);
        _ = CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _);
        return new CapabilityDescriptor(
            CapabilityDescriptor.CurrentSchemaVersion,
            id!,
            CapabilityKind.Actuator,
            version!,
            new CapabilityImplementationIdentity(provider!, "workspace-command"),
            new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://embodysense.dev/builtins/workspace-command", "1", null),
            new CapabilityCompatibility(hostRange!, [CapabilityPlatform.Any]),
            "Expose governed workspace commands through the runtime tool broker.",
            schema!,
            schema!,
            new CapabilityResourceLimits(86_400_000, 1_099_511_627_776, 16_777_216, 1_024),
            CapabilitySideEffectClass.LocalReversible,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
    }

    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelProfileCapabilityId = "org.embodysense/model-profile/codex";

    private static GovernedLoopPortDefinition Port(string id, GovernedLoopPortDirection direction, GovernedLoopBindingKind kind)
        => new(id, direction, kind, "text", true);

    private static GovernedModelRoutingPolicy DefaultModelRoutingPolicy()
    {
        _ = CapabilityId.TryParse(ModelProfileCapabilityId, out var profileId, out _);
        _ = CapabilityDataClass.TryParse("public", out var publicData, out _);
        var privacy = GovernedModelPrivacyRequirement.Create(1, true, CapabilityEgressMode.None, [], [publicData!], ["local"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited);
        var unbounded = GovernedModelUsageCeiling.Create(GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelMonetaryLimit.Unbounded);
        var budget = GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded);
        var requirements = GovernedModelProfileRequirements.Create(1, [GovernedModelModality.Text], [], 1, 1, privacy, budget);
        return GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(profileId!), [], requirements);
    }

    private static string Hash(char value) => new(value, 64);
}
