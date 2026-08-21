using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopTopologyNodeCatalogContractTests
{
    [Fact]
    public void Catalog_is_closed_executable_authority_free_and_canonically_ordered()
    {
        var descriptors = GovernedLoopTopologyNodeCatalogContract.Descriptors;

        Assert.Equal(6, descriptors.Count);
        Assert.Equal(GovernedLoopTopologyNodeVocabulary.DescriptorTypeIds, descriptors.Select(descriptor => descriptor.Descriptor.TypeId).Order(StringComparer.Ordinal));
        Assert.Equal(descriptors.OrderBy(DescriptorKey, StringComparer.Ordinal), descriptors);
        Assert.All(descriptors, descriptor =>
        {
            Assert.True(descriptor.IsAdvertised);
            Assert.True(descriptor.IsExecutable);
            Assert.False(descriptor.IsLegalEntry);
            Assert.False(descriptor.IsLegalTerminal);
            Assert.Empty(descriptor.RequiredCapabilityIds);
            Assert.Equal(1, descriptor.Descriptor.Version);
        });
    }

    [Fact]
    public void Conditions_pin_exact_branches_ports_and_optional_cycle_budget_contracts()
    {
        var conditions = GovernedLoopTopologyNodeCatalogContract.Descriptors.Where(descriptor => descriptor.Descriptor.Kind == GovernedLoopNodeKind.Condition).ToArray();

        Assert.Equal(3, conditions.Length);
        Assert.All(conditions, descriptor =>
        {
            Assert.Equal([GovernedLoopControlCondition.True, GovernedLoopControlCondition.False], descriptor.AllowedControlOutcomes);
            Assert.Equal([GovernedLoopControlCondition.True, GovernedLoopControlCondition.False], descriptor.RequiredControlOutcomes);
            Assert.Equal(GovernedLoopJoinPolicy.None, descriptor.JoinPolicy);
            Assert.True(descriptor.AllowsCycle);
            var input = Assert.Single(descriptor.Ports);
            Assert.Equal(GovernedLoopPortDirection.Input, input.Direction);
            Assert.Equal(GovernedLoopBindingKind.Data, input.BindingKind);
            Assert.True(input.Required);
            var iterations = Assert.Single(descriptor.Parameters, parameter => parameter.Id == GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter);
            var duration = Assert.Single(descriptor.Parameters, parameter => parameter.Id == GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter);
            Assert.False(iterations.Required);
            Assert.False(duration.Required);
            Assert.Equal(GovernedLoopParameterValueKind.Integer, iterations.ValueKind);
            Assert.Equal(GovernedLoopParameterValueKind.Integer, duration.ValueKind);
            Assert.Equal(
                new GovernedLoopNodeResourceBudget(
                    1,
                    CustomLoopLimits.MaxGraphNodePayloadCharacters,
                    CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
                    0),
                descriptor.ResourceBudget);
        });
    }

    [Theory]
    [InlineData(GovernedLoopTopologyNodeVocabulary.AllJoin, GovernedLoopJoinPolicy.All)]
    [InlineData(GovernedLoopTopologyNodeVocabulary.AnyJoin, GovernedLoopJoinPolicy.Any)]
    [InlineData(GovernedLoopTopologyNodeVocabulary.SelectedJoin, GovernedLoopJoinPolicy.Selected)]
    public void Joins_pin_exact_arrival_policy(string typeId, GovernedLoopJoinPolicy expectedPolicy)
    {
        var descriptor = Assert.Single(GovernedLoopTopologyNodeCatalogContract.Descriptors, value => value.Descriptor.TypeId == typeId);

        Assert.Equal(GovernedLoopNodeKind.Join, descriptor.Descriptor.Kind);
        Assert.Equal(expectedPolicy, descriptor.JoinPolicy);
        Assert.Equal(2, descriptor.MinimumIncomingControlEdges);
        Assert.Equal([GovernedLoopControlCondition.Success], descriptor.AllowedControlOutcomes);
        Assert.Empty(descriptor.Ports);
        Assert.Empty(descriptor.Parameters);
        Assert.False(descriptor.AllowsCycle);
        Assert.Equal(
            new GovernedLoopNodeResourceBudget(
                1,
                0,
                CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
                0),
            descriptor.ResourceBudget);
    }

    [Fact]
    public void Resolution_requires_exact_kind_type_and_version()
    {
        var exact = new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, GovernedLoopTopologyNodeVocabulary.SelectedJoin, 1);

        Assert.True(GovernedLoopTopologyNodeCatalogContract.TryResolve(exact, out var resolved));
        Assert.Equal(exact, resolved!.Descriptor);
        Assert.False(GovernedLoopTopologyNodeCatalogContract.TryResolve(exact with { Kind = GovernedLoopNodeKind.Condition }, out _));
        Assert.False(GovernedLoopTopologyNodeCatalogContract.TryResolve(exact with { TypeId = "selected" }, out _));
        Assert.False(GovernedLoopTopologyNodeCatalogContract.TryResolve(exact with { Version = 2 }, out _));
        Assert.False(GovernedLoopTopologyNodeCatalogContract.TryResolve(null, out _));
    }

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor descriptor)
        => $"{(int)descriptor.Descriptor.Kind:D3}:{descriptor.Descriptor.TypeId}:{descriptor.Descriptor.Version:D10}";
}
