using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.HumanInputContinuationHost;

/// <summary>Builds the exact bounded Human Input continuation graph in both the test process and worker process.</summary>
internal static class HumanInputResponseContinuationGraphFixture
{
    private static readonly DateTimeOffset _createdAtUtc = new(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);

    internal static GovernedLoopGraphRevisionArtifact CreateArtifact(GovernedLoopHumanInputNodeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var nodes = new GovernedLoopNodeDefinition[]
        {
            Trigger(),
            new GovernedLoopNodeDefinition(
                "human-input",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                [Port(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "confirmation")],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>(),
                null,
                null,
                null,
                configuration),
            new GovernedLoopNodeDefinition(
                "confirmation-gate",
                GovernedLoopSequentialNodeDescriptors.BooleanCondition,
                [
                    Port(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "confirmation"),
                ],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            SafeResult(),
            Exit(),
            ResultJoin(),
            FailureTerminal(),
        };
        var graph = GovernedLoopGraphDefinition.Create(
            1,
            "sequential-loop",
            "revision-1",
            "Route one bounded Human Input confirmation through an ordered deterministic condition.",
            new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("sequential-role", 1), Hash('a')),
            "trigger",
            ["exit", "fail"],
            GovernedLoopAuthorityCeiling.Create(["org.embodysense/conversation-turn"]),
            [
                new GovernedLoopValueSchemaDefinition("confirmation", GovernedLoopValueKind.Boolean, false),
                new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false),
            ],
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-confirmation", "human-input", "confirmation-gate", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-input-to-fail", "human-input", "fail", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("confirmation-true", "confirmation-gate", "result-join", GovernedLoopControlCondition.True),
                new GovernedLoopControlEdgeDefinition("confirmation-false", "confirmation-gate", "result-join", GovernedLoopControlCondition.False),
                new GovernedLoopControlEdgeDefinition("result-join-to-safe-result", "result-join", "safe-result", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("safe-result-to-exit", "safe-result", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("response-to-confirmation", GovernedLoopBindingKind.Data, "human-input", GovernedLoopHumanInputVocabulary.ResponsePortId, "confirmation-gate", GovernedLoopTopologyNodeVocabulary.ValuePort),
                new GovernedLoopBindingDefinition("trigger-to-safe-result", GovernedLoopBindingKind.Data, "trigger", "request", "safe-result", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("safe-result-to-exit", GovernedLoopBindingKind.Data, "safe-result", GovernedLoopPureNodeVocabulary.OutputPort, "exit", "result"),
            ],
            new GovernedLoopOutputContract("Return the exact bounded response after ordered downstream advancement.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata("Human Input continuation", "Display metadata is not execution order.", nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            CreateRoutingPolicy());
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "create-sequential", "user-owner", _createdAtUtc);
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    private static GovernedLoopNodeDefinition Trigger()
        => new(
            "trigger",
            GovernedLoopSequentialNodeDescriptors.ManualTrigger,
            [
                Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

    private static GovernedLoopNodeDefinition Exit()
        => new(
            "exit",
            GovernedLoopSequentialNodeDescriptors.SuccessExit,
            [
                Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create(["org.embodysense/conversation-turn"]),
            new Dictionary<string, string>());

    private static GovernedLoopNodeDefinition SafeResult()
        => new(
            "safe-result",
            GovernedLoopSequentialNodeDescriptors.IdentityTransform,
            [
                Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

    private static GovernedLoopNodeDefinition FailureTerminal()
        => new(
            "fail",
            GovernedLoopSequentialNodeDescriptors.FailTerminal,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

    private static GovernedLoopNodeDefinition ResultJoin()
        => new(
            "result-join",
            GovernedLoopSequentialNodeDescriptors.SelectedJoin,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

    private static GovernedLoopPortDefinition Port(string id, GovernedLoopPortDirection direction, GovernedLoopBindingKind kind, string schemaId = "text")
        => new(id, direction, kind, schemaId, true);

    private static GovernedModelRoutingPolicy CreateRoutingPolicy()
    {
        if (!CapabilityId.TryParse("org.embodysense/model-profile/codex", out var profileId, out _)
            || !CapabilityDataClass.TryParse("public", out var publicData, out _))
        {
            throw new InvalidOperationException("The deterministic Human Input continuation fixture could not construct its exact model routing policy.");
        }

        var privacy = GovernedModelPrivacyRequirement.Create(1, true, CapabilityEgressMode.None, [], [publicData!], ["local"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited);
        var unbounded = GovernedModelUsageCeiling.Create(GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelMonetaryLimit.Unbounded);
        return GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(profileId!), [], GovernedModelProfileRequirements.Create(1, [GovernedModelModality.Text], [], 1, 1, privacy, GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded)));
    }

    private static string Hash(char value) => new(value, 64);
}
