using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Creates and strictly reads canonical schema-1 pure-node outcome artifacts.</summary>
public static class GovernedLoopPureNodeOutcomeJson
{
    /// <summary>Creates one exact graph-bound pure-node outcome artifact.</summary>
    /// <param name="graph">The exact canonical graph revision.</param>
    /// <param name="nodeId">The exact Transform or Validate node identity.</param>
    /// <param name="inputs">Every exact materialized binding targeting the node.</param>
    /// <param name="outputs">Every materialized node output.</param>
    /// <param name="validationEvidence">Required only for Validate outcomes.</param>
    /// <param name="outcome">The immutable canonical artifact on success.</param>
    /// <param name="validation">The deterministic validation result.</param>
    /// <returns><see langword="true"/> only when the complete outcome is valid.</returns>
    public static bool TryCreate(
        GovernedLoopGraphDefinition? graph,
        string? nodeId,
        IEnumerable<GovernedLoopTypedBindingValue>? inputs,
        IEnumerable<GovernedLoopTypedNodeOutput>? outputs,
        GovernedLoopValidationEvidence? validationEvidence,
        out GovernedLoopPureNodeOutcome? outcome,
        out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        outcome = null;
        if (graph is null)
        {
            validation = Invalid("pure-outcome.graph-required", "$.graphRevision", "An exact canonical graph revision is required.");
            return false;
        }

        if (!CustomLoopArtifactIdentifier.IsValid(nodeId))
        {
            validation = Invalid("pure-outcome.node-invalid", "$.nodeId", "A canonical graph node identity is required.");
            return false;
        }

        if (!TrySnapshot(inputs, "$.inputs", out var inputSnapshot, out validation) || !TrySnapshot(outputs, "$.outputs", out var outputSnapshot, out validation))
        {
            return false;
        }

        var node = graph.Nodes.SingleOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (node is null || node.Descriptor is null || node.Ports is null || node.AuthorityCeiling is null)
        {
            validation = Invalid("pure-outcome.node-missing", "$.nodeId", "The outcome node must exist in the exact graph revision.");
            return false;
        }

        if (node.Descriptor.Version != GovernedLoopPureNodeVocabulary.DescriptorVersion
            || node.Descriptor.Kind == GovernedLoopNodeKind.Transform && !GovernedLoopPureNodeVocabulary.IsTransform(node.Descriptor.TypeId)
            || node.Descriptor.Kind == GovernedLoopNodeKind.Validate && !GovernedLoopPureNodeVocabulary.IsValidate(node.Descriptor.TypeId)
            || node.Descriptor.Kind is not (GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate))
        {
            validation = Invalid("pure-outcome.descriptor-unsupported", "$.descriptor", "The outcome requires one exact admitted schema-1 Transform or Validate descriptor.");
            return false;
        }

        if (node.AuthorityCeiling.CapabilityIds.Count != 0)
        {
            validation = Invalid("pure-outcome.authority-invalid", "$.descriptor", "Pure-node outcomes cannot carry node actuator authority.");
            return false;
        }

        if (!ValidateInputs(graph, node, inputSnapshot!, out validation) || !ValidateOutputs(graph, node, outputSnapshot!, out validation))
        {
            return false;
        }

        var canonicalInputs = inputSnapshot!.OrderBy(value => value.BindingId, StringComparer.Ordinal).ToArray();
        var canonicalOutputs = outputSnapshot!.OrderBy(value => value.PortId, StringComparer.Ordinal).ToArray();
        if (!GovernedLoopPureNodeEvaluator.TryEvaluate(graph, node.Id, canonicalInputs, out var expectedOutput, out var expectedEvidence, out validation))
        {
            return false;
        }

        if (canonicalOutputs.Length != 1
            || !MatchesOutput(canonicalOutputs[0], expectedOutput)
            || !MatchesEvidence(validationEvidence, expectedEvidence))
        {
            validation = Invalid("pure-outcome.semantic-mismatch", "$", "The retained output or validation evidence does not equal deterministic execution of the exact graph-bound operator.");
            return false;
        }

        if (!WithinValueBudget(canonicalInputs, canonicalOutputs))
        {
            validation = Invalid("pure-outcome.size-exceeded", "$", "The pure-node outcome exceeds the schema-1 UTF-8 bound.");
            return false;
        }

        var graphRevision = GovernedLoopTypedBindingValue.Copy(graph.RevisionReference);
        var descriptor = node.Descriptor with { };
        var evidence = validationEvidence is null
            ? null
            : GovernedLoopValidationEvidence.Create(validationEvidence.SchemaVersion, validationEvidence.Passed, validationEvidence.Observations.Select(item => GovernedLoopValidationObservation.Create(item.Code, item.Path)));
        var payload = WriteDocument(graphRevision, node.Id, descriptor, canonicalInputs, canonicalOutputs, evidence, contentHash: null);
        if (Encoding.UTF8.GetByteCount(payload) > CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes)
        {
            validation = Invalid("pure-outcome.size-exceeded", "$", "The pure-node outcome exceeds the schema-1 UTF-8 bound.");
            return false;
        }

        var contentHash = GovernedLoopPureNodeOutcomeHash.ComputeCanonical(payload);
        var canonicalJson = WriteDocument(graphRevision, node.Id, descriptor, canonicalInputs, canonicalOutputs, evidence, contentHash);
        if (Encoding.UTF8.GetByteCount(canonicalJson) > CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes)
        {
            validation = Invalid("pure-outcome.size-exceeded", "$", "The pure-node outcome exceeds the schema-1 UTF-8 bound.");
            return false;
        }

        outcome = new GovernedLoopPureNodeOutcome(graphRevision, node.Id, descriptor, canonicalInputs, canonicalOutputs, evidence, payload, canonicalJson, contentHash);
        validation = Valid();
        return true;
    }

