using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopHumanInputNodeCatalogContractTests
{
    [Fact]
    public void Catalog_is_closed_advertised_but_not_runner_executable_data_only_and_authority_free()
    {
        var descriptor = GovernedLoopHumanInputNodeCatalogContract.Descriptor;
        var port = Assert.Single(descriptor.Ports);

        Assert.Equal(new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, 1), descriptor.Descriptor);
        Assert.True(descriptor.IsAdvertised);
        Assert.False(descriptor.IsExecutable);
        Assert.False(descriptor.IsLegalEntry);
        Assert.False(descriptor.IsLegalTerminal);
        Assert.Equal([GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure], descriptor.AllowedControlOutcomes);
        Assert.Equal([GovernedLoopControlCondition.Success], descriptor.RequiredControlOutcomes);
        Assert.Equal(GovernedLoopJoinPolicy.None, descriptor.JoinPolicy);
        Assert.Equal(1, descriptor.MinimumIncomingControlEdges);
        Assert.False(descriptor.AllowsCycle);
        Assert.Null(descriptor.CycleIterationBudgetParameterId);
        Assert.Null(descriptor.CycleTimeBudgetMillisecondsParameterId);
        Assert.Equal(GovernedLoopHumanInputVocabulary.ResponsePortId, port.Id);
        Assert.Equal(GovernedLoopPortDirection.Output, port.Direction);
        Assert.Equal(GovernedLoopBindingKind.Data, port.BindingKind);
        Assert.Equal(GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Text, GovernedLoopValueKind.Boolean, GovernedLoopValueKind.Object]), port.AllowedValueKinds);
        Assert.True(port.Required);
        Assert.Empty(descriptor.Parameters);
        Assert.Empty(descriptor.RequiredCapabilityIds);
        Assert.Equal(new GovernedLoopNodeResourceBudget(1, 0, CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation, 0), descriptor.ResourceBudget);
    }

    [Fact]
    public void Resolution_and_catalog_semantics_require_one_exact_schema_one_descriptor()
    {
        var descriptor = GovernedLoopHumanInputNodeCatalogContract.Descriptor;
        var mutations = new GovernedLoopNodeCatalogDescriptor[]
        {
            descriptor with { IsExecutable = true },
            descriptor with { RequiredCapabilityIds = ["org.embodysense/workspace-read"] },
            descriptor with { Ports = [] },
            descriptor with { RequiredControlOutcomes = [GovernedLoopControlCondition.Failure] },
        };

        Assert.True(GovernedLoopHumanInputNodeCatalogContract.TryResolve(descriptor.Descriptor, out var resolved));
        Assert.Equal(descriptor, resolved);
        Assert.False(GovernedLoopSequentialNodeDescriptors.IsSupported(descriptor.Descriptor));
        Assert.Equal(descriptor.IsExecutable, GovernedLoopSequentialNodeDescriptors.IsSupported(descriptor.Descriptor));
        Assert.False(GovernedLoopHumanInputNodeCatalogContract.TryResolve(descriptor.Descriptor with { Kind = GovernedLoopNodeKind.HumanReview }, out _));
        Assert.False(GovernedLoopHumanInputNodeCatalogContract.TryResolve(descriptor.Descriptor with { TypeId = "human-review" }, out _));
        Assert.False(GovernedLoopHumanInputNodeCatalogContract.TryResolve(descriptor.Descriptor with { Version = 2 }, out _));
        Assert.All(mutations, mutation => Assert.False(GovernedLoopHumanInputNodeCatalogContract.HasExactCatalogSemantics(mutation)));
    }

    [Fact]
    public void Instance_schema_requires_the_exact_untrusted_response_projection()
    {
        var configuration = Configuration();
        var schemas = new Dictionary<string, GovernedLoopValueSchemaDefinition>(StringComparer.Ordinal)
        {
            ["text"] = new("text", GovernedLoopValueKind.Text, false),
        };
        var canonical = Node(configuration);

        Assert.True(GovernedLoopHumanInputNodeCatalogContract.HasExactSchemaSemantics(canonical, schemas));
        Assert.False(GovernedLoopHumanInputNodeCatalogContract.HasExactSchemaSemantics(canonical with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create(["org.embodysense/workspace-read"]) }, schemas));
        Assert.False(GovernedLoopHumanInputNodeCatalogContract.HasExactSchemaSemantics(canonical with { Descriptor = canonical.Descriptor with { Kind = GovernedLoopNodeKind.HumanReview } }, schemas));
        Assert.False(GovernedLoopHumanInputNodeCatalogContract.HasExactSchemaSemantics(canonical with { Ports = [new GovernedLoopPortDefinition("response", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", false)] }, schemas));
        Assert.False(GovernedLoopHumanInputNodeCatalogContract.HasExactSchemaSemantics(Node(Configuration(prompt: "Paste bearer token here.")), schemas));
    }

    private static GovernedLoopNodeDefinition Node(GovernedLoopHumanInputNodeConfiguration configuration)
        => new(
            "human-input",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, 1),
            [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(),
            null,
            null,
            null,
            configuration);

    private static GovernedLoopHumanInputNodeConfiguration Configuration(string prompt = "Provide a bounded response.")
        => new(
            1,
            "text",
            "Collect untrusted data.",
            prompt,
            new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")],
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            "timeout-policy-one",
            "failure-policy-one");
}
