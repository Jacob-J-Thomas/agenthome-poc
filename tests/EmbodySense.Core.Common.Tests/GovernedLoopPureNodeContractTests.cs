using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Common.Tests;

public sealed class GovernedLoopPureNodeContractTests
{
    [Fact]
    public void Initial_operator_vocabulary_is_closed_exact_and_kind_bounded()
    {
        Assert.Equal(
        [
            "array-length",
            "canonical-equality",
            "identity-transform",
            "inclusive-integer-range",
            "inclusive-number-range",
            "ordered-text-concat",
            "schema-conformance",
            "structured-select",
            "text-length"
        ], GovernedLoopPureNodeVocabulary.DescriptorTypeIds);
        Assert.True(GovernedLoopPureNodeVocabulary.IsTransform("identity-transform"));
        Assert.True(GovernedLoopPureNodeVocabulary.IsTransform("structured-select"));
        Assert.True(GovernedLoopPureNodeVocabulary.IsTransform("ordered-text-concat"));
        Assert.False(GovernedLoopPureNodeVocabulary.IsTransform("Identity-Transform"));
        Assert.True(GovernedLoopPureNodeVocabulary.IsValidate("schema-conformance"));
        Assert.True(GovernedLoopPureNodeVocabulary.IsValidate("array-length"));
        Assert.False(GovernedLoopPureNodeVocabulary.IsValidate("custom-validator"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)GovernedLoopPureNodeVocabulary.DescriptorTypeIds).Add("custom-validator"));

