using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Executes the closed schema-1 pure-node catalog without effects, ambient inputs, clocks, or provider access.</summary>
public static class GovernedLoopPureNodeEvaluator
{
    /// <summary>Evaluates one exact graph-bound Transform or Validate node.</summary>
    /// <param name="graph">The exact canonical graph revision.</param>
    /// <param name="nodeId">The exact Transform or Validate node identity.</param>
    /// <param name="inputs">Every exact materialized data binding targeting the node.</param>
    /// <param name="output">The one deterministic graph-pinned output on success.</param>
    /// <param name="validationEvidence">The deterministic validator evidence, or <see langword="null"/> for Transform.</param>
    /// <param name="validation">Bounded structured execution rejection evidence.</param>
    /// <returns><see langword="true"/> when execution succeeded; a false validator result is still successful execution.</returns>
    public static bool TryEvaluate(
        GovernedLoopGraphDefinition? graph,
        string? nodeId,
        IEnumerable<GovernedLoopTypedBindingValue>? inputs,
        [NotNullWhen(true)] out GovernedLoopTypedNodeOutput? output,
        out GovernedLoopValidationEvidence? validationEvidence,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        output = null;
        validationEvidence = null;
        if (graph is null)
        {
            validation = Invalid("pure-node.graph-required", "$.graphRevision", "An exact canonical graph revision is required.");
            return false;
        }

        if (!CustomLoopArtifactIdentifier.IsValid(nodeId))
        {
            validation = Invalid("pure-node.node-invalid", "$.nodeId", "A canonical graph node identity is required.");
            return false;
        }

        if (!TrySnapshot(inputs, out var inputSnapshot, out validation))
        {
            return false;
        }

        var node = graph.Nodes.SingleOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (node is null || node.Descriptor is null || node.Ports is null || node.Parameters is null || node.AuthorityCeiling is null)
        {
            validation = Invalid("pure-node.node-missing", "$.nodeId", "The pure node must exist in the exact graph revision.");
            return false;
        }

        if (!IsSupportedDescriptor(node.Descriptor))
        {
            validation = Invalid("pure-node.descriptor-unsupported", "$.descriptor", "The node requires one exact admitted schema-1 Transform or Validate descriptor.");
            return false;
        }

        if (node.AuthorityCeiling.CapabilityIds.Count != 0)
        {
            validation = Invalid("pure-node.authority-invalid", "$.descriptor", "Pure nodes cannot carry actuator authority.");
            return false;
        }

        if (!ValidateInputs(graph, node, inputSnapshot!, out validation))
        {
            return false;
        }

        return node.Descriptor.Kind == GovernedLoopNodeKind.Transform
            ? TryEvaluateTransform(graph, node, inputSnapshot!, out output, out validationEvidence, out validation)
            : TryEvaluateValidator(graph, node, inputSnapshot!, out output, out validationEvidence, out validation);
    }

    private static bool TryEvaluateTransform(
        GovernedLoopGraphDefinition graph,
        GovernedLoopNodeDefinition node,
        GovernedLoopTypedBindingValue[] inputs,
        out GovernedLoopTypedNodeOutput? output,
        out GovernedLoopValidationEvidence? evidence,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        output = null;
        evidence = null;
        return node.Descriptor.TypeId switch
        {
            GovernedLoopPureNodeVocabulary.IdentityTransform => TryIdentity(graph, node, inputs, out output, out validation),
            GovernedLoopPureNodeVocabulary.StructuredSelect => TryStructuredSelect(graph, node, inputs, out output, out validation),
            GovernedLoopPureNodeVocabulary.OrderedTextConcat => TryOrderedTextConcat(graph, node, inputs, out output, out validation),
            _ => Unsupported(out validation)
        };
    }

