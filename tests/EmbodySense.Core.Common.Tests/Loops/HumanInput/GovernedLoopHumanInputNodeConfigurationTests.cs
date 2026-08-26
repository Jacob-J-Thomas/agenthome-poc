using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.HumanInput;

public sealed class GovernedLoopHumanInputNodeConfigurationTests
{
    [Fact]
    public void Valid_data_only_configuration_is_deeply_snapshotted_and_bound_to_the_response_schema()
    {
        var choices = new[] { new HumanInputChoice("yes", "Yes"), new HumanInputChoice("no", "No") };
        var respondents = new List<HumanInputEligibleRespondent?>
        {
            new("user-one", "role-one", "route-one"),
        };
        var configuration = new GovernedLoopHumanInputNodeConfiguration(
            1,
            "text",
            "Collect untrusted data.",
            "Choose the bounded response.",
            new HumanInputResponseSchema(HumanInputResponseKind.Choice, null, choices, null, null),
            HumanInputPrivacyClass.Private,
            respondents,
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            "timeout-policy-one",
            "failure-policy-one");

        Assert.True(GovernedLoopHumanInputNodeConfigurationValidator.IsValid(configuration));
        var graph = Graph(configuration, "text", GovernedLoopValueKind.Text);
        choices[0] = new HumanInputChoice("changed", "Changed");
        respondents[0] = new HumanInputEligibleRespondent("user-two", "role-two", "route-two");
        var node = graph.Nodes.Single(value => value.Id == "human-input");

        Assert.True(GovernedLoopHumanInputNodeConfigurationValidator.HasExactNodeSemantics(node, graph.ValueSchemas.ToDictionary(value => value.Id, StringComparer.Ordinal)));
        Assert.Equal("yes", node.HumanInputConfiguration!.ResponseSchema!.Choices![0].ChoiceId);
        Assert.Equal("user-one", node.HumanInputConfiguration.EligibleRespondents![0]!.RespondentId);
        Assert.Throws<NotSupportedException>(() => ((IList<HumanInputEligibleRespondent?>)node.HumanInputConfiguration.EligibleRespondents).Add(null));
    }

    [Fact]
    public void Hostile_unknown_and_noncanonical_configuration_shapes_fail_closed()
    {
        var variants = new[]
        {
            Configuration(schemaVersion: 2),
            Configuration(requestSchemaReference: "not a reference"),
            Configuration(prompt: "Paste api_key here."),
            Configuration(purpose: "Approve this action."),
            Configuration(privacyClass: HumanInputPrivacyClass.Unknown),
            Configuration(responseKind: HumanInputResponseKind.Unknown),
            Configuration(eligibleRespondents:
            [
                new HumanInputEligibleRespondent("user-two", "role-two", "route-two"),
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
            ]),
            Configuration(eligibleRespondents:
            [
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
            ]),
        };

        Assert.All(variants, value => Assert.False(GovernedLoopHumanInputNodeConfigurationValidator.IsValid(value)));
    }

