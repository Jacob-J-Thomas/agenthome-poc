using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopFailNodeCatalogContractTests
{
    [Fact]
    public void Catalog_declares_one_authority_free_terminal_without_outgoing_outcomes()
    {
        var descriptor = Assert.Single(GovernedLoopFailNodeCatalogContract.Descriptors);

        Assert.Equal(GovernedLoopSequentialNodeDescriptors.FailTerminal, descriptor.Descriptor);
        Assert.True(descriptor.IsAdvertised);
        Assert.True(descriptor.IsExecutable);
        Assert.False(descriptor.IsLegalEntry);
        Assert.True(descriptor.IsLegalTerminal);
        Assert.Empty(descriptor.AllowedControlOutcomes);
        Assert.Empty(descriptor.RequiredControlOutcomes);
        Assert.Empty(descriptor.Ports);
        Assert.Empty(descriptor.RequiredCapabilityIds);
        Assert.Equal(2, descriptor.Parameters.Count);
        Assert.All(descriptor.Parameters, parameter => Assert.False(parameter.Required));
    }

    [Fact]
    public void Classified_and_explicit_fail_shapes_are_exact_and_caller_classification_is_rejected()
    {
        var classified = Node(new Dictionary<string, string>());
        var explicitFailure = Node(new Dictionary<string, string>
        {
            [GovernedLoopFailNodeVocabulary.CodeParameter] = "agent-selected",
            [GovernedLoopFailNodeVocabulary.ExplanationParameter] = "bounded explanation",
        });
        var failureEdge = new GovernedLoopControlEdgeDefinition("failure-edge", "source", "fail", GovernedLoopControlCondition.Failure);

        Assert.True(GovernedLoopFailNodeCatalogContract.HasExactNodeSemantics(classified, [failureEdge]));
        Assert.True(GovernedLoopFailNodeCatalogContract.HasExactNodeSemantics(explicitFailure, [failureEdge with { Condition = GovernedLoopControlCondition.Success }]));
        Assert.False(GovernedLoopFailNodeCatalogContract.HasExactNodeSemantics(classified, [failureEdge with { Condition = GovernedLoopControlCondition.Success }]));
        Assert.False(GovernedLoopFailNodeCatalogContract.HasExactNodeSemantics(explicitFailure with { Parameters = new Dictionary<string, string> { ["failure-class"] = "retryable-no-effect" } }, [failureEdge]));
        Assert.False(GovernedLoopFailNodeCatalogContract.HasExactNodeSemantics(
            explicitFailure with
            {
                Parameters = new Dictionary<string, string>
                {
                    [GovernedLoopFailNodeVocabulary.CodeParameter] = "agent-selected",
                    [GovernedLoopFailNodeVocabulary.ExplanationParameter] = "token=private",
                },
            },
            [failureEdge]));
    }

    [Fact]
    public void Resolution_requires_exact_kind_type_and_version()
    {
        var exact = GovernedLoopSequentialNodeDescriptors.FailTerminal;

        Assert.True(GovernedLoopFailNodeCatalogContract.TryResolve(exact, out var resolved));
        Assert.Equal(exact, resolved!.Descriptor);
        Assert.False(GovernedLoopFailNodeCatalogContract.TryResolve(exact with { Kind = GovernedLoopNodeKind.Exit }, out _));
        Assert.False(GovernedLoopFailNodeCatalogContract.TryResolve(exact with { TypeId = "fail" }, out _));
        Assert.False(GovernedLoopFailNodeCatalogContract.TryResolve(exact with { Version = 2 }, out _));
    }

    private static GovernedLoopNodeDefinition Node(IReadOnlyDictionary<string, string> parameters)
        => new("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal, [], GovernedLoopAuthorityCeiling.Create([]), parameters);
}
