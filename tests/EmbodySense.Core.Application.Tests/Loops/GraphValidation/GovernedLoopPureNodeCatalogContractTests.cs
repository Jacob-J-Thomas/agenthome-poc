using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopPureNodeCatalogContractTests
{
    [Fact]
    public void CatalogDeclaresExactlyNineExecutableAuthorityFreeDescriptorsInCanonicalOrder()
    {
        var descriptors = GovernedLoopPureNodeCatalogContract.Descriptors;

        Assert.Equal(9, descriptors.Count);
        Assert.Equal(
            GovernedLoopPureNodeVocabulary.DescriptorTypeIds.Order(StringComparer.Ordinal),
            descriptors.Select(value => value.Descriptor.TypeId).Order(StringComparer.Ordinal));
        Assert.Equal(
            descriptors.OrderBy(DescriptorKey, StringComparer.Ordinal).Select(DescriptorKey),
            descriptors.Select(DescriptorKey));
        Assert.Equal(9, descriptors.Select(value => value.Descriptor).Distinct().Count());
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal(GovernedLoopPureNodeVocabulary.DescriptorVersion, descriptor.Descriptor.Version);
            Assert.True(descriptor.IsAdvertised);
            Assert.True(descriptor.IsExecutable);
            Assert.False(descriptor.IsLegalEntry);
            Assert.False(descriptor.IsLegalTerminal);
            Assert.Equal([GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure], descriptor.AllowedControlOutcomes);
            Assert.Equal([GovernedLoopControlCondition.Success], descriptor.RequiredControlOutcomes);
            Assert.Equal(GovernedLoopJoinPolicy.None, descriptor.JoinPolicy);
            Assert.Equal(1, descriptor.MinimumIncomingControlEdges);
            Assert.False(descriptor.AllowsCycle);
            Assert.Null(descriptor.CycleIterationBudgetParameterId);
            Assert.Null(descriptor.CycleTimeBudgetMillisecondsParameterId);
            Assert.Empty(descriptor.RequiredCapabilityIds);
            Assert.Equal(
                new GovernedLoopNodeResourceBudget(
                    1,
                    CustomLoopLimits.MaxGraphNodePayloadCharacters,
                    CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
                    0),
                descriptor.ResourceBudget);
            Assert.All(descriptor.Ports, port =>
            {
                Assert.Equal(GovernedLoopBindingKind.Data, port.BindingKind);
                Assert.True(port.Required);
                Assert.NotEmpty(port.AllowedValueKinds.Kinds);
            });
            Assert.All(descriptor.Parameters, parameter => Assert.True(parameter.Required));
        });
        Assert.All(descriptors.Where(value => GovernedLoopPureNodeVocabulary.IsTransform(value.Descriptor.TypeId)), value => Assert.Equal(GovernedLoopNodeKind.Transform, value.Descriptor.Kind));
        Assert.All(descriptors.Where(value => GovernedLoopPureNodeVocabulary.IsValidate(value.Descriptor.TypeId)), value => Assert.Equal(GovernedLoopNodeKind.Validate, value.Descriptor.Kind));
    }

    [Fact]
    public void CatalogPinsExactPortAndParameterSemanticsForEveryDescriptor()
    {
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.IdentityTransform,
            [(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, PureKinds()), (GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, PureKinds())],
            []);
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.StructuredSelect,
            [(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, [GovernedLoopValueKind.Object, GovernedLoopValueKind.Array]), (GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, PureKinds())],
            [(GovernedLoopPureNodeVocabulary.PointerParameter, GovernedLoopParameterValueKind.JsonPointer)]);
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.OrderedTextConcat,
            [(GovernedLoopPureNodeVocabulary.ValuesPort, GovernedLoopPortDirection.Input, [GovernedLoopValueKind.Array]), (GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, [GovernedLoopValueKind.Text])],
            [(GovernedLoopPureNodeVocabulary.SeparatorParameter, GovernedLoopParameterValueKind.Text)]);
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.SchemaConformance,
            [(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, PureKinds()), (GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, [GovernedLoopValueKind.Boolean])],
            []);
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.CanonicalEquality,
            [(GovernedLoopPureNodeVocabulary.LeftPort, GovernedLoopPortDirection.Input, PureKinds()), (GovernedLoopPureNodeVocabulary.RightPort, GovernedLoopPortDirection.Input, PureKinds()), (GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, [GovernedLoopValueKind.Boolean])],
            []);
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.InclusiveIntegerRange,
            [(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, [GovernedLoopValueKind.Integer]), (GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, [GovernedLoopValueKind.Boolean])],
            [(GovernedLoopPureNodeVocabulary.MinimumParameter, GovernedLoopParameterValueKind.Integer), (GovernedLoopPureNodeVocabulary.MaximumParameter, GovernedLoopParameterValueKind.Integer)]);
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.InclusiveNumberRange,
            [(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, [GovernedLoopValueKind.Number]), (GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, [GovernedLoopValueKind.Boolean])],
            [(GovernedLoopPureNodeVocabulary.MinimumParameter, GovernedLoopParameterValueKind.Number), (GovernedLoopPureNodeVocabulary.MaximumParameter, GovernedLoopParameterValueKind.Number)]);
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.TextLength,
            [(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, [GovernedLoopValueKind.Text]), (GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, [GovernedLoopValueKind.Boolean])],
            [(GovernedLoopPureNodeVocabulary.MinimumParameter, GovernedLoopParameterValueKind.Integer), (GovernedLoopPureNodeVocabulary.MaximumParameter, GovernedLoopParameterValueKind.Integer)]);
        AssertDescriptor(
            GovernedLoopPureNodeVocabulary.ArrayLength,
            [(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, [GovernedLoopValueKind.Array]), (GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, [GovernedLoopValueKind.Boolean])],
            [(GovernedLoopPureNodeVocabulary.MinimumParameter, GovernedLoopParameterValueKind.Integer), (GovernedLoopPureNodeVocabulary.MaximumParameter, GovernedLoopParameterValueKind.Integer)]);
    }

    [Fact]
    public void ResolutionRequiresTheExactKindTypeAndVersionWithoutFallback()
    {
        var exact = new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, GovernedLoopPureNodeVocabulary.IdentityTransform, 1);

        Assert.True(GovernedLoopPureNodeCatalogContract.TryResolve(exact, out var resolved));
        Assert.Equal(exact, resolved!.Descriptor);
        Assert.False(GovernedLoopPureNodeCatalogContract.TryResolve(exact with { Kind = GovernedLoopNodeKind.Validate }, out _));
        Assert.False(GovernedLoopPureNodeCatalogContract.TryResolve(exact with { TypeId = "identity" }, out _));
        Assert.False(GovernedLoopPureNodeCatalogContract.TryResolve(exact with { Version = 2 }, out _));
        Assert.False(GovernedLoopPureNodeCatalogContract.TryResolve(null, out _));
    }

    private static void AssertDescriptor(
        string typeId,
        IReadOnlyList<(string Id, GovernedLoopPortDirection Direction, GovernedLoopValueKind[] Kinds)> ports,
        IReadOnlyList<(string Id, GovernedLoopParameterValueKind Kind)> parameters)
    {
        var descriptor = Assert.Single(GovernedLoopPureNodeCatalogContract.Descriptors, value => string.Equals(value.Descriptor.TypeId, typeId, StringComparison.Ordinal));
        Assert.Equal(ports.Count, descriptor.Ports.Count);
        foreach (var expected in ports)
        {
            var actual = Assert.Single(descriptor.Ports, value => string.Equals(value.Id, expected.Id, StringComparison.Ordinal));
            Assert.Equal(expected.Direction, actual.Direction);
            Assert.Equal(expected.Kinds.Order(), actual.AllowedValueKinds.Kinds);
        }

        Assert.Equal(
            parameters.OrderBy(value => value.Id, StringComparer.Ordinal),
            descriptor.Parameters.Select(value => (value.Id, Kind: value.ValueKind)).OrderBy(value => value.Id, StringComparer.Ordinal));
    }

    private static GovernedLoopValueKind[] PureKinds()
        => GovernedLoopPureNodeVocabulary.PureValueKinds().Kinds.ToArray();

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor value)
        => $"{(int)value.Descriptor.Kind:D3}:{value.Descriptor.TypeId}:{value.Descriptor.Version:D10}";
}