        var pureKinds = GovernedLoopPureNodeVocabulary.PureValueKinds();
        Assert.Equal(
        [
            GovernedLoopValueKind.Text,
            GovernedLoopValueKind.Boolean,
            GovernedLoopValueKind.Integer,
            GovernedLoopValueKind.Number,
            GovernedLoopValueKind.Object,
            GovernedLoopValueKind.Array
        ], pureKinds.Kinds);
        Assert.False(pureKinds.Contains(GovernedLoopValueKind.Binary));
    }

    [Fact]
    public void Kind_sets_are_sorted_immutable_value_objects_and_reject_ambiguity()
    {
        var source = new[] { GovernedLoopValueKind.Array, GovernedLoopValueKind.Text };
        var actual = GovernedLoopValueKindSet.Create(source);
        source[0] = GovernedLoopValueKind.Binary;

        Assert.Equal([GovernedLoopValueKind.Text, GovernedLoopValueKind.Array], actual.Kinds);
        Assert.Equal(actual, GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Text, GovernedLoopValueKind.Array]));
        Assert.Equal(actual.GetHashCode(), GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Array, GovernedLoopValueKind.Text]).GetHashCode());
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopValueKind>)actual.Kinds).Add(GovernedLoopValueKind.Number));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopValueKindSet.Create(null!));
        Assert.Throws<ArgumentException>(() => GovernedLoopValueKindSet.Create([]));
        Assert.Throws<ArgumentException>(() => GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Text, GovernedLoopValueKind.Text]));
        Assert.Throws<ArgumentException>(() => GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Unknown]));
        Assert.Throws<ArgumentException>(() => GovernedLoopValueKindSet.Create([(GovernedLoopValueKind)99]));
    }

    [Fact]
    public void Value_kind_wire_vocabulary_has_no_case_aliases_or_numeric_fallback()
    {
        foreach (var kind in Enum.GetValues<GovernedLoopValueKind>().Where(value => value != GovernedLoopValueKind.Unknown))
        {
            var canonical = GovernedLoopValueKindVocabulary.ToCanonical(kind);
            Assert.True(GovernedLoopValueKindVocabulary.TryParse(canonical, out var parsed));
            Assert.Equal(kind, parsed);
            Assert.False(GovernedLoopValueKindVocabulary.TryParse(canonical.ToUpperInvariant(), out _));
        }

        Assert.False(GovernedLoopValueKindVocabulary.TryParse("1", out var numeric));
        Assert.Equal(GovernedLoopValueKind.Unknown, numeric);
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopValueKindVocabulary.ToCanonical(GovernedLoopValueKind.Unknown));
    }

    [Fact]
    public void Materialized_binding_and_output_are_exact_graph_revision_pinned_typed_witnesses()
    {
        var graph = GovernedLoopGraphTestFixture.Create();
        var value = Value(GovernedLoopValueKind.Text, "\"request\"");

        var binding = GovernedLoopTypedBindingValue.Create(graph, "request-binding", value);
        var output = GovernedLoopTypedNodeOutput.Create(graph, "infer", "result", value);

        Assert.Equal(1, binding.SchemaVersion);
        Assert.Equal("request-binding", binding.BindingId);
        Assert.Equal(GovernedLoopBindingKind.Data, binding.BindingKind);
        Assert.Equal("trigger", binding.SourceNodeId);
        Assert.Equal("request", binding.SourcePortId);
        Assert.Equal("infer", binding.TargetNodeId);
        Assert.Equal("request", binding.TargetPortId);
        Assert.Equal("text", binding.ValueSchemaId);
        Assert.Same(value, binding.Value);
        Assert.Equal(graph.RevisionReference, binding.GraphRevision);
        Assert.NotSame(graph.RevisionReference, binding.GraphRevision);

        Assert.Equal(1, output.SchemaVersion);
        Assert.Equal("infer", output.NodeId);
        Assert.Equal("result", output.PortId);
        Assert.Equal(GovernedLoopBindingKind.Data, output.BindingKind);
        Assert.Equal("text", output.ValueSchemaId);
        Assert.Same(value, output.Value);
        Assert.Equal(graph.RevisionReference, output.GraphRevision);
        Assert.NotSame(graph.RevisionReference, output.GraphRevision);
    }

    [Fact]
    public void Materialization_rejects_missing_ports_kind_substitution_and_nonnullable_nulls()
    {
        var graph = GovernedLoopGraphTestFixture.Create();
        var boolean = Value(GovernedLoopValueKind.Boolean, "true");
        var nullText = Value(GovernedLoopValueKind.Text, "null");

        Assert.Throws<ArgumentException>(() => GovernedLoopTypedBindingValue.Create(graph, "missing", boolean));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedBindingValue.Create(graph, "request-binding", boolean));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedBindingValue.Create(graph, "request-binding", nullText));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedNodeOutput.Create(graph, "infer", "request", boolean));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedNodeOutput.Create(graph, "missing", "result", boolean));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopTypedBindingValue.Create(null!, "request-binding", nullText));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopTypedNodeOutput.Create(graph, "infer", "result", null!));

        var nullable = GovernedLoopGraphTestFixture.Create(schemas: [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, true)]);
        Assert.True(GovernedLoopTypedBindingValue.Create(nullable, "request-binding", nullText).Value.IsNull);
    }

    [Fact]
    public void Validation_evidence_is_boolean_consistent_bounded_sorted_and_rfc6901_scoped()
    {
        var later = GovernedLoopValidationObservation.Create("kind-mismatch", "/items/1");
        var earlier = GovernedLoopValidationObservation.Create("required-value-missing", "/items/0");
        var failure = GovernedLoopValidationEvidence.Create(1, false, [later, earlier]);
        var success = GovernedLoopValidationEvidence.Create(1, true, []);

        Assert.False(failure.Passed);
        Assert.Equal([earlier, later], failure.Observations);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopValidationObservation>)failure.Observations).Add(earlier));
        Assert.True(success.Passed);
        Assert.Empty(success.Observations);
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationEvidence.Create(2, true, []));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationEvidence.Create(1, true, [earlier]));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationEvidence.Create(1, false, []));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationEvidence.Create(1, false, [earlier, earlier]));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationEvidence.Create(1, false, Enumerable.Range(0, CustomLoopLimits.MaxGraphPureNodeObservations + 1).Select(index => GovernedLoopValidationObservation.Create($"code-{index}", $"/{index}"))));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationEvidence.Create(1, false, InfiniteObservations()));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationEvidence.Create(1, false, ThrowingObservations(earlier)));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationObservation.Create("INVALID", "/valid"));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationObservation.Create("valid", "not-a-pointer"));
        Assert.Throws<ArgumentException>(() => GovernedLoopValidationObservation.Create("valid", "/bad~2escape"));
    }

    [Fact]
    public void Pure_node_reservation_covers_base64_outcome_and_metadata_but_stays_within_trace()
    {
        var encodedOutcomeBound = ((CustomLoopLimits.MaxGraphPureNodeOutcomeUtf8Bytes + 2) / 3) * 4;
        Assert.True(CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes >= encodedOutcomeBound + (256 * 1024));
        Assert.True(CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes < CustomLoopLimits.MaxRunTraceUtf8Bytes);
    }

    private static IEnumerable<GovernedLoopValidationObservation> InfiniteObservations()
    {
        var index = 0;
        while (true)
        {
            yield return GovernedLoopValidationObservation.Create($"code-{index}", $"/{index}");
            index++;
        }
    }

    private static IEnumerable<GovernedLoopValidationObservation> ThrowingObservations(GovernedLoopValidationObservation observation)
    {
        yield return observation;
        throw new IOException("Injected enumeration failure.");
    }

    private static GovernedLoopTypedValue Value(GovernedLoopValueKind kind, string json)
    {
        Assert.True(GovernedLoopTypedValue.TryCreate(1, kind, json, out var value, out var validation));
        Assert.True(validation.IsValid);
        return value!;
    }
}