    private static bool TryEvaluateValidator(
        GovernedLoopGraphDefinition graph,
        GovernedLoopNodeDefinition node,
        GovernedLoopTypedBindingValue[] inputs,
        out GovernedLoopTypedNodeOutput? output,
        out GovernedLoopValidationEvidence? evidence,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        output = null;
        evidence = null;
        bool passed;
        string? failureCode;
        switch (node.Descriptor.TypeId)
        {
            case GovernedLoopPureNodeVocabulary.SchemaConformance:
                if (!HasExactContract(node, [Input(GovernedLoopPureNodeVocabulary.InputPort), Output(GovernedLoopPureNodeVocabulary.ResultPort)], []))
                {
                    return InvalidContract(out validation);
                }

                var schemaInput = InputByPort(inputs, GovernedLoopPureNodeVocabulary.InputPort);
                if (schemaInput is null || HasFormat(graph, schemaInput.ValueSchemaId, new HashSet<string>(StringComparer.Ordinal)))
                {
                    validation = Invalid("pure-node.schema-unsupported", "$.descriptor", "Schema conformance supports the exact structural schema-1 vocabulary and no declared formats.");
                    return false;
                }

                passed = true;
                failureCode = null;
                break;
            case GovernedLoopPureNodeVocabulary.CanonicalEquality:
                if (!HasExactContract(node, [Input(GovernedLoopPureNodeVocabulary.LeftPort), Input(GovernedLoopPureNodeVocabulary.RightPort), Output(GovernedLoopPureNodeVocabulary.ResultPort)], []))
                {
                    return InvalidContract(out validation);
                }

                var left = InputByPort(inputs, GovernedLoopPureNodeVocabulary.LeftPort);
                var right = InputByPort(inputs, GovernedLoopPureNodeVocabulary.RightPort);
                if (left is null || right is null || left.Value.Kind != right.Value.Kind)
                {
                    return InvalidContract(out validation);
                }

                passed = left.Value.Equals(right.Value);
                failureCode = "canonical-values-differ";
                break;
            case GovernedLoopPureNodeVocabulary.InclusiveIntegerRange:
                if (!TryIntegerRange(node, inputs, out passed, out failureCode))
                {
                    return InvalidContract(out validation);
                }

                break;
            case GovernedLoopPureNodeVocabulary.InclusiveNumberRange:
                if (!TryNumberRange(node, inputs, out passed, out failureCode))
                {
                    return InvalidContract(out validation);
                }

                break;
            case GovernedLoopPureNodeVocabulary.TextLength:
                if (!TryLength(node, inputs, GovernedLoopValueKind.Text, out passed, out failureCode))
                {
                    return InvalidContract(out validation);
                }

                break;
            case GovernedLoopPureNodeVocabulary.ArrayLength:
                if (!TryLength(node, inputs, GovernedLoopValueKind.Array, out passed, out failureCode))
                {
                    return InvalidContract(out validation);
                }

                break;
            default:
                return Unsupported(out validation);
        }

        evidence = GovernedLoopValidationEvidence.Create(
            GovernedLoopValidationEvidence.CurrentSchemaVersion,
            passed,
            passed ? [] : [GovernedLoopValidationObservation.Create(failureCode!, string.Empty)]);
        if (!TryBooleanOutput(graph, node, passed, out output))
        {
            evidence = null;
            validation = Invalid("pure-node.output-schema-mismatch", "$.outputs", "The validator result cannot materialize through its exact required Boolean output schema.");
            return false;
        }

        validation = Valid();
        return true;
    }

    private static bool TryIdentity(
        GovernedLoopGraphDefinition graph,
        GovernedLoopNodeDefinition node,
        GovernedLoopTypedBindingValue[] inputs,
        out GovernedLoopTypedNodeOutput? output,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        output = null;
        var input = InputByPort(inputs, GovernedLoopPureNodeVocabulary.InputPort);
        if (!HasExactContract(node, [Input(GovernedLoopPureNodeVocabulary.InputPort), Output(GovernedLoopPureNodeVocabulary.OutputPort)], [])
            || input is null
            || !TryOutput(graph, node, GovernedLoopPureNodeVocabulary.OutputPort, input.Value, out output))
        {
            return InvalidContract(out validation);
        }

        validation = Valid();
        return true;
    }

