using System.Globalization;
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

    internal static bool HasExactSchemaSemantics(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
    {
        if (node.Ports.Any(port => !HasSupportedSchemaTree(
                port.ValueSchemaId,
                schemas,
                new HashSet<string>(StringComparer.Ordinal),
                1)))
        {
            return false;
        }

        var input = InputPort(node, GovernedLoopPureNodeVocabulary.InputPort);
        var output = OutputPort(node, GovernedLoopPureNodeVocabulary.OutputPort);
        var result = OutputPort(node, GovernedLoopPureNodeVocabulary.ResultPort);
        return node.Descriptor.TypeId switch
        {
            GovernedLoopPureNodeVocabulary.IdentityTransform => input is not null
                && output is not null
                && string.Equals(input.ValueSchemaId, output.ValueSchemaId, StringComparison.Ordinal),
            GovernedLoopPureNodeVocabulary.StructuredSelect => IsNonNullable(input, schemas),
            GovernedLoopPureNodeVocabulary.OrderedTextConcat => IsExactConcat(node, schemas, output),
            GovernedLoopPureNodeVocabulary.SchemaConformance => input is not null
                && IsNonNullable(result, schemas)
                && HasSupportedSchemaTree(
                    input.ValueSchemaId,
                    schemas,
                    new HashSet<string>(StringComparer.Ordinal),
                    1),
            GovernedLoopPureNodeVocabulary.CanonicalEquality => IsExactEquality(node, schemas, result),
            GovernedLoopPureNodeVocabulary.InclusiveIntegerRange or GovernedLoopPureNodeVocabulary.InclusiveNumberRange
                => IsNonNullable(input, schemas) && IsNonNullable(result, schemas) && HasOrderedRange(node),
            GovernedLoopPureNodeVocabulary.TextLength or GovernedLoopPureNodeVocabulary.ArrayLength
                => IsNonNullable(input, schemas) && IsNonNullable(result, schemas) && HasOrderedIntegerRange(node),
            _ => false,
        };
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
                EvidenceItems: CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation,
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

    private static bool IsExactConcat(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        GovernedLoopPortDefinition? output)
    {
        var values = InputPort(node, GovernedLoopPureNodeVocabulary.ValuesPort);
        if (values is null
            || output is null
            || !schemas.TryGetValue(values.ValueSchemaId, out var valuesSchema)
            || valuesSchema is not { Nullable: false, Format: null, ElementSchemaId: { } elementSchemaId }
            || !schemas.TryGetValue(elementSchemaId, out var element))
        {
            return false;
        }

        return element is { Kind: GovernedLoopValueKind.Text, Nullable: false, Format: null, ElementSchemaId: null }
            && IsNonNullable(output, schemas);
    }

    private static bool HasSupportedSchemaTree(
        string schemaId,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        HashSet<string> active,
        int depth)
    {
        if (depth > CustomLoopLimits.MaxGraphTypedValueDepth
            || !schemas.TryGetValue(schemaId, out var schema)
            || !active.Add(schema.Id))
        {
            return false;
        }

        var valid = schema.Format is null
            && (schema.Kind != GovernedLoopValueKind.Array
                || schema.ElementSchemaId is not null
                && HasSupportedSchemaTree(schema.ElementSchemaId, schemas, active, depth + 1));
        active.Remove(schema.Id);
        return valid;
    }

    private static bool IsExactEquality(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        GovernedLoopPortDefinition? result)
    {
        var left = InputPort(node, GovernedLoopPureNodeVocabulary.LeftPort);
        var right = InputPort(node, GovernedLoopPureNodeVocabulary.RightPort);
        return left is not null
            && right is not null
            && schemas.TryGetValue(left.ValueSchemaId, out var leftSchema)
            && schemas.TryGetValue(right.ValueSchemaId, out var rightSchema)
            && leftSchema.Kind == rightSchema.Kind
            && IsNonNullable(result, schemas);
    }

    private static bool IsNonNullable(
        GovernedLoopPortDefinition? port,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
        => port is not null
            && schemas.TryGetValue(port.ValueSchemaId, out var schema)
            && !schema.Nullable;

    private static bool HasOrderedRange(GovernedLoopNodeDefinition node)
        => node.Descriptor.TypeId == GovernedLoopPureNodeVocabulary.InclusiveIntegerRange
            ? HasOrderedIntegerRange(node)
            : TryGetParameter(node, GovernedLoopPureNodeVocabulary.MinimumParameter, out var minimumValue)
                && TryGetParameter(node, GovernedLoopPureNodeVocabulary.MaximumParameter, out var maximumValue)
                && TryCanonicalNumber(minimumValue, out var minimum)
                && TryCanonicalNumber(maximumValue, out var maximum)
                && minimum <= maximum;

    private static bool HasOrderedIntegerRange(GovernedLoopNodeDefinition node)
        => TryGetParameter(node, GovernedLoopPureNodeVocabulary.MinimumParameter, out var minimumValue)
            && TryGetParameter(node, GovernedLoopPureNodeVocabulary.MaximumParameter, out var maximumValue)
            && long.TryParse(minimumValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minimum)
            && long.TryParse(maximumValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var maximum)
            && string.Equals(minimum.ToString(CultureInfo.InvariantCulture), minimumValue, StringComparison.Ordinal)
            && string.Equals(maximum.ToString(CultureInfo.InvariantCulture), maximumValue, StringComparison.Ordinal)
            && minimum <= maximum;

    private static bool TryCanonicalNumber(string value, out double number)
    {
        number = default;
        return GovernedLoopTypedValue.TryCreate(
                GovernedLoopTypedValue.CurrentSchemaVersion,
                GovernedLoopValueKind.Number,
                value,
                out var canonical,
                out _)
            && string.Equals(canonical!.CanonicalValueJson, value, StringComparison.Ordinal)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }

    private static GovernedLoopPortDefinition? InputPort(GovernedLoopNodeDefinition node, string id)
        => node.Ports.SingleOrDefault(port => port.Direction == GovernedLoopPortDirection.Input && string.Equals(port.Id, id, StringComparison.Ordinal));

    private static GovernedLoopPortDefinition? OutputPort(GovernedLoopNodeDefinition node, string id)
        => node.Ports.SingleOrDefault(port => port.Direction == GovernedLoopPortDirection.Output && string.Equals(port.Id, id, StringComparison.Ordinal));

    private static bool TryGetParameter(GovernedLoopNodeDefinition node, string id, out string value)
        => node.Parameters.TryGetValue(id, out value!);

    private static string DescriptorKey(GovernedLoopNodeCatalogDescriptor value)
        => $"{(int)value.Descriptor.Kind:D3}:{value.Descriptor.TypeId}:{value.Descriptor.Version:D10}";
}
