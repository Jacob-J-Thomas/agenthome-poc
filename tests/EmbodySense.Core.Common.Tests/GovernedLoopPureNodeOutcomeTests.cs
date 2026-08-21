using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Common.Tests;

public sealed class GovernedLoopPureNodeOutcomeTests
{
    [Fact]
    public void Every_initial_operator_produces_strict_graph_bound_restart_evidence()
    {
        var cases = new[]
        {
            Transform(GovernedLoopPureNodeVocabulary.IdentityTransform, [(GovernedLoopPureNodeVocabulary.InputPort, "text", Value(GovernedLoopValueKind.Text, "\"same\""))], "text", Value(GovernedLoopValueKind.Text, "\"same\"")),
            Transform(GovernedLoopPureNodeVocabulary.StructuredSelect, [(GovernedLoopPureNodeVocabulary.InputPort, "object", Value(GovernedLoopValueKind.Object, "{\"selected\":\"yes\"}"))], "text", Value(GovernedLoopValueKind.Text, "\"yes\"")),
            Transform(GovernedLoopPureNodeVocabulary.OrderedTextConcat, [(GovernedLoopPureNodeVocabulary.ValuesPort, "array", Value(GovernedLoopValueKind.Array, "[\"a\",\"b\"]"))], "text", Value(GovernedLoopValueKind.Text, "\"a,b\"")),
            Validate(GovernedLoopPureNodeVocabulary.SchemaConformance, [(GovernedLoopPureNodeVocabulary.InputPort, "object", Value(GovernedLoopValueKind.Object, "{\"valid\":true}"))]),
            Validate(GovernedLoopPureNodeVocabulary.CanonicalEquality, [(GovernedLoopPureNodeVocabulary.LeftPort, "text", Value(GovernedLoopValueKind.Text, "\"same\"")), (GovernedLoopPureNodeVocabulary.RightPort, "text", Value(GovernedLoopValueKind.Text, "\"same\""))]),
            Validate(GovernedLoopPureNodeVocabulary.InclusiveIntegerRange, [(GovernedLoopPureNodeVocabulary.InputPort, "integer", Value(GovernedLoopValueKind.Integer, "2"))]),
            Validate(GovernedLoopPureNodeVocabulary.InclusiveNumberRange, [(GovernedLoopPureNodeVocabulary.InputPort, "number", Value(GovernedLoopValueKind.Number, "2.5"))]),
            Validate(GovernedLoopPureNodeVocabulary.TextLength, [(GovernedLoopPureNodeVocabulary.InputPort, "text", Value(GovernedLoopValueKind.Text, "\"abc\""))]),
            Validate(GovernedLoopPureNodeVocabulary.ArrayLength, [(GovernedLoopPureNodeVocabulary.InputPort, "array", Value(GovernedLoopValueKind.Array, "[\"a\"]"))])
        };

        foreach (var item in cases)
        {
            Assert.True(GovernedLoopPureNodeOutcome.TryCreate(item.Graph, "pure", item.Inputs, [item.Output], item.Evidence, out var outcome, out var validation), $"{item.Graph.Nodes.Single(node => node.Id == "pure").Descriptor.TypeId}: {string.Join(", ", validation.Errors.Select(error => error.Code))}");
            Assert.True(validation.IsValid);
            Assert.Equal(item.Graph.RevisionReference, outcome!.GraphRevision);
            Assert.Equal(item.Graph.Nodes.Single(node => node.Id == "pure").Descriptor, outcome.Descriptor);
            Assert.Equal(item.Inputs.OrderBy(value => value.BindingId).Select(value => value.BindingId), outcome.Inputs.Select(value => value.BindingId));
            Assert.Equal([item.Output.PortId], outcome.Outputs.Select(value => value.PortId));
            Assert.Equal(item.Evidence?.Passed, outcome.ValidationEvidence?.Passed);
            Assert.True(GovernedLoopPureNodeOutcomeHash.Matches(outcome, outcome.ContentHash));
            Assert.Equal(outcome.ContentHash, GovernedLoopPureNodeOutcomeHash.Compute(outcome));
            Assert.DoesNotContain("runId", outcome.CanonicalJson, StringComparison.Ordinal);
            Assert.DoesNotContain("attempt", outcome.CanonicalJson, StringComparison.Ordinal);

            Assert.True(GovernedLoopPureNodeOutcome.TryDeserialize(item.Graph, outcome.CanonicalJson, out var replayed, out var replayValidation));
            Assert.True(replayValidation.IsValid);
            Assert.Equal(outcome.CanonicalJson, replayed!.CanonicalJson);
            Assert.Equal(outcome.ContentHash, replayed.ContentHash);
        }
    }

