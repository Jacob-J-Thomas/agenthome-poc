using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopWaitNodeCatalogContractTests
{
    [Fact]
    public void Catalog_is_closed_executable_authority_free_and_canonically_ordered()
    {
        var descriptors = GovernedLoopWaitNodeCatalogContract.Descriptors;

        Assert.Equal(2, descriptors.Count);
        Assert.Equal(GovernedLoopWaitVocabulary.DescriptorTypeIds, descriptors.Select(value => value.Descriptor.TypeId));
        Assert.Equal(descriptors.OrderBy(DescriptorKey, StringComparer.Ordinal), descriptors);
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal(GovernedLoopNodeKind.Wait, descriptor.Descriptor.Kind);
            Assert.Equal(GovernedLoopWaitVocabulary.DescriptorVersion, descriptor.Descriptor.Version);
            Assert.True(descriptor.IsAdvertised);
            Assert.True(descriptor.IsExecutable);
            Assert.False(descriptor.IsLegalEntry);
            Assert.False(descriptor.IsLegalTerminal);
            Assert.Equal([GovernedLoopControlCondition.Success], descriptor.AllowedControlOutcomes);
            Assert.Equal(descriptor.AllowedControlOutcomes, descriptor.RequiredControlOutcomes);
            Assert.Equal(GovernedLoopJoinPolicy.None, descriptor.JoinPolicy);
            Assert.Equal(1, descriptor.MinimumIncomingControlEdges);
            Assert.False(descriptor.AllowsCycle);
            Assert.Null(descriptor.CycleIterationBudgetParameterId);
            Assert.Null(descriptor.CycleTimeBudgetMillisecondsParameterId);
            Assert.Empty(descriptor.Ports);
            Assert.Empty(descriptor.RequiredCapabilityIds);
            Assert.Equal(
                new GovernedLoopNodeResourceBudget(
                    1,
                    0,
                    CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
                    0),
                descriptor.ResourceBudget);
        });
    }

    [Fact]
    public void Conditions_pin_one_exact_bounded_parameter()
    {
        var timestamp = Descriptor(GovernedLoopWaitVocabulary.Timestamp);
        var deadline = Assert.Single(timestamp.Parameters);
        Assert.Equal(GovernedLoopWaitVocabulary.DeadlineUtcParameter, deadline.Id);
        Assert.Equal(GovernedLoopParameterValueKind.Text, deadline.ValueKind);
        Assert.True(deadline.Required);
        Assert.Equal(28, deadline.MinimumCharacters);
        Assert.Equal(28, deadline.MaximumCharacters);

        var authenticatedEvent = Descriptor(GovernedLoopWaitVocabulary.AuthenticatedEvent);
        var eventReference = Assert.Single(authenticatedEvent.Parameters);
        Assert.Equal(GovernedLoopWaitVocabulary.EventReferenceParameter, eventReference.Id);
        Assert.Equal(GovernedLoopParameterValueKind.Text, eventReference.ValueKind);
        Assert.True(eventReference.Required);
        Assert.Equal(1, eventReference.MinimumCharacters);
        Assert.Equal(GovernedLoopWaitContractLimits.MaxEventReferenceCharacters, eventReference.MaximumCharacters);
    }

    [Fact]
    public void Resolution_requires_exact_kind_type_and_version()
    {
        var exact = new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, GovernedLoopWaitVocabulary.Timestamp, 1);

        Assert.True(GovernedLoopWaitNodeCatalogContract.TryResolve(exact, out var resolved));
        Assert.Equal(exact, resolved!.Descriptor);
        Assert.False(GovernedLoopWaitNodeCatalogContract.TryResolve(exact with { Kind = GovernedLoopNodeKind.Action }, out _));
        Assert.False(GovernedLoopWaitNodeCatalogContract.TryResolve(exact with { TypeId = "wait" }, out _));
        Assert.False(GovernedLoopWaitNodeCatalogContract.TryResolve(exact with { Version = 2 }, out _));
        Assert.False(GovernedLoopWaitNodeCatalogContract.TryResolve(null, out _));
    }

    private static GovernedLoopNodeCatalogDescriptor Descriptor(string typeId)
        => Assert.Single(GovernedLoopWaitNodeCatalogContract.Descriptors, value => value.Descriptor.TypeId == typeId);

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor descriptor)
        => $"{(int)descriptor.Descriptor.Kind:D3}:{descriptor.Descriptor.TypeId}:{descriptor.Descriptor.Version:D10}";
}
