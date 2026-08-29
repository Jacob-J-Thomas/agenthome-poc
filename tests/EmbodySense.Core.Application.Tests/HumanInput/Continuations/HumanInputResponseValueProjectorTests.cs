using System.Collections.Immutable;
using System.Text.Json;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

public sealed class HumanInputResponseValueProjectorTests
{
    [Fact]
    public void Projects_every_supported_response_kind_into_its_exact_typed_graph_value()
    {
        var cases = new[]
        {
            (
                new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null),
                new HumanInputResponseValue(HumanInputResponseKind.Text, "private text", null, null, null, null),
                GovernedLoopValueKind.Text,
                "private text"),
            (
                new HumanInputResponseSchema(HumanInputResponseKind.Choice, null, [new HumanInputChoice("choice-one", "Choice one")], null, null),
                new HumanInputResponseValue(HumanInputResponseKind.Choice, null, "choice-one", null, null, null),
                GovernedLoopValueKind.Text,
                "choice-one"),
            (
                new HumanInputResponseSchema(HumanInputResponseKind.Confirmation, null, null, null, null),
                new HumanInputResponseValue(HumanInputResponseKind.Confirmation, null, null, true, null, null),
                GovernedLoopValueKind.Boolean,
                "true"),
            (
                new HumanInputResponseSchema(HumanInputResponseKind.Reference, null, null, null, new HumanInputReferencePolicy(HumanInputReferenceKind.Artifact, 64)),
                new HumanInputResponseValue(HumanInputResponseKind.Reference, null, null, null, null, new HumanInputReference(HumanInputReferenceKind.Artifact, "artifact-one")),
                GovernedLoopValueKind.Text,
                "artifact-one"),
            (
                new HumanInputResponseSchema(
                    HumanInputResponseKind.Structured,
                    null,
                    null,
                    [
                        new HumanInputStructuredFieldSchema("note", HumanInputStructuredFieldKind.Text, true, 64, null),
                        new HumanInputStructuredFieldSchema("decision", HumanInputStructuredFieldKind.Choice, false, null, [new HumanInputChoice("continue", "Continue")]),
                    ],
                    null),
                new HumanInputResponseValue(
                    HumanInputResponseKind.Structured,
                    null,
                    null,
                    null,
                    ImmutableArray.Create(
                        new HumanInputStructuredFieldValue("note", "private note", null),
                        new HumanInputStructuredFieldValue("decision", null, "continue")),
                    null),
                GovernedLoopValueKind.Object,
                null),
        };

        foreach (var (schema, response, expectedKind, expectedValue) in cases)
        {
            Assert.True(HumanInputResponseValueProjector.TryProject(schema, response, out var value));
            var projected = Assert.IsType<EmbodySense.Core.Common.Loops.PureNodes.GovernedLoopTypedValue>(value);
            Assert.Equal(expectedKind, projected.Kind);
            if (expectedKind == GovernedLoopValueKind.Boolean)
            {
                Assert.Equal(expectedValue, projected.CanonicalValueJson);
            }
            else if (expectedValue is not null)
            {
                Assert.Equal(expectedValue, JsonSerializer.Deserialize<string>(projected.CanonicalValueJson) ?? projected.CanonicalValueJson);
            }
            else
            {
                using var document = JsonDocument.Parse(projected.CanonicalValueJson);
                Assert.Equal("private note", document.RootElement.GetProperty("note").GetString());
                Assert.Equal("continue", document.RootElement.GetProperty("decision").GetString());
            }
        }
    }

    [Fact]
    public void Rejects_mismatched_and_noncanonical_structured_response_shapes()
    {
        var text = new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null);
        var structured = new HumanInputResponseSchema(
            HumanInputResponseKind.Structured,
            null,
            null,
            [new HumanInputStructuredFieldSchema("required", HumanInputStructuredFieldKind.Text, true, 64, null)],
            null);

        Assert.False(HumanInputResponseValueProjector.TryProject(text, new HumanInputResponseValue(HumanInputResponseKind.Choice, null, "choice-one", null, null, null), out _));
        Assert.False(HumanInputResponseValueProjector.TryProject(structured, new HumanInputResponseValue(HumanInputResponseKind.Structured, null, null, null, ImmutableArray<HumanInputStructuredFieldValue>.Empty, null), out _));
        Assert.False(HumanInputResponseValueProjector.TryProject(structured, new HumanInputResponseValue(HumanInputResponseKind.Structured, null, null, null, ImmutableArray.Create(new HumanInputStructuredFieldValue("required", null, "choice-one")), null), out _));
        Assert.False(HumanInputResponseValueProjector.TryProject(null, new HumanInputResponseValue(HumanInputResponseKind.Text, "private text", null, null, null, null), out _));
    }

    [Fact]
    public void Rejects_absent_supported_values_and_structured_fields_that_are_not_an_exact_schema_projection()
    {
        var text = new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null);
        var structuredWithoutSchema = new HumanInputResponseSchema(HumanInputResponseKind.Structured, null, null, null, null);
        var structuredWithOptionalField = new HumanInputResponseSchema(
            HumanInputResponseKind.Structured,
            null,
            null,
            [new HumanInputStructuredFieldSchema("known", HumanInputStructuredFieldKind.Text, false, 64, null)],
            null);

        Assert.False(HumanInputResponseValueProjector.TryProject(text, new HumanInputResponseValue(HumanInputResponseKind.Text, null, null, null, null, null), out _));
        Assert.False(HumanInputResponseValueProjector.TryProject(
            structuredWithoutSchema,
            new HumanInputResponseValue(HumanInputResponseKind.Structured, null, null, null, ImmutableArray<HumanInputStructuredFieldValue>.Empty, null),
            out _));
        Assert.False(HumanInputResponseValueProjector.TryProject(
            structuredWithOptionalField,
            new HumanInputResponseValue(
                HumanInputResponseKind.Structured,
                null,
                null,
                null,
                ImmutableArray.Create(new HumanInputStructuredFieldValue("unknown", "private value", null)),
                null),
            out _));
    }
}