    private static bool MatchesOutput(GovernedLoopTypedNodeOutput actual, GovernedLoopTypedNodeOutput expected)
        => Equals(actual.GraphRevision, expected.GraphRevision)
            && string.Equals(actual.NodeId, expected.NodeId, StringComparison.Ordinal)
            && string.Equals(actual.PortId, expected.PortId, StringComparison.Ordinal)
            && actual.BindingKind == expected.BindingKind
            && string.Equals(actual.ValueSchemaId, expected.ValueSchemaId, StringComparison.Ordinal)
            && actual.Value.Equals(expected.Value);

    private static bool MatchesEvidence(GovernedLoopValidationEvidence? actual, GovernedLoopValidationEvidence? expected)
        => actual is null && expected is null
            || actual is not null
            && expected is not null
            && actual.SchemaVersion == expected.SchemaVersion
            && actual.Passed == expected.Passed
            && actual.Observations.SequenceEqual(expected.Observations);

    /// <summary>Reads one exact canonical outcome against its immutable graph revision.</summary>
    /// <param name="graph">The exact canonical graph revision.</param>
    /// <param name="json">The candidate canonical outcome artifact.</param>
    /// <param name="outcome">The immutable verified artifact on success.</param>
    /// <param name="validation">The deterministic validation result.</param>
    /// <returns><see langword="true"/> only when the input is byte-for-byte canonical and graph-exact.</returns>
    public static bool TryDeserialize(GovernedLoopGraphDefinition? graph, string? json, out GovernedLoopPureNodeOutcome? outcome, out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        outcome = null;
        if (graph is null)
        {
            validation = Invalid("pure-outcome.graph-required", "$.graphRevision", "An exact canonical graph revision is required.");
            return false;
        }

        if (string.IsNullOrEmpty(json) || json.Length > CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes || Encoding.UTF8.GetByteCount(json) > CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes)
        {
            validation = Invalid("pure-outcome.document-invalid", "$", "A bounded canonical pure-node outcome artifact is required.");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = CustomLoopLimits.MaxGraphTypedValueDepth + 8
            });
            if (!TryObject(document.RootElement, ["schemaVersion", "graphRevision", "nodeId", "descriptor", "inputs", "outputs", "validationEvidence", "contentHash"], out var root)
                || !TryInt32(root!, "schemaVersion", out var schemaVersion)
                || schemaVersion != GovernedLoopPureNodeOutcome.CurrentSchemaVersion
                || !TryString(root, "nodeId", out var nodeId)
                || !TryString(root, "contentHash", out var claimedHash)
                || !TryGraphRevision(root["graphRevision"], graph.RevisionReference)
                || !TryDescriptor(root["descriptor"], out var descriptor)
                || !TryInputs(root["inputs"], graph, out var inputs)
                || !TryOutputs(root["outputs"], graph, out var outputs)
                || !TryValidationEvidence(root["validationEvidence"], out var evidence))
            {
                validation = Invalid("pure-outcome.document-shape", "$", "The pure-node outcome does not have the exact bounded schema-1 shape.");
                return false;
            }

