using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Defines the closed Application-owned catalog contract for the nine schema-1 dependency-free pure operators.</summary>
public static class GovernedLoopPureNodeCatalogContract
{
    private static readonly IReadOnlyList<GovernedLoopControlCondition> _success =
        Array.AsReadOnly(new[] { GovernedLoopControlCondition.Success });
    private static readonly IReadOnlyList<string> _noCapabilities = Array.Empty<string>();
    private static readonly GovernedLoopValueKindSet _pureKinds = GovernedLoopPureNodeVocabulary.PureValueKinds();
    private static readonly GovernedLoopValueKindSet _structuredKinds = Kinds(GovernedLoopValueKind.Object, GovernedLoopValueKind.Array);
    private static readonly GovernedLoopValueKindSet _arrayKind = Kinds(GovernedLoopValueKind.Array);
    private static readonly GovernedLoopValueKindSet _booleanKind = Kinds(GovernedLoopValueKind.Boolean);
    private static readonly GovernedLoopValueKindSet _integerKind = Kinds(GovernedLoopValueKind.Integer);
    private static readonly GovernedLoopValueKindSet _numberKind = Kinds(GovernedLoopValueKind.Number);
    private static readonly GovernedLoopValueKindSet _textKind = Kinds(GovernedLoopValueKind.Text);
    private static readonly IReadOnlyList<GovernedLoopNodeCatalogDescriptor> _descriptors = CreateDescriptors();

    /// <summary>Gets the nine exact descriptor declarations in canonical descriptor-key order.</summary>
    public static IReadOnlyList<GovernedLoopNodeCatalogDescriptor> Descriptors => _descriptors;

    /// <summary>Resolves one exact pure descriptor declaration without aliases or version fallback.</summary>
    public static bool TryResolve(GovernedLoopNodeDescriptor? descriptor, out GovernedLoopNodeCatalogDescriptor? contract)
    {
        contract = descriptor is null
            ? null
            : _descriptors.SingleOrDefault(candidate => Equals(candidate.Descriptor, descriptor));
        return contract is not null;
    }

    private static IReadOnlyList<GovernedLoopNodeCatalogDescriptor> CreateDescriptors()
    {
        var values = new[]
        {
            Descriptor(
                GovernedLoopNodeKind.Transform,
                GovernedLoopPureNodeVocabulary.IdentityTransform,
                [Input(GovernedLoopPureNodeVocabulary.InputPort, _pureKinds), Output(GovernedLoopPureNodeVocabulary.OutputPort, _pureKinds)],
                []),
            Descriptor(
                GovernedLoopNodeKind.Transform,
                GovernedLoopPureNodeVocabulary.StructuredSelect,
                [Input(GovernedLoopPureNodeVocabulary.InputPort, _structuredKinds), Output(GovernedLoopPureNodeVocabulary.OutputPort, _pureKinds)],
                [Parameter(GovernedLoopPureNodeVocabulary.PointerParameter, GovernedLoopParameterValueKind.JsonPointer, 0, CustomLoopLimits.MaxGraphParameterValueCharacters)]),
            Descriptor(
                GovernedLoopNodeKind.Transform,
                GovernedLoopPureNodeVocabulary.OrderedTextConcat,
                [Input(GovernedLoopPureNodeVocabulary.ValuesPort, _arrayKind), Output(GovernedLoopPureNodeVocabulary.OutputPort, _textKind)],
                [Parameter(GovernedLoopPureNodeVocabulary.SeparatorParameter, GovernedLoopParameterValueKind.Text, 0, CustomLoopLimits.MaxGraphParameterValueCharacters)]),
            Descriptor(
                GovernedLoopNodeKind.Validate,
                GovernedLoopPureNodeVocabulary.SchemaConformance,
                [Input(GovernedLoopPureNodeVocabulary.InputPort, _pureKinds), Output(GovernedLoopPureNodeVocabulary.ResultPort, _booleanKind)],
                []),
            Descriptor(
                GovernedLoopNodeKind.Validate,
                GovernedLoopPureNodeVocabulary.CanonicalEquality,
                [Input(GovernedLoopPureNodeVocabulary.LeftPort, _pureKinds), Input(GovernedLoopPureNodeVocabulary.RightPort, _pureKinds), Output(GovernedLoopPureNodeVocabulary.ResultPort, _booleanKind)],
                []),
            Descriptor(
                GovernedLoopNodeKind.Validate,
                GovernedLoopPureNodeVocabulary.InclusiveIntegerRange,
                [Input(GovernedLoopPureNodeVocabulary.InputPort, _integerKind), Output(GovernedLoopPureNodeVocabulary.ResultPort, _booleanKind)],
                [IntegerParameter(GovernedLoopPureNodeVocabulary.MinimumParameter, long.MinValue, long.MaxValue), IntegerParameter(GovernedLoopPureNodeVocabulary.MaximumParameter, long.MinValue, long.MaxValue)]),
            Descriptor(
                GovernedLoopNodeKind.Validate,
                GovernedLoopPureNodeVocabulary.InclusiveNumberRange,
                [Input(GovernedLoopPureNodeVocabulary.InputPort, _numberKind), Output(GovernedLoopPureNodeVocabulary.ResultPort, _booleanKind)],
                [Parameter(GovernedLoopPureNodeVocabulary.MinimumParameter, GovernedLoopParameterValueKind.Number, 1, CustomLoopLimits.MaxGraphTypedValueNumberCharacters), Parameter(GovernedLoopPureNodeVocabulary.MaximumParameter, GovernedLoopParameterValueKind.Number, 1, CustomLoopLimits.MaxGraphTypedValueNumberCharacters)]),
            Descriptor(
                GovernedLoopNodeKind.Validate,
                GovernedLoopPureNodeVocabulary.TextLength,
                [Input(GovernedLoopPureNodeVocabulary.InputPort, _textKind), Output(GovernedLoopPureNodeVocabulary.ResultPort, _booleanKind)],
                [IntegerParameter(GovernedLoopPureNodeVocabulary.MinimumParameter, 0, CustomLoopLimits.MaxGraphTypedValueStringCharacters), IntegerParameter(GovernedLoopPureNodeVocabulary.MaximumParameter, 0, CustomLoopLimits.MaxGraphTypedValueStringCharacters)]),
            Descriptor(
                GovernedLoopNodeKind.Validate,
                GovernedLoopPureNodeVocabulary.ArrayLength,
                [Input(GovernedLoopPureNodeVocabulary.InputPort, _arrayKind), Output(GovernedLoopPureNodeVocabulary.ResultPort, _booleanKind)],
                [IntegerParameter(GovernedLoopPureNodeVocabulary.MinimumParameter, 0, CustomLoopLimits.MaxGraphTypedValueCollectionEntries), IntegerParameter(GovernedLoopPureNodeVocabulary.MaximumParameter, 0, CustomLoopLimits.MaxGraphTypedValueCollectionEntries)]),
        };
        return Array.AsReadOnly(values.OrderBy(DescriptorKey, StringComparer.Ordinal).ToArray());
    }