    private static bool TryStructuredSelect(
        GovernedLoopGraphDefinition graph,
        GovernedLoopNodeDefinition node,
        GovernedLoopTypedBindingValue[] inputs,
        out GovernedLoopTypedNodeOutput? output,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        output = null;
        var input = InputByPort(inputs, GovernedLoopPureNodeVocabulary.InputPort);
        if (!HasExactContract(node, [Input(GovernedLoopPureNodeVocabulary.InputPort), Output(GovernedLoopPureNodeVocabulary.OutputPort)], [GovernedLoopPureNodeVocabulary.PointerParameter])
            || input is null
            || input.Value.IsNull
            || input.Value.Kind is not (GovernedLoopValueKind.Object or GovernedLoopValueKind.Array)
            || !node.Parameters.TryGetValue(GovernedLoopPureNodeVocabulary.PointerParameter, out var pointer)
            || !IsPointer(pointer))
        {
            return InvalidContract(out validation);
        }

        using var document = JsonDocument.Parse(input.Value.CanonicalValueJson);
        if (!TrySelect(document.RootElement, pointer, out var selected))
        {
            validation = Invalid("pure-node.selection-missing", "$.inputs", "The exact RFC 6901 selection does not exist in the admitted structured input.");
            return false;
        }

        var outputPort = node.Ports.Single(port => string.Equals(port.Id, GovernedLoopPureNodeVocabulary.OutputPort, StringComparison.Ordinal));
        var outputSchema = graph.ValueSchemas.Single(schema => string.Equals(schema.Id, outputPort.ValueSchemaId, StringComparison.Ordinal));
        if (!GovernedLoopTypedValue.TryCreate(GovernedLoopTypedValue.CurrentSchemaVersion, outputSchema.Kind, selected.GetRawText(), out var selectedValue, out _)
            || !TryOutput(graph, node, GovernedLoopPureNodeVocabulary.OutputPort, selectedValue!, out output))
        {
            validation = Invalid("pure-node.output-schema-mismatch", "$.outputs", "The selected value does not conform to the exact declared output schema.");
            return false;
        }

        validation = Valid();
        return true;
    }

    private static bool TryOrderedTextConcat(
        GovernedLoopGraphDefinition graph,
        GovernedLoopNodeDefinition node,
        GovernedLoopTypedBindingValue[] inputs,
        out GovernedLoopTypedNodeOutput? output,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        output = null;
        var input = InputByPort(inputs, GovernedLoopPureNodeVocabulary.ValuesPort);
        if (!HasExactContract(node, [Input(GovernedLoopPureNodeVocabulary.ValuesPort), Output(GovernedLoopPureNodeVocabulary.OutputPort)], [GovernedLoopPureNodeVocabulary.SeparatorParameter])
            || input is null
            || input.Value.IsNull
            || input.Value.Kind != GovernedLoopValueKind.Array
            || !node.Parameters.TryGetValue(GovernedLoopPureNodeVocabulary.SeparatorParameter, out var separator)
            || !GovernedLoopPureNodeTextRules.IsSafe(separator, CustomLoopLimits.MaxGraphParameterValueCharacters)
            || !IsNonNullableTextArray(graph, input.ValueSchemaId))
        {
            return InvalidContract(out validation);
        }

        using var document = JsonDocument.Parse(input.Value.CanonicalValueJson);
        var values = document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray();
        long characterCount = values.Sum(value => (long)value.Length) + Math.Max(0, values.Length - 1L) * separator.Length;
        if (characterCount > CustomLoopLimits.MaxGraphTypedValueStringCharacters)
        {
            validation = Invalid("pure-node.output-size-exceeded", "$.outputs", "The ordered concatenation exceeds the bounded typed-text contract.");
            return false;
        }

        var joined = string.Join(separator, values);
        if (!GovernedLoopTypedValue.TryCreate(GovernedLoopTypedValue.CurrentSchemaVersion, GovernedLoopValueKind.Text, JsonSerializer.Serialize(joined), out var joinedValue, out _)
            || !TryOutput(graph, node, GovernedLoopPureNodeVocabulary.OutputPort, joinedValue!, out output))
        {
            validation = Invalid("pure-node.output-schema-mismatch", "$.outputs", "The concatenated value does not conform to the exact declared output schema.");
            return false;
        }

        validation = Valid();
        return true;
    }