            var node = graph.Nodes.SingleOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
            if (node is null || !Equals(node.Descriptor, descriptor))
            {
                validation = Invalid("pure-outcome.descriptor-substituted", "$.descriptor", "The outcome descriptor does not match the exact graph node descriptor.");
                return false;
            }

            if (!TryCreate(graph, nodeId, inputs!, outputs!, evidence, out var candidate, out validation))
            {
                return false;
            }

            if (!string.Equals(claimedHash, candidate!.ContentHash, StringComparison.Ordinal) || !GovernedLoopPureNodeOutcomeHash.Matches(candidate, claimedHash))
            {
                validation = Invalid("pure-outcome.hash-mismatch", "$.contentHash", "The outcome content hash does not authenticate the exact canonical payload.");
                return false;
            }

            if (!string.Equals(json, candidate.CanonicalJson, StringComparison.Ordinal))
            {
                validation = Invalid("pure-outcome.document-noncanonical", "$", "The pure-node outcome is valid only after normalization and is not accepted as durable canonical evidence.");
                return false;
            }

            outcome = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or EncoderFallbackException)
        {
            validation = Invalid("pure-outcome.document-malformed", "$", "The pure-node outcome is malformed or exceeds its bounded parse shape.");
            return false;
        }
    }

    private static bool ValidateInputs(GovernedLoopGraphDefinition graph, GovernedLoopNodeDefinition node, GovernedLoopTypedBindingValue[] inputs, out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        var expected = graph.Bindings.Where(binding => string.Equals(binding.ToNodeId, node.Id, StringComparison.Ordinal)).OrderBy(binding => binding.Id, StringComparer.Ordinal).ToArray();
        if (inputs.Length != expected.Length || inputs.Select(value => value.BindingId).Distinct(StringComparer.Ordinal).Count() != inputs.Length || inputs.Select(value => value.TargetPortId).Distinct(StringComparer.Ordinal).Count() != inputs.Length)
        {
            validation = Invalid("pure-outcome.inputs-inexact", "$.inputs", "Outcome inputs must contain every exact target binding once and no ambient values.");
            return false;
        }

        var expectedById = expected.ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (!expectedById.TryGetValue(input.BindingId, out var binding)
                || !Equals(input.GraphRevision, graph.RevisionReference)
                || input.BindingKind != binding.Kind
                || !string.Equals(input.SourceNodeId, binding.FromNodeId, StringComparison.Ordinal)
                || !string.Equals(input.SourcePortId, binding.FromPortId, StringComparison.Ordinal)
                || !string.Equals(input.TargetNodeId, binding.ToNodeId, StringComparison.Ordinal)
                || !string.Equals(input.TargetPortId, binding.ToPortId, StringComparison.Ordinal)
                || input.Value is null)
            {
                validation = Invalid("pure-outcome.input-substituted", "$.inputs", "A materialized input does not match its exact graph revision and binding.");
                return false;
            }

            var port = node.Ports.Single(value => string.Equals(value.Id, binding.ToPortId, StringComparison.Ordinal));
            if (!string.Equals(input.ValueSchemaId, port.ValueSchemaId, StringComparison.Ordinal) || input.BindingKind != GovernedLoopBindingKind.Data)
            {
                validation = Invalid("pure-outcome.input-substituted", "$.inputs", "A materialized input substitutes its exact schema or channel.");
                return false;
            }
        }

        validation = Valid();
        return true;
    }

    private static bool ValidateOutputs(GovernedLoopGraphDefinition graph, GovernedLoopNodeDefinition node, GovernedLoopTypedNodeOutput[] outputs, out GovernedLoopPureNodeOutcomeValidationResult validation)
    {
        var outputPorts = node.Ports.Where(port => port.Direction == GovernedLoopPortDirection.Output).OrderBy(port => port.Id, StringComparer.Ordinal).ToArray();
        if (outputs.Length > outputPorts.Length || outputs.Select(value => value.PortId).Distinct(StringComparer.Ordinal).Count() != outputs.Length)
        {
            validation = Invalid("pure-outcome.outputs-inexact", "$.outputs", "Outcome outputs must be unique and declared by the exact node.");
            return false;
        }

        var outputByPort = outputs.ToDictionary(value => value.PortId, StringComparer.Ordinal);
        if (outputPorts.Any(port => port.Required && !outputByPort.ContainsKey(port.Id)))
        {
            validation = Invalid("pure-outcome.outputs-inexact", "$.outputs", "Every required exact node output must be materialized.");
            return false;
        }

        var portById = outputPorts.ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            if (!portById.TryGetValue(output.PortId, out var port)
                || !Equals(output.GraphRevision, graph.RevisionReference)
                || !string.Equals(output.NodeId, node.Id, StringComparison.Ordinal)
                || output.BindingKind != port.BindingKind
                || output.BindingKind != GovernedLoopBindingKind.Data
                || !string.Equals(output.ValueSchemaId, port.ValueSchemaId, StringComparison.Ordinal)
                || output.Value is null)
            {
                validation = Invalid("pure-outcome.output-substituted", "$.outputs", "A materialized output does not match its exact graph revision, node, port, schema, and channel.");
                return false;
            }
        }

        validation = Valid();
        return true;
    }

    private static bool WithinValueBudget(IEnumerable<GovernedLoopTypedBindingValue> inputs, IEnumerable<GovernedLoopTypedNodeOutput> outputs)
    {
        long total = 16 * 1024;
        foreach (var value in inputs.Select(input => input.Value).Concat(outputs.Select(output => output.Value)))
        {
            total += Encoding.UTF8.GetByteCount(value.CanonicalJson) + 1024L;
            if (total > CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySnapshot<T>(IEnumerable<T>? values, string path, out T[]? snapshot, out GovernedLoopPureNodeOutcomeValidationResult validation) where T : class
    {
        snapshot = null;
        if (values is null)
        {
            validation = Invalid("pure-outcome.collection-required", path, "Pure-node outcome input and output collections are required.");
            return false;
        }

        T[] bounded;
        try
        {
            bounded = values.Take(CustomLoopLimits.MaxGraphPortsPerNode + 1).ToArray();
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            validation = Invalid("pure-outcome.collection-invalid", path, "Pure-node outcome collections could not be inspected within the bounded contract.");
            return false;
        }

        if (bounded.Length > CustomLoopLimits.MaxGraphPortsPerNode || bounded.Any(value => value is null))
        {
            validation = Invalid("pure-outcome.collection-invalid", path, "Pure-node outcome collections must be bounded and contain no null values.");
            return false;
        }

        snapshot = bounded;
        validation = Valid();
        return true;
    }

    private static string WriteDocument(
        GovernedLoopRevisionReference graphRevision,
        string nodeId,
        GovernedLoopNodeDescriptor descriptor,
        IReadOnlyList<GovernedLoopTypedBindingValue> inputs,
        IReadOnlyList<GovernedLoopTypedNodeOutput> outputs,
        GovernedLoopValidationEvidence? evidence,
        string? contentHash)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", GovernedLoopPureNodeOutcome.CurrentSchemaVersion);
            WriteGraphRevision(writer, graphRevision);
            writer.WriteString("nodeId", nodeId);
            WriteDescriptor(writer, descriptor);
            WriteInputs(writer, inputs);
            WriteOutputs(writer, outputs);
            WriteValidationEvidence(writer, evidence);
            if (contentHash is not null)
            {
                writer.WriteString("contentHash", contentHash);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteGraphRevision(Utf8JsonWriter writer, GovernedLoopRevisionReference revision)
    {
        writer.WritePropertyName("graphRevision");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", revision.SchemaVersion);
        writer.WriteString("graphId", revision.GraphId);
        writer.WriteString("revisionId", revision.RevisionId);
        writer.WriteString("executableHash", revision.ExecutableHash);
        writer.WriteEndObject();
    }

    private static void WriteDescriptor(Utf8JsonWriter writer, GovernedLoopNodeDescriptor descriptor)
    {
        writer.WritePropertyName("descriptor");
        writer.WriteStartObject();
        writer.WriteString("kind", descriptor.Kind == GovernedLoopNodeKind.Transform ? "transform" : "validate");
        writer.WriteString("typeId", descriptor.TypeId);
        writer.WriteNumber("version", descriptor.Version);
        writer.WriteEndObject();
    }

    private static void WriteInputs(Utf8JsonWriter writer, IReadOnlyList<GovernedLoopTypedBindingValue> inputs)
    {
        writer.WritePropertyName("inputs");
        writer.WriteStartArray();
        foreach (var input in inputs)
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", input.SchemaVersion);
            writer.WriteString("bindingId", input.BindingId);
            writer.WriteString("bindingKind", ToCanonical(input.BindingKind));
            writer.WriteString("sourceNodeId", input.SourceNodeId);
            writer.WriteString("sourcePortId", input.SourcePortId);
            writer.WriteString("targetNodeId", input.TargetNodeId);
            writer.WriteString("targetPortId", input.TargetPortId);
            writer.WriteString("valueSchemaId", input.ValueSchemaId);
            writer.WritePropertyName("value");
            writer.WriteRawValue(input.Value.CanonicalJson, skipInputValidation: true);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOutputs(Utf8JsonWriter writer, IReadOnlyList<GovernedLoopTypedNodeOutput> outputs)
    {
        writer.WritePropertyName("outputs");
        writer.WriteStartArray();
        foreach (var output in outputs)
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", output.SchemaVersion);
            writer.WriteString("nodeId", output.NodeId);
            writer.WriteString("portId", output.PortId);
            writer.WriteString("bindingKind", ToCanonical(output.BindingKind));
            writer.WriteString("valueSchemaId", output.ValueSchemaId);
            writer.WritePropertyName("value");
            writer.WriteRawValue(output.Value.CanonicalJson, skipInputValidation: true);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteValidationEvidence(Utf8JsonWriter writer, GovernedLoopValidationEvidence? evidence)
    {
        writer.WritePropertyName("validationEvidence");
        if (evidence is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WriteBoolean("passed", evidence.Passed);
        writer.WritePropertyName("observations");
        writer.WriteStartArray();
        foreach (var observation in evidence.Observations)
        {
            writer.WriteStartObject();
            writer.WriteString("code", observation.Code);
            writer.WriteString("path", observation.Path);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool TryGraphRevision(JsonElement element, GovernedLoopRevisionReference expected)
    {
        return TryObject(element, ["schemaVersion", "graphId", "revisionId", "executableHash"], out var value)
            && TryInt32(value!, "schemaVersion", out var schemaVersion)
            && TryString(value, "graphId", out var graphId)
            && TryString(value, "revisionId", out var revisionId)
            && TryString(value, "executableHash", out var executableHash)
            && schemaVersion == expected.SchemaVersion
            && string.Equals(graphId, expected.GraphId, StringComparison.Ordinal)
            && string.Equals(revisionId, expected.RevisionId, StringComparison.Ordinal)
            && string.Equals(executableHash, expected.ExecutableHash, StringComparison.Ordinal);
    }

    private static bool TryDescriptor(JsonElement element, [NotNullWhen(true)] out GovernedLoopNodeDescriptor? descriptor)
    {
        descriptor = null;
        if (!TryObject(element, ["kind", "typeId", "version"], out var value)
            || !TryString(value!, "kind", out var kindToken)
            || !TryString(value, "typeId", out var typeId)
            || !TryInt32(value, "version", out var version))
        {
            return false;
        }

        var kind = kindToken switch
        {
            "transform" => GovernedLoopNodeKind.Transform,
            "validate" => GovernedLoopNodeKind.Validate,
            _ => GovernedLoopNodeKind.Unknown
        };
        if (kind == GovernedLoopNodeKind.Unknown)
        {
            return false;
        }

        descriptor = new GovernedLoopNodeDescriptor(kind, typeId!, version);
        return true;
    }

    private static bool TryInputs(JsonElement element, GovernedLoopGraphDefinition graph, [NotNullWhen(true)] out GovernedLoopTypedBindingValue[]? inputs)
    {
        inputs = null;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > CustomLoopLimits.MaxGraphPortsPerNode)
        {
            return false;
        }

        var values = new List<GovernedLoopTypedBindingValue>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryObject(item, ["schemaVersion", "bindingId", "bindingKind", "sourceNodeId", "sourcePortId", "targetNodeId", "targetPortId", "valueSchemaId", "value"], out var fields)
                || !TryInt32(fields!, "schemaVersion", out var schemaVersion)
                || schemaVersion != GovernedLoopTypedBindingValue.CurrentSchemaVersion
                || !TryString(fields, "bindingId", out var bindingId)
                || !TryBindingKind(fields, "bindingKind", out var bindingKind)
                || !TryString(fields, "sourceNodeId", out var sourceNodeId)
                || !TryString(fields, "sourcePortId", out var sourcePortId)
                || !TryString(fields, "targetNodeId", out var targetNodeId)
                || !TryString(fields, "targetPortId", out var targetPortId)
                || !TryString(fields, "valueSchemaId", out var valueSchemaId)
                || !GovernedLoopTypedValue.TryDeserialize(fields["value"].GetRawText(), out var typedValue, out _))
            {
                return false;
            }

            var materialized = GovernedLoopTypedBindingValue.Create(graph, bindingId!, typedValue!);
            if (materialized.BindingKind != bindingKind
                || !string.Equals(materialized.SourceNodeId, sourceNodeId, StringComparison.Ordinal)
                || !string.Equals(materialized.SourcePortId, sourcePortId, StringComparison.Ordinal)
                || !string.Equals(materialized.TargetNodeId, targetNodeId, StringComparison.Ordinal)
                || !string.Equals(materialized.TargetPortId, targetPortId, StringComparison.Ordinal)
                || !string.Equals(materialized.ValueSchemaId, valueSchemaId, StringComparison.Ordinal))
            {
                return false;
            }

            values.Add(materialized);
        }

        inputs = values.ToArray();
        return true;
    }

    private static bool TryOutputs(JsonElement element, GovernedLoopGraphDefinition graph, [NotNullWhen(true)] out GovernedLoopTypedNodeOutput[]? outputs)
    {
        outputs = null;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > CustomLoopLimits.MaxGraphPortsPerNode)
        {
            return false;
        }

        var values = new List<GovernedLoopTypedNodeOutput>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryObject(item, ["schemaVersion", "nodeId", "portId", "bindingKind", "valueSchemaId", "value"], out var fields)
                || !TryInt32(fields!, "schemaVersion", out var schemaVersion)
                || schemaVersion != GovernedLoopTypedNodeOutput.CurrentSchemaVersion
                || !TryString(fields, "nodeId", out var nodeId)
                || !TryString(fields, "portId", out var portId)
                || !TryBindingKind(fields, "bindingKind", out var bindingKind)
                || !TryString(fields, "valueSchemaId", out var valueSchemaId)
                || !GovernedLoopTypedValue.TryDeserialize(fields["value"].GetRawText(), out var typedValue, out _))
            {
                return false;
            }

            var materialized = GovernedLoopTypedNodeOutput.Create(graph, nodeId!, portId!, typedValue!);
            if (materialized.BindingKind != bindingKind || !string.Equals(materialized.ValueSchemaId, valueSchemaId, StringComparison.Ordinal))
            {
                return false;
            }

            values.Add(materialized);
        }

        outputs = values.ToArray();
        return true;
    }

    private static bool TryValidationEvidence(JsonElement element, out GovernedLoopValidationEvidence? evidence)
    {
        evidence = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!TryObject(element, ["schemaVersion", "passed", "observations"], out var fields)
            || !TryInt32(fields!, "schemaVersion", out var schemaVersion)
            || schemaVersion != GovernedLoopValidationEvidence.CurrentSchemaVersion
            || !fields.TryGetValue("passed", out var passedElement)
            || passedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !fields.TryGetValue("observations", out var observationsElement)
            || observationsElement.ValueKind != JsonValueKind.Array
            || observationsElement.GetArrayLength() > CustomLoopLimits.MaxGraphPureNodeObservations)
        {
            return false;
        }

        var observations = new List<GovernedLoopValidationObservation>();
        foreach (var item in observationsElement.EnumerateArray())
        {
            if (!TryObject(item, ["code", "path"], out var observation)
                || !TryString(observation!, "code", out var code)
                || !TryString(observation, "path", out var path))
            {
                return false;
            }

            observations.Add(GovernedLoopValidationObservation.Create(code!, path!));
        }

        evidence = GovernedLoopValidationEvidence.Create(schemaVersion, passedElement.GetBoolean(), observations);
        return true;
    }

    private static bool TryObject(JsonElement element, IReadOnlyList<string> expectedNames, [NotNullWhen(true)] out Dictionary<string, JsonElement>? fields)
    {
        fields = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != expectedNames.Count || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
        {
            return false;
        }

        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
        if (properties.Any(property => !expected.Contains(property.Name)))
        {
            return false;
        }

        fields = properties.ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        return true;
    }

    private static bool TryString(IReadOnlyDictionary<string, JsonElement> fields, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!fields.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null;
    }

    private static bool TryInt32(IReadOnlyDictionary<string, JsonElement> fields, string name, out int value)
    {
        value = default;
        return fields.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryBindingKind(IReadOnlyDictionary<string, JsonElement> fields, string name, out GovernedLoopBindingKind kind)
    {
        kind = GovernedLoopBindingKind.Unknown;
        if (!TryString(fields, name, out var value))
        {
            return false;
        }

        kind = value switch
        {
            "data" => GovernedLoopBindingKind.Data,
            "context" => GovernedLoopBindingKind.Context,
            _ => GovernedLoopBindingKind.Unknown
        };
        return kind != GovernedLoopBindingKind.Unknown;
    }

    private static string ToCanonical(GovernedLoopBindingKind kind)
        => kind == GovernedLoopBindingKind.Data ? "data" : kind == GovernedLoopBindingKind.Context ? "context" : throw new ArgumentOutOfRangeException(nameof(kind));

    private static GovernedLoopPureNodeOutcomeValidationResult Valid() => new(Array.Empty<GovernedLoopPureNodeOutcomeError>());

    private static GovernedLoopPureNodeOutcomeValidationResult Invalid(string code, string path, string message)
        => new([GovernedLoopPureNodeOutcomeError.Create(code, path, message)]);
}