    private static GovernedLoopNodeCatalogDescriptor Descriptor(
        GovernedLoopNodeKind kind,
        string typeId,
        GovernedLoopCatalogPortContract[] ports,
        GovernedLoopCatalogParameterContract[] parameters)
        => new(
            new GovernedLoopNodeDescriptor(kind, typeId, GovernedLoopPureNodeVocabulary.DescriptorVersion),
            IsAdvertised: true,
            IsExecutable: true,
            IsLegalEntry: false,
            IsLegalTerminal: false,
            _success,
            _success,
            GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges: 1,
            AllowsCycle: false,
            CycleIterationBudgetParameterId: null,
            CycleTimeBudgetMillisecondsParameterId: null,
            Array.AsReadOnly(ports),
            Array.AsReadOnly(parameters),
            _noCapabilities,
            new GovernedLoopNodeResourceBudget(
                Attempts: 1,
                PayloadCharacters: CustomLoopLimits.MaxGraphNodePayloadCharacters,
                EvidenceItems: 1,
                ResourceUnits: 0));

    private static GovernedLoopCatalogPortContract Input(string id, GovernedLoopValueKindSet kinds)
        => new(id, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, kinds, Required: true);

    private static GovernedLoopCatalogPortContract Output(string id, GovernedLoopValueKindSet kinds)
        => new(id, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, kinds, Required: true);

    private static GovernedLoopCatalogParameterContract Parameter(
        string id,
        GovernedLoopParameterValueKind kind,
        int minimumCharacters,
        int maximumCharacters)
        => new(id, kind, Required: true, minimumCharacters, maximumCharacters, null, null, Array.Empty<string>());

    private static GovernedLoopCatalogParameterContract IntegerParameter(string id, long minimum, long maximum)
        => new(id, GovernedLoopParameterValueKind.Integer, Required: true, 1, 20, minimum, maximum, Array.Empty<string>());

    private static GovernedLoopValueKindSet Kinds(params GovernedLoopValueKind[] values)
        => GovernedLoopValueKindSet.Create(values);

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor value)
        => $"{(int)value.Descriptor.Kind:D3}:{value.Descriptor.TypeId}:{value.Descriptor.Version:D10}";
}