    private static bool TryIntegerRange(GovernedLoopNodeDefinition node, GovernedLoopTypedBindingValue[] inputs, out bool passed, out string? failureCode)
    {
        passed = false;
        failureCode = null;
        var input = InputByPort(inputs, GovernedLoopPureNodeVocabulary.InputPort);
        if (!HasExactContract(node, [Input(GovernedLoopPureNodeVocabulary.InputPort), Output(GovernedLoopPureNodeVocabulary.ResultPort)], [GovernedLoopPureNodeVocabulary.MinimumParameter, GovernedLoopPureNodeVocabulary.MaximumParameter])
            || input is null
            || input.Value.Kind != GovernedLoopValueKind.Integer
            || !TryCanonicalInt64(node.Parameters[GovernedLoopPureNodeVocabulary.MinimumParameter], out var minimum)
            || !TryCanonicalInt64(node.Parameters[GovernedLoopPureNodeVocabulary.MaximumParameter], out var maximum)
            || minimum > maximum)
        {
            return false;
        }

        if (input.Value.IsNull)
        {
            failureCode = "required-value-missing";
            return true;
        }

        var value = long.Parse(input.Value.CanonicalValueJson, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        passed = value >= minimum && value <= maximum;
        failureCode = "integer-outside-range";
        return true;
    }

    private static bool TryNumberRange(GovernedLoopNodeDefinition node, GovernedLoopTypedBindingValue[] inputs, out bool passed, out string? failureCode)
    {
        passed = false;
        failureCode = null;
        var input = InputByPort(inputs, GovernedLoopPureNodeVocabulary.InputPort);
        if (!HasExactContract(node, [Input(GovernedLoopPureNodeVocabulary.InputPort), Output(GovernedLoopPureNodeVocabulary.ResultPort)], [GovernedLoopPureNodeVocabulary.MinimumParameter, GovernedLoopPureNodeVocabulary.MaximumParameter])
            || input is null
            || input.Value.Kind != GovernedLoopValueKind.Number
            || !TryCanonicalFiniteNumber(node.Parameters[GovernedLoopPureNodeVocabulary.MinimumParameter], out var minimum)
            || !TryCanonicalFiniteNumber(node.Parameters[GovernedLoopPureNodeVocabulary.MaximumParameter], out var maximum)
            || minimum > maximum)
        {
            return false;
        }

        if (input.Value.IsNull)
        {
            failureCode = "required-value-missing";
            return true;
        }

        var value = double.Parse(input.Value.CanonicalValueJson, NumberStyles.Float, CultureInfo.InvariantCulture);
        passed = value >= minimum && value <= maximum;
        failureCode = "number-outside-range";
        return true;
    }

    private static bool TryLength(
        GovernedLoopNodeDefinition node,
        GovernedLoopTypedBindingValue[] inputs,
        GovernedLoopValueKind expectedKind,
        out bool passed,
        out string? failureCode)
    {
        passed = false;
        failureCode = null;
        var input = InputByPort(inputs, GovernedLoopPureNodeVocabulary.InputPort);
        if (!HasExactContract(node, [Input(GovernedLoopPureNodeVocabulary.InputPort), Output(GovernedLoopPureNodeVocabulary.ResultPort)], [GovernedLoopPureNodeVocabulary.MinimumParameter, GovernedLoopPureNodeVocabulary.MaximumParameter])
            || input is null
            || input.Value.Kind != expectedKind
            || !TryCanonicalNonNegativeInt32(node.Parameters[GovernedLoopPureNodeVocabulary.MinimumParameter], out var minimum)
            || !TryCanonicalNonNegativeInt32(node.Parameters[GovernedLoopPureNodeVocabulary.MaximumParameter], out var maximum)
            || minimum > maximum)
        {
            return false;
        }

        if (input.Value.IsNull)
        {
            failureCode = "required-value-missing";
            return true;
        }

        using var document = JsonDocument.Parse(input.Value.CanonicalValueJson);
        var length = expectedKind == GovernedLoopValueKind.Text
            ? document.RootElement.GetString()!.EnumerateRunes().Count()
            : document.RootElement.GetArrayLength();
        passed = length >= minimum && length <= maximum;
        failureCode = expectedKind == GovernedLoopValueKind.Text ? "text-length-outside-range" : "array-length-outside-range";
        return true;
    }

    private static bool TryBooleanOutput(GovernedLoopGraphDefinition graph, GovernedLoopNodeDefinition node, bool value, out GovernedLoopTypedNodeOutput? output)
    {
        output = null;
        if (!GovernedLoopTypedValue.TryCreate(GovernedLoopTypedValue.CurrentSchemaVersion, GovernedLoopValueKind.Boolean, value ? "true" : "false", out var typedValue, out _))
        {
            return false;
        }

        return TryOutput(graph, node, GovernedLoopPureNodeVocabulary.ResultPort, typedValue!, out output);
    }

    private static bool TryOutput(
        GovernedLoopGraphDefinition graph,
        GovernedLoopNodeDefinition node,
        string portId,
        GovernedLoopTypedValue value,
        out GovernedLoopTypedNodeOutput? output)
    {
        output = null;
        try
        {
            output = GovernedLoopTypedNodeOutput.Create(graph, node.Id, portId, value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ValidateInputs(
        GovernedLoopGraphDefinition graph,
        GovernedLoopNodeDefinition node,
        GovernedLoopTypedBindingValue[] inputs,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        var expected = graph.Bindings.Where(binding => string.Equals(binding.ToNodeId, node.Id, StringComparison.Ordinal)).OrderBy(binding => binding.Id, StringComparer.Ordinal).ToArray();
        if (inputs.Length != expected.Length
            || inputs.Select(value => value.BindingId).Distinct(StringComparer.Ordinal).Count() != inputs.Length
            || inputs.Select(value => value.TargetPortId).Distinct(StringComparer.Ordinal).Count() != inputs.Length)
        {
            validation = Invalid("pure-node.inputs-inexact", "$.inputs", "Inputs must contain every exact target binding once and no ambient values.");
            return false;
        }

        var expectedById = expected.ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var port = node.Ports.SingleOrDefault(value => string.Equals(value.Id, input.TargetPortId, StringComparison.Ordinal));
            if (!expectedById.TryGetValue(input.BindingId, out var binding)
                || port is null
                || !Equals(input.GraphRevision, graph.RevisionReference)
                || input.BindingKind != GovernedLoopBindingKind.Data
                || input.BindingKind != binding.Kind
                || !string.Equals(input.SourceNodeId, binding.FromNodeId, StringComparison.Ordinal)
                || !string.Equals(input.SourcePortId, binding.FromPortId, StringComparison.Ordinal)
                || !string.Equals(input.TargetNodeId, binding.ToNodeId, StringComparison.Ordinal)
                || !string.Equals(input.TargetPortId, binding.ToPortId, StringComparison.Ordinal)
                || !string.Equals(input.ValueSchemaId, port.ValueSchemaId, StringComparison.Ordinal)
                || input.Value is null)
            {
                validation = Invalid("pure-node.input-substituted", "$.inputs", "An input does not match its exact graph revision, binding, data channel, or schema.");
                return false;
            }
        }

        validation = Valid();
        return true;
    }

    private static bool HasExactContract(
        GovernedLoopNodeDefinition node,
        IReadOnlyList<(string Id, GovernedLoopPortDirection Direction)> ports,
        IReadOnlyList<string> parameters)
    {
        if (node.Ports.Count != ports.Count
            || node.Parameters.Count != parameters.Count
            || !node.Parameters.Keys.Order(StringComparer.Ordinal).SequenceEqual(parameters.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }

        var expected = ports.ToDictionary(value => value.Id, StringComparer.Ordinal);
        return node.Ports.All(port => expected.TryGetValue(port.Id, out var contract)
            && port.Direction == contract.Direction
            && port.BindingKind == GovernedLoopBindingKind.Data
            && port.Required);
    }

    private static GovernedLoopTypedBindingValue? InputByPort(IEnumerable<GovernedLoopTypedBindingValue> inputs, string portId)
        => inputs.SingleOrDefault(input => string.Equals(input.TargetPortId, portId, StringComparison.Ordinal));

    private static (string Id, GovernedLoopPortDirection Direction) Input(string id) => (id, GovernedLoopPortDirection.Input);

    private static (string Id, GovernedLoopPortDirection Direction) Output(string id) => (id, GovernedLoopPortDirection.Output);

    private static bool TrySnapshot(
        IEnumerable<GovernedLoopTypedBindingValue>? inputs,
        [NotNullWhen(true)] out GovernedLoopTypedBindingValue[]? snapshot,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        snapshot = null;
        if (inputs is null)
        {
            validation = Invalid("pure-node.inputs-required", "$.inputs", "Pure-node inputs are required.");
            return false;
        }

        try
        {
            snapshot = inputs.Take(CustomLoopLimits.MaxGraphPortsPerNode + 1).ToArray();
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            validation = Invalid("pure-node.inputs-invalid", "$.inputs", "Pure-node inputs could not be inspected within the bounded contract.");
            return false;
        }

        if (snapshot.Length > CustomLoopLimits.MaxGraphPortsPerNode || snapshot.Any(value => value is null))
        {
            snapshot = null;
            validation = Invalid("pure-node.inputs-invalid", "$.inputs", "Pure-node inputs must be bounded and contain no null values.");
            return false;
        }

        validation = Valid();
        return true;
    }

    private static bool TrySelect(JsonElement root, string pointer, out JsonElement selected)
    {
        selected = root;
        if (pointer.Length == 0)
        {
            return true;
        }

        foreach (var encodedSegment in pointer[1..].Split('/'))
        {
            var segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (selected.ValueKind == JsonValueKind.Object)
            {
                if (!selected.TryGetProperty(segment, out selected))
                {
                    return false;
                }

                continue;
            }

            if (selected.ValueKind != JsonValueKind.Array
                || !TryCanonicalNonNegativeInt32(segment, out var index)
                || index >= selected.GetArrayLength())
            {
                return false;
            }

            selected = selected[index];
        }

        return true;
    }

    private static bool IsPointer(string value)
    {
        if (!GovernedLoopPureNodeTextRules.IsSafe(value, CustomLoopLimits.MaxGraphParameterValueCharacters))
        {
            return false;
        }

        if (value.Length == 0)
        {
            return true;
        }

        if (value[0] != '/')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '~' && (++index >= value.Length || value[index] is not ('0' or '1')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNonNullableTextArray(GovernedLoopGraphDefinition graph, string schemaId)
    {
        var array = graph.ValueSchemas.Single(schema => string.Equals(schema.Id, schemaId, StringComparison.Ordinal));
        var element = array.ElementSchemaId is null ? null : graph.ValueSchemas.SingleOrDefault(schema => string.Equals(schema.Id, array.ElementSchemaId, StringComparison.Ordinal));
        return array.Kind == GovernedLoopValueKind.Array && array.Format is null && element is { Kind: GovernedLoopValueKind.Text, Nullable: false, Format: null, ElementSchemaId: null };
    }

    private static bool HasFormat(GovernedLoopGraphDefinition graph, string schemaId, HashSet<string> active)
    {
        var schema = graph.ValueSchemas.SingleOrDefault(item => string.Equals(item.Id, schemaId, StringComparison.Ordinal));
        if (schema is null || !active.Add(schema.Id))
        {
            return true;
        }

        var hasFormat = schema.Format is not null
            || schema.Kind == GovernedLoopValueKind.Array
            && (schema.ElementSchemaId is null || HasFormat(graph, schema.ElementSchemaId, active));
        active.Remove(schema.Id);
        return hasFormat;
    }

    private static bool TryCanonicalInt64(string value, out long result)
        => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result)
            && string.Equals(value, result.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool TryCanonicalNonNegativeInt32(string value, out int result)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && string.Equals(value, result.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool TryCanonicalFiniteNumber(string value, out double result)
    {
        result = default;
        return GovernedLoopTypedValue.TryCreate(GovernedLoopTypedValue.CurrentSchemaVersion, GovernedLoopValueKind.Number, value, out var typed, out _)
            && !typed!.IsNull
            && double.TryParse(typed.CanonicalValueJson, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            && double.IsFinite(result);
    }

    private static bool IsSupportedDescriptor(GovernedLoopNodeDescriptor descriptor)
        => descriptor.Version == GovernedLoopPureNodeVocabulary.DescriptorVersion
            && (descriptor.Kind == GovernedLoopNodeKind.Transform && GovernedLoopPureNodeVocabulary.IsTransform(descriptor.TypeId)
                || descriptor.Kind == GovernedLoopNodeKind.Validate && GovernedLoopPureNodeVocabulary.IsValidate(descriptor.TypeId));

    private static bool InvalidContract(out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        validation = Invalid("pure-node.contract-invalid", "$.descriptor", "The node violates its exact operator ports, parameters, or value-kind contract.");
        return false;
    }

    private static bool Unsupported(out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        validation = Invalid("pure-node.descriptor-unsupported", "$.descriptor", "The pure-node descriptor is not in the closed schema-1 catalog.");
        return false;
    }

    private static GovernedLoopPureNodeOutcomeValidationResult Valid() => new([]);

    private static GovernedLoopPureNodeOutcomeValidationResult Invalid(string code, string path, string message)
        => new([GovernedLoopPureNodeOutcomeError.Create(code, path, message)]);
}