    [Fact]
    public void Outcome_snapshots_are_sorted_immutable_and_hash_pinned()
    {
        var item = Validate(
            GovernedLoopPureNodeVocabulary.CanonicalEquality,
            [
                (GovernedLoopPureNodeVocabulary.LeftPort, "text", Value(GovernedLoopValueKind.Text, "\"same\"")),
                (GovernedLoopPureNodeVocabulary.RightPort, "text", Value(GovernedLoopValueKind.Text, "\"same\""))
            ]);
        var reversed = item.Inputs.Reverse().ToArray();
        Assert.True(GovernedLoopPureNodeOutcome.TryCreate(item.Graph, "pure", reversed, [item.Output], item.Evidence, out var outcome, out _));
        reversed[0] = reversed[1];

        Assert.Equal(["binding-left", "binding-right"], outcome!.Inputs.Select(value => value.BindingId));
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopTypedBindingValue>)outcome.Inputs).Add(item.Inputs[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopTypedNodeOutput>)outcome.Outputs).Add(item.Output));
        Assert.Equal("6f6a28438f48a0254b3d42524e20b6c3aaf7317c9b3ae8fca8a06605777ab3c1", outcome.ContentHash);
        Assert.False(GovernedLoopPureNodeOutcomeHash.Matches(outcome, new string('A', 64)));
        Assert.False(GovernedLoopPureNodeOutcomeHash.Matches(null, outcome.ContentHash));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopPureNodeOutcomeHash.Compute(null!));
    }

    [Fact]
    public void Strict_reader_rejects_noncanonical_tampered_substituted_and_oversized_artifacts()
    {
        var item = Transform(GovernedLoopPureNodeVocabulary.IdentityTransform, [(GovernedLoopPureNodeVocabulary.InputPort, "text", Value(GovernedLoopValueKind.Text, "\"same\""))], "text", Value(GovernedLoopValueKind.Text, "\"same\""));
        Assert.True(GovernedLoopPureNodeOutcome.TryCreate(item.Graph, "pure", item.Inputs, [item.Output], null, out var outcome, out _));
        var json = outcome!.CanonicalJson;

        AssertReadCode(item.Graph, "\n" + json, "pure-outcome.document-noncanonical");
        AssertReadCode(item.Graph, json.Replace(outcome.ContentHash, new string('0', 64), StringComparison.Ordinal), "pure-outcome.hash-mismatch");
        AssertReadCode(item.Graph, json.Replace("identity-transform", "structured-select", StringComparison.Ordinal), "pure-outcome.descriptor-substituted");
        AssertReadCode(item.Graph, json.Replace("\"sourceNodeId\":\"trigger\"", "\"sourceNodeId\":\"other\"", StringComparison.Ordinal), "pure-outcome.document-shape");
        AssertReadCode(item.Graph, json.Replace(item.Graph.GraphId, "other-graph", StringComparison.Ordinal), "pure-outcome.document-shape");
        AssertReadCode(item.Graph, json.Insert(1, "\"extra\":true,"), "pure-outcome.document-shape");
        AssertReadCode(item.Graph, ReplaceLast(json, "\"same\"", "\"different\""), "pure-outcome.semantic-mismatch");
        AssertReadCode(item.Graph, "{", "pure-outcome.document-malformed");
        AssertReadCode(item.Graph, new string('x', CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes + 1), "pure-outcome.document-invalid");
        AssertReadCode(null, json, "pure-outcome.graph-required");
    }

    [Fact]
    public void Strict_reader_rejects_rehashed_semantic_substitution_for_select_concat_and_validator_evidence()
    {
        var selected = Transform(
            GovernedLoopPureNodeVocabulary.StructuredSelect,
            [(GovernedLoopPureNodeVocabulary.InputPort, "object", Value(GovernedLoopValueKind.Object, "{\"selected\":\"yes\"}"))],
            "text",
            Value(GovernedLoopValueKind.Text, "\"yes\""));
        Assert.True(GovernedLoopPureNodeOutcome.TryCreate(selected.Graph, "pure", selected.Inputs, [selected.Output], null, out var selectedOutcome, out _));
        var wrongSelection = Value(GovernedLoopValueKind.Text, "\"no\"");
        var substitutedSelection = Rehash(ReplaceLast(selectedOutcome!.CanonicalJson, selected.Output.Value.CanonicalJson, wrongSelection.CanonicalJson));
        AssertReadCode(selected.Graph, substitutedSelection, "pure-outcome.semantic-mismatch");

        var concatenated = Transform(
            GovernedLoopPureNodeVocabulary.OrderedTextConcat,
            [(GovernedLoopPureNodeVocabulary.ValuesPort, "array", Value(GovernedLoopValueKind.Array, "[\"a\",\"b\"]"))],
            "text",
            Value(GovernedLoopValueKind.Text, "\"a,b\""));
        Assert.True(GovernedLoopPureNodeOutcome.TryCreate(concatenated.Graph, "pure", concatenated.Inputs, [concatenated.Output], null, out var concatenatedOutcome, out _));
        var wrongConcat = Value(GovernedLoopValueKind.Text, "\"b,a\"");
        var substitutedConcat = Rehash(ReplaceLast(concatenatedOutcome!.CanonicalJson, concatenated.Output.Value.CanonicalJson, wrongConcat.CanonicalJson));
        AssertReadCode(concatenated.Graph, substitutedConcat, "pure-outcome.semantic-mismatch");

        var equality = Validate(
            GovernedLoopPureNodeVocabulary.CanonicalEquality,
            [
                (GovernedLoopPureNodeVocabulary.LeftPort, "text", Value(GovernedLoopValueKind.Text, "\"same\"")),
                (GovernedLoopPureNodeVocabulary.RightPort, "text", Value(GovernedLoopValueKind.Text, "\"same\""))
            ]);
        Assert.True(GovernedLoopPureNodeOutcome.TryCreate(equality.Graph, "pure", equality.Inputs, [equality.Output], equality.Evidence, out var equalityOutcome, out _));
        var falseValue = Value(GovernedLoopValueKind.Boolean, "false");
        var substitutedValidator = ReplaceLast(equalityOutcome!.CanonicalJson, equality.Output.Value.CanonicalJson, falseValue.CanonicalJson)
            .Replace(
                "\"passed\":true,\"observations\":[]",
                "\"passed\":false,\"observations\":[{\"code\":\"canonical-values-differ\",\"path\":\"\"}]",
                StringComparison.Ordinal);
        AssertReadCode(equality.Graph, Rehash(substitutedValidator), "pure-outcome.semantic-mismatch");
    }

    [Fact]
    public void Evaluator_returns_false_validator_results_as_successful_effect_free_execution()
    {
        var equality = Validate(
            GovernedLoopPureNodeVocabulary.CanonicalEquality,
            [
                (GovernedLoopPureNodeVocabulary.LeftPort, "text", Value(GovernedLoopValueKind.Text, "\"left\"")),
                (GovernedLoopPureNodeVocabulary.RightPort, "text", Value(GovernedLoopValueKind.Text, "\"right\""))
            ]);
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(equality.Graph, "pure", equality.Inputs, out var equalityOutput, out var equalityEvidence, out var equalityValidation));
        Assert.True(equalityValidation.IsValid);
        Assert.Equal("false", equalityOutput.Value.CanonicalValueJson);
        Assert.False(equalityEvidence!.Passed);
        Assert.Equal("canonical-values-differ", Assert.Single(equalityEvidence.Observations).Code);

        var integerRange = Validate(
            GovernedLoopPureNodeVocabulary.InclusiveIntegerRange,
            [(GovernedLoopPureNodeVocabulary.InputPort, "integer", Value(GovernedLoopValueKind.Integer, "9"))]);
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(integerRange.Graph, "pure", integerRange.Inputs, out var integerOutput, out var integerEvidence, out _));
        Assert.Equal("false", integerOutput.Value.CanonicalValueJson);
        Assert.Equal("integer-outside-range", Assert.Single(integerEvidence!.Observations).Code);

        var numberRange = Validate(
            GovernedLoopPureNodeVocabulary.InclusiveNumberRange,
            [(GovernedLoopPureNodeVocabulary.InputPort, "number", Value(GovernedLoopValueKind.Number, "9.5"))]);
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(numberRange.Graph, "pure", numberRange.Inputs, out _, out var numberEvidence, out _));
        Assert.Equal("number-outside-range", Assert.Single(numberEvidence!.Observations).Code);

        var textLength = Validate(
            GovernedLoopPureNodeVocabulary.TextLength,
            [(GovernedLoopPureNodeVocabulary.InputPort, "text", Value(GovernedLoopValueKind.Text, "\"abcdef\""))]);
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(textLength.Graph, "pure", textLength.Inputs, out _, out var textEvidence, out _));
        Assert.Equal("text-length-outside-range", Assert.Single(textEvidence!.Observations).Code);

        var arrayLength = Validate(
            GovernedLoopPureNodeVocabulary.ArrayLength,
            [(GovernedLoopPureNodeVocabulary.InputPort, "array", Value(GovernedLoopValueKind.Array, "[\"a\",\"b\",\"c\"]"))]);
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(arrayLength.Graph, "pure", arrayLength.Inputs, out _, out var arrayEvidence, out _));
        Assert.Equal("array-length-outside-range", Assert.Single(arrayEvidence!.Observations).Code);
    }

    [Fact]
    public void Schema_conformance_attests_admitted_structure_and_rejects_unimplemented_formats()
    {
        var structural = Validate(
            GovernedLoopPureNodeVocabulary.SchemaConformance,
            [(GovernedLoopPureNodeVocabulary.InputPort, "array", Value(GovernedLoopValueKind.Array, "[\"admitted\"]"))]);
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(structural.Graph, "pure", structural.Inputs, out var output, out var evidence, out _));
        Assert.Equal("true", output.Value.CanonicalValueJson);
        Assert.True(evidence!.Passed);
        Assert.Empty(evidence.Observations);

        var formattedSchemas = new[]
        {
            new GovernedLoopValueSchemaDefinition("boolean", GovernedLoopValueKind.Boolean, false),
            new GovernedLoopValueSchemaDefinition("formatted", GovernedLoopValueKind.Text, false, Format: "uri"),
            new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)
        };
        var formatted = PureGraph(
            GovernedLoopNodeKind.Validate,
            GovernedLoopPureNodeVocabulary.SchemaConformance,
            [(GovernedLoopPureNodeVocabulary.InputPort, "formatted")],
            GovernedLoopPureNodeVocabulary.ResultPort,
            "boolean",
            valueSchemas: formattedSchemas);
        var formattedInput = GovernedLoopTypedBindingValue.Create(formatted, "binding-input", Value(GovernedLoopValueKind.Text, "\"https://example.test\""));
        AssertEvaluateCode(formatted, [formattedInput], "pure-node.schema-unsupported");
    }

    [Fact]
    public void Structured_selection_honors_rfc6901_escaping_array_indexes_and_missing_paths()
    {
        var escaped = Transform(
            GovernedLoopPureNodeVocabulary.StructuredSelect,
            [(GovernedLoopPureNodeVocabulary.InputPort, "object", Value(GovernedLoopValueKind.Object, "{\"a/b\":{\"~key\":\"found\"}}"))],
            "text",
            Value(GovernedLoopValueKind.Text, "\"found\""),
            new Dictionary<string, string> { [GovernedLoopPureNodeVocabulary.PointerParameter] = "/a~1b/~0key" });
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(escaped.Graph, "pure", escaped.Inputs, out var escapedOutput, out var transformEvidence, out _));
        Assert.Equal("\"found\"", escapedOutput.Value.CanonicalValueJson);
        Assert.Null(transformEvidence);

        var indexed = Transform(
            GovernedLoopPureNodeVocabulary.StructuredSelect,
            [(GovernedLoopPureNodeVocabulary.InputPort, "array", Value(GovernedLoopValueKind.Array, "[\"zero\",\"one\"]"))],
            "text",
            Value(GovernedLoopValueKind.Text, "\"one\""),
            new Dictionary<string, string> { [GovernedLoopPureNodeVocabulary.PointerParameter] = "/1" });
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(indexed.Graph, "pure", indexed.Inputs, out var indexedOutput, out _, out _));
        Assert.Equal("\"one\"", indexedOutput.Value.CanonicalValueJson);

        var missing = Transform(
            GovernedLoopPureNodeVocabulary.StructuredSelect,
            [(GovernedLoopPureNodeVocabulary.InputPort, "object", Value(GovernedLoopValueKind.Object, "{\"present\":true}"))],
            "boolean",
            Value(GovernedLoopValueKind.Boolean, "true"),
            new Dictionary<string, string> { [GovernedLoopPureNodeVocabulary.PointerParameter] = "/missing" });
        AssertEvaluateCode(missing.Graph, missing.Inputs, "pure-node.selection-missing");

        var malformed = PureGraph(
            GovernedLoopNodeKind.Transform,
            GovernedLoopPureNodeVocabulary.StructuredSelect,
            [(GovernedLoopPureNodeVocabulary.InputPort, "object")],
            GovernedLoopPureNodeVocabulary.OutputPort,
            "text",
            new Dictionary<string, string> { [GovernedLoopPureNodeVocabulary.PointerParameter] = "not-a-pointer" });
        var malformedInput = GovernedLoopTypedBindingValue.Create(malformed, "binding-input", Value(GovernedLoopValueKind.Object, "{}"));
        AssertEvaluateCode(malformed, [malformedInput], "pure-node.contract-invalid");
    }

    [Fact]
    public void Materialization_recursively_enforces_array_schemas_and_rejects_cycles()
    {
        var arrayIdentity = PureGraph(
            GovernedLoopNodeKind.Transform,
            GovernedLoopPureNodeVocabulary.IdentityTransform,
            [(GovernedLoopPureNodeVocabulary.InputPort, "array")],
            GovernedLoopPureNodeVocabulary.OutputPort,
            "array");
        var valid = Value(GovernedLoopValueKind.Array, "[\"a\",\"b\"]");
        var invalid = Value(GovernedLoopValueKind.Array, "[1,true,null]");
        Assert.Equal(valid, GovernedLoopTypedBindingValue.Create(arrayIdentity, "binding-input", valid).Value);
        Assert.Equal(valid, GovernedLoopTypedNodeOutput.Create(arrayIdentity, "pure", GovernedLoopPureNodeVocabulary.OutputPort, valid).Value);
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedBindingValue.Create(arrayIdentity, "binding-input", invalid));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedNodeOutput.Create(arrayIdentity, "pure", GovernedLoopPureNodeVocabulary.OutputPort, invalid));

        var cyclicSchemas = new[]
        {
            new GovernedLoopValueSchemaDefinition("cycle", GovernedLoopValueKind.Array, false, ElementSchemaId: "cycle"),
            new GovernedLoopValueSchemaDefinition("boolean", GovernedLoopValueKind.Boolean, false),
            new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)
        };
        var cyclic = PureGraph(
            GovernedLoopNodeKind.Transform,
            GovernedLoopPureNodeVocabulary.IdentityTransform,
            [(GovernedLoopPureNodeVocabulary.InputPort, "cycle")],
            GovernedLoopPureNodeVocabulary.OutputPort,
            "cycle",
            valueSchemas: cyclicSchemas);
        var emptyArray = Value(GovernedLoopValueKind.Array, "[]");
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedBindingValue.Create(cyclic, "binding-input", emptyArray));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedNodeOutput.Create(cyclic, "pure", GovernedLoopPureNodeVocabulary.OutputPort, emptyArray));
    }

    [Fact]
    public void Evaluator_rejects_invalid_configuration_inputs_authority_and_oversized_output()
    {
        AssertEvaluateCode(null, [], "pure-node.graph-required");

        var identity = Transform(GovernedLoopPureNodeVocabulary.IdentityTransform, [(GovernedLoopPureNodeVocabulary.InputPort, "text", Value(GovernedLoopValueKind.Text, "\"same\""))], "text", Value(GovernedLoopValueKind.Text, "\"same\""));
        Assert.False(GovernedLoopPureNodeEvaluator.TryEvaluate(identity.Graph, "INVALID", identity.Inputs, out _, out _, out var invalidId));
        Assert.Equal("pure-node.node-invalid", Assert.Single(invalidId.Errors).Code);
        Assert.False(GovernedLoopPureNodeEvaluator.TryEvaluate(identity.Graph, "missing", identity.Inputs, out _, out _, out var missing));
        Assert.Equal("pure-node.node-missing", Assert.Single(missing.Errors).Code);
        AssertEvaluateCode(identity.Graph, null, "pure-node.inputs-required");
        AssertEvaluateCode(identity.Graph, ThrowingInputs(identity.Inputs[0]), "pure-node.inputs-invalid");
        AssertEvaluateCode(identity.Graph, Enumerable.Repeat(identity.Inputs[0], CustomLoopLimits.MaxGraphPortsPerNode + 1), "pure-node.inputs-invalid");
        AssertEvaluateCode(identity.Graph, [], "pure-node.inputs-inexact");

        var authorized = PureGraph(GovernedLoopNodeKind.Transform, GovernedLoopPureNodeVocabulary.IdentityTransform, [(GovernedLoopPureNodeVocabulary.InputPort, "text")], GovernedLoopPureNodeVocabulary.OutputPort, "text", grantPureAuthority: true);
        var authorizedInput = GovernedLoopTypedBindingValue.Create(authorized, "binding-input", Value(GovernedLoopValueKind.Text, "\"same\""));
        AssertEvaluateCode(authorized, [authorizedInput], "pure-node.authority-invalid");

        var invalidParameters = PureGraph(
            GovernedLoopNodeKind.Transform,
            GovernedLoopPureNodeVocabulary.IdentityTransform,
            [(GovernedLoopPureNodeVocabulary.InputPort, "text")],
            GovernedLoopPureNodeVocabulary.OutputPort,
            "text",
            new Dictionary<string, string> { [GovernedLoopPureNodeVocabulary.SeparatorParameter] = "," });
        var invalidParameterInput = GovernedLoopTypedBindingValue.Create(invalidParameters, "binding-input", Value(GovernedLoopValueKind.Text, "\"same\""));
        AssertEvaluateCode(invalidParameters, [invalidParameterInput], "pure-node.contract-invalid");

        var longValue = new string('x', CustomLoopLimits.MaxGraphTypedValueStringCharacters / 2 + 1);
        var oversizedConcat = Transform(
            GovernedLoopPureNodeVocabulary.OrderedTextConcat,
            [(GovernedLoopPureNodeVocabulary.ValuesPort, "array", Value(GovernedLoopValueKind.Array, JsonSerializer.Serialize(new[] { longValue, longValue })))],
            "text",
            Value(GovernedLoopValueKind.Text, "\"unused\""));
        AssertEvaluateCode(oversizedConcat.Graph, oversizedConcat.Inputs, "pure-node.output-size-exceeded");
    }

    [Fact]
    public void Creation_rejects_inexact_inputs_outputs_authority_channels_and_result_evidence()
    {
        var identity = Transform(GovernedLoopPureNodeVocabulary.IdentityTransform, [(GovernedLoopPureNodeVocabulary.InputPort, "text", Value(GovernedLoopValueKind.Text, "\"same\""))], "text", Value(GovernedLoopValueKind.Text, "\"same\""));
        var differentOutput = GovernedLoopTypedNodeOutput.Create(identity.Graph, "pure", GovernedLoopPureNodeVocabulary.OutputPort, Value(GovernedLoopValueKind.Text, "\"different\""));
        AssertCreateCode(identity.Graph, identity.Inputs, [differentOutput], null, "pure-outcome.semantic-mismatch");
        AssertCreateCode(identity.Graph, [], [identity.Output], null, "pure-outcome.inputs-inexact");
        AssertCreateCode(identity.Graph, [identity.Inputs[0], identity.Inputs[0]], [identity.Output], null, "pure-outcome.inputs-inexact");
        AssertCreateCode(identity.Graph, identity.Inputs, [identity.Output, identity.Output], null, "pure-outcome.outputs-inexact");
        AssertCreateCode(identity.Graph, identity.Inputs, [identity.Output], GovernedLoopValidationEvidence.Create(1, true, []), "pure-outcome.semantic-mismatch");
        AssertCreateCode(identity.Graph, Enumerable.Repeat(identity.Inputs[0], CustomLoopLimits.MaxGraphPortsPerNode + 1), [identity.Output], null, "pure-outcome.collection-invalid");
        AssertCreateCode(identity.Graph, ThrowingInputs(identity.Inputs[0]), [identity.Output], null, "pure-outcome.collection-invalid");
        AssertCreateCode(identity.Graph, null, [identity.Output], null, "pure-outcome.collection-required");
        AssertCreateCode(null, identity.Inputs, [identity.Output], null, "pure-outcome.graph-required");

        var otherGraph = PureGraph(GovernedLoopNodeKind.Transform, GovernedLoopPureNodeVocabulary.IdentityTransform, [(GovernedLoopPureNodeVocabulary.InputPort, "text")], GovernedLoopPureNodeVocabulary.OutputPort, "text", revisionId: "revision-other");
        var foreignInput = GovernedLoopTypedBindingValue.Create(otherGraph, "binding-input", Value(GovernedLoopValueKind.Text, "\"same\""));
        AssertCreateCode(identity.Graph, [foreignInput], [identity.Output], null, "pure-outcome.input-substituted");

        var authorizedGraph = PureGraph(GovernedLoopNodeKind.Transform, GovernedLoopPureNodeVocabulary.IdentityTransform, [(GovernedLoopPureNodeVocabulary.InputPort, "text")], GovernedLoopPureNodeVocabulary.OutputPort, "text", grantPureAuthority: true);
        var authorizedInput = GovernedLoopTypedBindingValue.Create(authorizedGraph, "binding-input", Value(GovernedLoopValueKind.Text, "\"same\""));
        var authorizedOutput = GovernedLoopTypedNodeOutput.Create(authorizedGraph, "pure", GovernedLoopPureNodeVocabulary.OutputPort, Value(GovernedLoopValueKind.Text, "\"same\""));
        AssertCreateCode(authorizedGraph, [authorizedInput], [authorizedOutput], null, "pure-outcome.authority-invalid");

        var contextGraph = PureGraph(GovernedLoopNodeKind.Transform, GovernedLoopPureNodeVocabulary.IdentityTransform, [(GovernedLoopPureNodeVocabulary.InputPort, "text")], GovernedLoopPureNodeVocabulary.OutputPort, "text", contextInput: true);
        var contextInput = GovernedLoopTypedBindingValue.Create(contextGraph, "binding-input", Value(GovernedLoopValueKind.Text, "\"same\""));
        var contextOutput = GovernedLoopTypedNodeOutput.Create(contextGraph, "pure", GovernedLoopPureNodeVocabulary.OutputPort, Value(GovernedLoopValueKind.Text, "\"same\""));
        AssertCreateCode(contextGraph, [contextInput], [contextOutput], null, "pure-outcome.input-substituted");

        var validator = Validate(GovernedLoopPureNodeVocabulary.TextLength, [(GovernedLoopPureNodeVocabulary.InputPort, "text", Value(GovernedLoopValueKind.Text, "\"abc\""))]);
        AssertCreateCode(validator.Graph, validator.Inputs, [validator.Output], null, "pure-outcome.semantic-mismatch");
        AssertCreateCode(validator.Graph, validator.Inputs, [validator.Output], GovernedLoopValidationEvidence.Create(1, false, [GovernedLoopValidationObservation.Create("text-length-outside-range", "")]), "pure-outcome.semantic-mismatch");
        Assert.False(GovernedLoopPureNodeOutcome.TryCreate(identity.Graph, "missing", identity.Inputs, [identity.Output], null, out _, out var missing));
        Assert.Equal("pure-outcome.node-missing", Assert.Single(missing.Errors).Code);
    }

    [Fact]
    public void Outcome_error_contract_is_bounded_and_result_errors_are_immutable()
    {
        var error = GovernedLoopPureNodeOutcomeError.Create("pure-outcome-invalid", "$", "The outcome is invalid.");
        Assert.Throws<ArgumentException>(() => GovernedLoopPureNodeOutcomeError.Create("INVALID", "$", "The outcome is invalid."));
        Assert.Throws<ArgumentException>(() => GovernedLoopPureNodeOutcomeError.Create("valid", string.Empty, "The outcome is invalid."));
        Assert.Throws<ArgumentException>(() => GovernedLoopPureNodeOutcomeError.Create("valid", new string('p', CustomLoopLimits.MaxGraphValidationErrorPathCharacters + 1), "The outcome is invalid."));
        Assert.Throws<ArgumentException>(() => GovernedLoopPureNodeOutcomeError.Create("valid", "$", string.Empty));
        Assert.Throws<ArgumentException>(() => GovernedLoopPureNodeOutcomeError.Create("valid", "$", new string('m', CustomLoopLimits.MaxGraphValidationErrorMessageCharacters + 1)));

        Assert.False(GovernedLoopPureNodeOutcome.TryCreate(null, "pure", [], [], null, out _, out var validation));
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopPureNodeOutcomeError>)validation.Errors).Add(error));
    }

    private static OutcomeCase Transform(
        string typeId,
        (string PortId, string SchemaId, GovernedLoopTypedValue Value)[] inputValues,
        string outputSchemaId,
        GovernedLoopTypedValue outputValue,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var graph = PureGraph(GovernedLoopNodeKind.Transform, typeId, inputValues.Select(value => (value.PortId, value.SchemaId)).ToArray(), GovernedLoopPureNodeVocabulary.OutputPort, outputSchemaId, parameters);
        return Case(graph, inputValues, GovernedLoopPureNodeVocabulary.OutputPort, outputValue, null);
    }

    private static OutcomeCase Validate(string typeId, (string PortId, string SchemaId, GovernedLoopTypedValue Value)[] inputValues)
    {
        var graph = PureGraph(GovernedLoopNodeKind.Validate, typeId, inputValues.Select(value => (value.PortId, value.SchemaId)).ToArray(), GovernedLoopPureNodeVocabulary.ResultPort, "boolean");
        return Case(graph, inputValues, GovernedLoopPureNodeVocabulary.ResultPort, Value(GovernedLoopValueKind.Boolean, "true"), GovernedLoopValidationEvidence.Create(1, true, []));
    }

    private static OutcomeCase Case(GovernedLoopGraphDefinition graph, (string PortId, string SchemaId, GovernedLoopTypedValue Value)[] inputValues, string outputPort, GovernedLoopTypedValue outputValue, GovernedLoopValidationEvidence? evidence)
    {
        var valueByPort = inputValues.ToDictionary(value => value.PortId, value => value.Value, StringComparer.Ordinal);
        var inputs = graph.Bindings.Where(binding => binding.ToNodeId == "pure").Select(binding => GovernedLoopTypedBindingValue.Create(graph, binding.Id, valueByPort[binding.ToPortId])).ToArray();
        var output = GovernedLoopTypedNodeOutput.Create(graph, "pure", outputPort, outputValue);
        return new OutcomeCase(graph, inputs, output, evidence);
    }

    private static GovernedLoopGraphDefinition PureGraph(
        GovernedLoopNodeKind kind,
        string typeId,
        (string PortId, string SchemaId)[] inputPorts,
        string outputPortId,
        string outputSchemaId,
        IReadOnlyDictionary<string, string>? parameters = null,
        string revisionId = "revision-1",
        bool grantPureAuthority = false,
        bool contextInput = false,
        IReadOnlyList<GovernedLoopValueSchemaDefinition>? valueSchemas = null)
    {
        const string Capability = "org.embodysense/model-inference";
        var loopAuthority = grantPureAuthority ? GovernedLoopAuthorityCeiling.Create([Capability]) : GovernedLoopAuthorityCeiling.Create([]);
        var nodeAuthority = grantPureAuthority ? GovernedLoopAuthorityCeiling.Create([Capability]) : GovernedLoopAuthorityCeiling.Create([]);
        var inputKind = contextInput ? GovernedLoopBindingKind.Context : GovernedLoopBindingKind.Data;
        var schemas = valueSchemas?.ToArray() ??
        [
            new GovernedLoopValueSchemaDefinition("array", GovernedLoopValueKind.Array, false, ElementSchemaId: "text"),
            new GovernedLoopValueSchemaDefinition("boolean", GovernedLoopValueKind.Boolean, false),
            new GovernedLoopValueSchemaDefinition("integer", GovernedLoopValueKind.Integer, false),
            new GovernedLoopValueSchemaDefinition("number", GovernedLoopValueKind.Number, false),
            new GovernedLoopValueSchemaDefinition("object", GovernedLoopValueKind.Object, false),
            new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)
        ];
        var triggerPorts = inputPorts.Select(port => new GovernedLoopPortDefinition(port.PortId, GovernedLoopPortDirection.Output, inputKind, port.SchemaId, true)).ToArray();
        var purePorts = inputPorts.Select(port => new GovernedLoopPortDefinition(port.PortId, GovernedLoopPortDirection.Input, inputKind, port.SchemaId, true))
            .Append(new GovernedLoopPortDefinition(outputPortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, outputSchemaId, true)).ToArray();
        var nodes = new[]
        {
            new GovernedLoopNodeDefinition("trigger", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1), triggerPorts, GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition("pure", new GovernedLoopNodeDescriptor(kind, typeId, 1), purePorts, nodeAuthority, parameters ?? OperatorParameters(typeId)),
            new GovernedLoopNodeDefinition("exit", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
            [
                new GovernedLoopPortDefinition("terminal-input", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, outputSchemaId, true),
                new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, outputSchemaId, true)
            ], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>())
        };
        var bindings = inputPorts.Select(port => new GovernedLoopBindingDefinition($"binding-{port.PortId}", inputKind, "trigger", port.PortId, "pure", port.PortId))
            .Append(new GovernedLoopBindingDefinition("pure-to-exit", GovernedLoopBindingKind.Data, "pure", outputPortId, "exit", "terminal-input")).ToArray();
        return GovernedLoopGraphDefinition.Create(
            1,
            "pure-loop",
            revisionId,
            "Execute one deterministic pure node.",
            GovernedLoopGraphTestFixture.Role(),
            "trigger",
            ["exit"],
            loopAuthority,
            schemas,
            nodes,
            [new GovernedLoopControlEdgeDefinition("trigger-to-pure", "trigger", "pure", GovernedLoopControlCondition.Always), new GovernedLoopControlEdgeDefinition("pure-to-exit", "pure", "exit", GovernedLoopControlCondition.Success)],
            bindings,
            new GovernedLoopOutputContract("Return the pure-node result.", [new GovernedLoopOutputDefinition("result", outputSchemaId, "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata("Pure loop", "One deterministic node.",
            [
                new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Admit exact inputs."),
                new GovernedLoopNodeDisplayMetadata("pure", "Pure", "Execute deterministically."),
                new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Return the result.")
            ]),
            GovernedLoopGraphTestFixture.DefaultModelRoutingPolicy());
    }

    private static IReadOnlyDictionary<string, string> OperatorParameters(string typeId)
        => typeId switch
        {
            GovernedLoopPureNodeVocabulary.StructuredSelect => new Dictionary<string, string>
            {
                [GovernedLoopPureNodeVocabulary.PointerParameter] = "/selected"
            },
            GovernedLoopPureNodeVocabulary.OrderedTextConcat => new Dictionary<string, string>
            {
                [GovernedLoopPureNodeVocabulary.SeparatorParameter] = ","
            },
            GovernedLoopPureNodeVocabulary.InclusiveIntegerRange => Bounds("1", "3"),
            GovernedLoopPureNodeVocabulary.InclusiveNumberRange => Bounds("1.5", "3.5"),
            GovernedLoopPureNodeVocabulary.TextLength => Bounds("1", "5"),
            GovernedLoopPureNodeVocabulary.ArrayLength => Bounds("0", "2"),
            _ => new Dictionary<string, string>()
        };

    private static IReadOnlyDictionary<string, string> Bounds(string minimum, string maximum)
        => new Dictionary<string, string>
        {
            [GovernedLoopPureNodeVocabulary.MinimumParameter] = minimum,
            [GovernedLoopPureNodeVocabulary.MaximumParameter] = maximum
        };

    private static GovernedLoopTypedValue Value(GovernedLoopValueKind kind, string json)
    {
        Assert.True(GovernedLoopTypedValue.TryCreate(1, kind, json, out var value, out var validation));
        Assert.True(validation.IsValid);
        return value!;
    }

    private static void AssertCreateCode(GovernedLoopGraphDefinition? graph, IEnumerable<GovernedLoopTypedBindingValue>? inputs, IEnumerable<GovernedLoopTypedNodeOutput>? outputs, GovernedLoopValidationEvidence? evidence, string code)
    {
        Assert.False(GovernedLoopPureNodeOutcome.TryCreate(graph, "pure", inputs, outputs, evidence, out _, out var validation));
        Assert.Equal(code, Assert.Single(validation.Errors).Code);
    }

    private static void AssertReadCode(GovernedLoopGraphDefinition? graph, string json, string code)
    {
        Assert.False(GovernedLoopPureNodeOutcome.TryDeserialize(graph, json, out _, out var validation));
        Assert.Equal(code, Assert.Single(validation.Errors).Code);
    }

    private static void AssertEvaluateCode(GovernedLoopGraphDefinition? graph, IEnumerable<GovernedLoopTypedBindingValue>? inputs, string code)
    {
        Assert.False(GovernedLoopPureNodeEvaluator.TryEvaluate(graph, "pure", inputs, out _, out _, out var validation));
        Assert.Equal(code, Assert.Single(validation.Errors).Code);
    }

    private static string ReplaceLast(string value, string oldValue, string newValue)
    {
        var index = value.LastIndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return string.Concat(value.AsSpan(0, index), newValue, value.AsSpan(index + oldValue.Length));
    }

    private static string Rehash(string canonicalJson)
    {
        const string Marker = ",\"contentHash\":\"";
        var markerIndex = canonicalJson.LastIndexOf(Marker, StringComparison.Ordinal);
        Assert.True(markerIndex > 0);
        var payload = canonicalJson[..markerIndex] + "}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return string.Concat(canonicalJson.AsSpan(0, markerIndex), Marker, hash, "\"}");
    }

    private static IEnumerable<GovernedLoopTypedBindingValue> ThrowingInputs(GovernedLoopTypedBindingValue value)
    {
        yield return value;
        throw new IOException("Injected enumeration failure.");
    }

    private sealed record OutcomeCase(GovernedLoopGraphDefinition Graph, GovernedLoopTypedBindingValue[] Inputs, GovernedLoopTypedNodeOutput Output, GovernedLoopValidationEvidence? Evidence);
}