    [Fact]
    public void Graph_admission_rejects_authority_and_Human_Review_confusion()
    {
        var configuration = Configuration();
        var reviewNodes = GraphNodes(configuration, "text");
        reviewNodes[1] = reviewNodes[1] with { Descriptor = new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanReview, "human-review", 1) };
        var authorityNodes = GraphNodes(configuration, "text");
        authorityNodes[1] = authorityNodes[1] with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create([GovernedLoopGraphTestFixture.WorkspaceReadCapability]) };

        Assert.Throws<ArgumentException>(() => Graph(configuration, "text", GovernedLoopValueKind.Text, authorityNodes));
        Assert.Throws<ArgumentException>(() => Graph(configuration, "text", GovernedLoopValueKind.Text, reviewNodes));
    }

    [Fact]
    public void Every_exact_human_input_binding_changes_the_executable_hash()
    {
        var variants = new[]
        {
            Configuration(requestSchemaReference: "alternate"),
            Configuration(purpose: "Collect a different untrusted datum."),
            Configuration(prompt: "Provide a different bounded response."),
            Configuration(maxTextCharacters: 65),
            Configuration(privacyClass: HumanInputPrivacyClass.Sensitive),
            Configuration(eligibleRespondents: [new HumanInputEligibleRespondent("user-two", "role-two", "route-two")]),
            Configuration(
                eligibleRespondents:
                [
                    new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
                    new HumanInputEligibleRespondent("user-two", "role-two", "route-two"),
                ],
                responsePolicy: new HumanInputResponsePolicy(HumanInputResponsePolicyKind.Quorum, 2, null)),
            Configuration(timeoutPolicyReference: "timeout-policy-two"),
            Configuration(failurePolicyReference: "failure-policy-two"),
        };
        var baseline = Configuration();
        var baselineHash = Graph(baseline, baseline.RequestSchemaReference!, GovernedLoopValueKind.Text).ExecutableHash;

        Assert.All(variants, variant => Assert.NotEqual(baselineHash, Graph(variant, variant.RequestSchemaReference!, GovernedLoopValueKind.Text).ExecutableHash));
    }

    private static GovernedLoopHumanInputNodeConfiguration Configuration(
        int schemaVersion = GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
        string requestSchemaReference = "text",
        string purpose = "Collect untrusted data.",
        string prompt = "Provide a bounded response.",
        HumanInputResponseKind responseKind = HumanInputResponseKind.Text,
        int? maxTextCharacters = 64,
        HumanInputPrivacyClass privacyClass = HumanInputPrivacyClass.Private,
        IReadOnlyList<HumanInputEligibleRespondent?>? eligibleRespondents = null,
        HumanInputResponsePolicy? responsePolicy = null,
        string timeoutPolicyReference = "timeout-policy-one",
        string failurePolicyReference = "failure-policy-one")
        => new(
            schemaVersion,
            requestSchemaReference,
            purpose,
            prompt,
            new HumanInputResponseSchema(responseKind, maxTextCharacters, null, null, null),
            privacyClass,
            eligibleRespondents ?? [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")],
            responsePolicy ?? new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            timeoutPolicyReference,
            failurePolicyReference);

    private static GovernedLoopGraphDefinition Graph(
        GovernedLoopHumanInputNodeConfiguration configuration,
        string schemaId,
        GovernedLoopValueKind valueKind,
        GovernedLoopNodeDefinition[]? nodes = null)
        => GovernedLoopGraphDefinition.Create(
            1,
            "human-input-graph",
            "revision-one",
            "Collect data without granting authority.",
            GovernedLoopGraphTestFixture.Role(),
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([GovernedLoopGraphTestFixture.WorkspaceReadCapability]),
            [new GovernedLoopValueSchemaDefinition(schemaId, valueKind, false)],
            nodes ?? GraphNodes(configuration, schemaId),
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-exit", "human-input", "exit", GovernedLoopControlCondition.Success),
            ],
            [new GovernedLoopBindingDefinition("response-binding", GovernedLoopBindingKind.Data, "human-input", GovernedLoopHumanInputVocabulary.ResponsePortId, "exit", "result")],
            new GovernedLoopOutputContract("Return the untrusted response.", [new GovernedLoopOutputDefinition("result", schemaId, "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Human Input graph",
                "A data-only Human Input graph.",
                [
                    new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Start the graph.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata("human-input", "Human Input", "Collect data.", 100, 0),
                    new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Return data.", 200, 0),
                ]),
            GovernedLoopGraphTestFixture.DefaultModelRoutingPolicy());

    private static GovernedLoopNodeDefinition[] GraphNodes(GovernedLoopHumanInputNodeConfiguration configuration, string schemaId)
        =>
        [
            new GovernedLoopNodeDefinition(
                "trigger",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
                [GovernedLoopGraphTestFixture.OutputPort("request", GovernedLoopBindingKind.Data, schemaId)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                "human-input",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, 1),
                [GovernedLoopGraphTestFixture.OutputPort(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopBindingKind.Data, schemaId)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>(),
                null,
                null,
                null,
                configuration),
            new GovernedLoopNodeDefinition(
                "exit",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                [
                    GovernedLoopGraphTestFixture.InputPort("result", GovernedLoopBindingKind.Data, schemaId),
                    GovernedLoopGraphTestFixture.OutputPort("published-result", GovernedLoopBindingKind.Data, schemaId),
                ],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
        ];
}
