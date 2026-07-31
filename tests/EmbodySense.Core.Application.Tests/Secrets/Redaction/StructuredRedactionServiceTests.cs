using System.Collections;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Secrets.Redaction;
using EmbodySense.Core.Application.Secrets.Redaction.Models;
using EmbodySense.Core.Common.Secrets;
using EmbodySense.Core.Common.Secrets.Redaction;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Application.Tests.Secrets.Redaction;

public sealed class StructuredRedactionServiceTests
{
    private const string Canary = "credential canary+";
    private readonly StructuredRedactionService _service = new();

    [Fact]
    public void Structure_redacts_keys_nested_values_encoded_derivatives_and_cycles_without_arbitrary_ToString()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary));
        var source = new Dictionary<string, object?>
        {
            [Canary + "-key"] = Canary,
            ["nested"] = new object?[] { encoded, true, 42, null },
            ["unsupported"] = new ThrowingProjectionValue()
        };
        source["self"] = source;
        using var fixture = CreateScope();

        var result = _service.RedactStructure(source, fixture.Scope);
        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(encoded, json, StringComparison.Ordinal);
        Assert.True(result.Summary.TextReplacementCount >= 3);
        Assert.Equal(1, result.Summary.LimitCount);
        Assert.Equal(1, result.Summary.FailureCount);
        Assert.True(result.Summary.ProjectedCharacterCount > 0);
        Assert.Contains(result.Value.Properties, property => property.Value.Kind == RedactedDataKind.Marker && property.Value.Text == StructuredRedactionService.CycleMarker);
        Assert.Contains(result.Value.Properties, property => property.Value.Kind == RedactedDataKind.Marker && property.Value.Text == StructuredRedactionService.UnsupportedValueMarker);
    }

    [Fact]
    public void Structure_projects_known_scalars_in_invariant_sanitized_forms()
    {
        var guid = Guid.Parse("cab2dc76-a57f-4e28-b78e-8e28d288e9ad");
        IReadOnlyDictionary<string, object?> source = new Dictionary<string, object?>
        {
            ["bool"] = true,
            ["byte"] = (byte)1,
            ["sbyte"] = (sbyte)-2,
            ["short"] = (short)-3,
            ["ushort"] = (ushort)4,
            ["int"] = 9999,
            ["uint"] = 6U,
            ["long"] = 7L,
            ["ulong"] = 8UL,
            ["float"] = 1.25F,
            ["double"] = 2.5D,
            ["decimal"] = 3.75M,
            ["date"] = DateTime.UnixEpoch,
            ["offset"] = DateTimeOffset.UnixEpoch,
            ["span"] = TimeSpan.FromSeconds(2),
            ["guid"] = guid,
            ["uri"] = new Uri("https://example.test/path"),
            ["enum"] = DayOfWeek.Friday,
            ["char"] = 'z'
        };
        using var fixture = CreateScope("9999");

        var result = _service.RedactStructure(source, fixture.Scope);
        var values = result.Value.Properties.ToDictionary(property => property.Key, property => property.Value);

        Assert.True(values["bool"].Boolean);
        Assert.Equal("[REDACTED]", values["int"].Text);
        Assert.Equal(guid.ToString("D"), values["guid"].Text);
        Assert.Equal("Friday", values["enum"].Text);
        Assert.True(result.Summary.IsComplete);
    }

    [Fact]
    public void Structure_fails_closed_for_depth_entry_node_and_read_bounds()
    {
        IReadOnlyDictionary<string, object?> deep = new Dictionary<string, object?> { ["level"] = new Dictionary<string, object?> { ["leaf"] = Canary } };
        IReadOnlyDictionary<string, object?> wide = new Dictionary<string, object?> { ["a"] = Canary, ["b"] = Canary };
        IReadOnlyDictionary<string, object?> sequence = new Dictionary<string, object?> { ["items"] = new[] { "a", "b" } };
        using var fixture = CreateScope();

        var depth = _service.RedactStructure(deep, fixture.Scope, new RedactionProjectionLimits(maxDepth: 1));
        var entries = _service.RedactStructure(wide, fixture.Scope, new RedactionProjectionLimits(maxCollectionEntries: 1));
        var nodes = _service.RedactStructure(deep, fixture.Scope, new RedactionProjectionLimits(maxNodes: 1));
        var sequenceEntries = _service.RedactStructure(sequence, fixture.Scope, new RedactionProjectionLimits(maxCollectionEntries: 1));
        var hostile = _service.RedactStructure(new ThrowingReadOnlyDictionary(), fixture.Scope);

        Assert.Contains(Flatten(depth.Value), node => node.Text == StructuredRedactionService.DepthLimitMarker);
        Assert.Equal(StructuredRedactionService.EntryLimitMarker, entries.Value.Text);
        Assert.Contains(Flatten(nodes.Value), node => node.Text == StructuredRedactionService.NodeLimitMarker);
        Assert.Contains(Flatten(sequenceEntries.Value), node => node.Text == StructuredRedactionService.EntryLimitMarker);
        Assert.Equal(StructuredRedactionService.ReadFailureMarker, hostile.Value.Text);
        Assert.False(depth.Summary.IsComplete);
        Assert.False(hostile.Summary.IsComplete);
    }

    [Fact]
    public void Structure_rejects_non_string_dictionary_keys_without_projecting_values()
    {
        var hostile = new Hashtable { [42] = Canary };
        IReadOnlyDictionary<string, object?> source = new Dictionary<string, object?> { ["hostile"] = hostile };
        using var fixture = CreateScope();

        var result = _service.RedactStructure(source, fixture.Scope);
        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.Contains(Flatten(result.Value), node => node.Text == StructuredRedactionService.UnsupportedValueMarker);
    }

    [Fact]
    public void Non_generic_dictionaries_and_sequences_fail_closed_for_entry_and_read_bounds()
    {
        var wideDictionary = new Hashtable { ["a"] = "1", ["b"] = "2" };
        IReadOnlyDictionary<string, object?> source = new Dictionary<string, object?>
        {
            ["dictionary"] = wideDictionary,
            ["throwing-dictionary"] = new ThrowingDictionary(),
            ["throwing-sequence"] = new ThrowingSequence()
        };
        using var fixture = CreateScope();

        var result = _service.RedactStructure(source, fixture.Scope, new RedactionProjectionLimits(maxCollectionEntries: 3));
        var nodes = Flatten(result.Value).ToArray();

        Assert.Contains(nodes, node => node.Text == StructuredRedactionService.ReadFailureMarker);
        Assert.True(result.Summary.FailureCount >= 2);

        IReadOnlyDictionary<string, object?> boundedSource = new Dictionary<string, object?> { ["dictionary"] = wideDictionary };
        var bounded = _service.RedactStructure(boundedSource, fixture.Scope, new RedactionProjectionLimits(maxCollectionEntries: 1));
        Assert.Contains(Flatten(bounded.Value), node => node.Text == StructuredRedactionService.EntryLimitMarker);
    }

    [Fact]
    public void Header_projection_redacts_names_and_values_and_sorts_names_deterministically()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary));
        var headers = new List<KeyValuePair<string, IEnumerable<string>>>
        {
            new("z-" + Canary, [Canary, encoded]),
            new("a-header", ["safe"])
        };
        using var fixture = CreateScope();

        var result = _service.RedactHeaders(headers, fixture.Scope);
        var json = JsonSerializer.Serialize(result);

        Assert.Equal("a-header", result.Value[0].Name);
        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(encoded, json, StringComparison.Ordinal);
        Assert.Equal(3, result.Summary.TextReplacementCount);
        Assert.True(result.Summary.IsComplete);
    }

    [Fact]
    public void Header_projection_bounds_outer_values_nodes_and_hostile_enumerators()
    {
        var outerWide = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("a", ["1"]),
            new KeyValuePair<string, IEnumerable<string>>("b", ["2"])
        };
        var valueWide = new[] { new KeyValuePair<string, IEnumerable<string>>("a", ["1", "2"]) };
        var nodeWide = new[] { new KeyValuePair<string, IEnumerable<string>>("a", ["1", "2"]) };
        using var fixture = CreateScope();

        var outer = _service.RedactHeaders(outerWide, fixture.Scope, new RedactionProjectionLimits(maxCollectionEntries: 1));
        var values = _service.RedactHeaders(valueWide, fixture.Scope, new RedactionProjectionLimits(maxCollectionEntries: 1));
        var nodes = _service.RedactHeaders(nodeWide, fixture.Scope, new RedactionProjectionLimits(maxNodes: 1));
        var hostileOuter = _service.RedactHeaders(ThrowBeforeFirstHeader(), fixture.Scope);
        var hostileValues = _service.RedactHeaders([new KeyValuePair<string, IEnumerable<string>>("a", ThrowBeforeFirstValue())], fixture.Scope);
        var headerNodeLimit = _service.RedactHeaders(outerWide, fixture.Scope, new RedactionProjectionLimits(maxNodes: 1));

        Assert.Equal(StructuredRedactionService.EntryLimitMarker, Assert.Single(outer.Value).Name);
        Assert.Equal(StructuredRedactionService.EntryLimitMarker, Assert.Single(Assert.Single(values.Value).Values));
        Assert.Equal(StructuredRedactionService.NodeLimitMarker, Assert.Single(Assert.Single(nodes.Value).Values));
        Assert.Equal(StructuredRedactionService.ReadFailureMarker, Assert.Single(hostileOuter.Value).Name);
        Assert.Equal(StructuredRedactionService.ReadFailureMarker, Assert.Single(Assert.Single(hostileValues.Value).Values));
        Assert.Equal(StructuredRedactionService.NodeLimitMarker, headerNodeLimit.Value[1].Name);
    }

    [Fact]
    public void Uri_projection_redacts_supported_encoded_derivatives_from_original_text()
    {
        using var fixture = CreateScope();
        var uri = new Uri("https://example.test/?credential=" + Uri.EscapeDataString(Canary));

        var result = _service.RedactUri(uri, fixture.Scope);

        Assert.DoesNotContain(Uri.EscapeDataString(Canary), result.Value, StringComparison.Ordinal);
        Assert.Equal(1, result.Summary.ReplacementCount);
    }

    [Fact]
    public void Exception_projection_redacts_message_source_stack_data_and_inner_graph()
    {
        using var fixture = CreateScope();
        Exception captured;
        try
        {
            var inner = new ArgumentException("inner " + Canary) { Source = Canary };
            inner.Data[Canary + "-key"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary));
            throw new InvalidOperationException("outer " + Canary, inner) { Source = Canary };
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        var result = _service.RedactException(captured, fixture.Scope);
        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.Single(result.Value.InnerExceptions);
        Assert.False(result.Value.IsMarker);
        Assert.True(result.Summary.TextReplacementCount >= 5);
    }

    [Fact]
    public void Exception_projection_bounds_depth_and_aggregate_width_and_handles_hostile_message_getters()
    {
        using var fixture = CreateScope();
        var nested = new InvalidOperationException("one", new InvalidOperationException("two", new InvalidOperationException(Canary)));
        var aggregate = new AggregateException(new InvalidOperationException("one"), new InvalidOperationException("two"));

        var depth = _service.RedactException(nested, fixture.Scope, new RedactionProjectionLimits(maxDepth: 1));
        var width = _service.RedactException(aggregate, fixture.Scope, new RedactionProjectionLimits(maxCollectionEntries: 1));
        var hostile = _service.RedactException(new ThrowingMessageException(), fixture.Scope);
        var nodeLimit = _service.RedactException(nested, fixture.Scope, new RedactionProjectionLimits(maxNodes: 1));
        var hostileData = _service.RedactException(new ThrowingDataException(), fixture.Scope);

        Assert.True(depth.Value.InnerExceptions[0].InnerExceptions[0].IsMarker);
        Assert.Equal(StructuredRedactionService.DepthLimitMarker, depth.Value.InnerExceptions[0].InnerExceptions[0].Message);
        Assert.True(Assert.Single(width.Value.InnerExceptions).IsMarker);
        Assert.Equal(StructuredRedactionService.EntryLimitMarker, Assert.Single(width.Value.InnerExceptions).Message);
        Assert.Equal(StructuredRedactionService.ReadFailureMarker, hostile.Value.Message);
        Assert.Equal(1, hostile.Summary.FailureCount);
        Assert.True(Assert.Single(nodeLimit.Value.InnerExceptions).IsMarker);
        Assert.Equal(StructuredRedactionService.NodeLimitMarker, Assert.Single(nodeLimit.Value.InnerExceptions).Message);
        Assert.Equal(StructuredRedactionService.ReadFailureMarker, hostileData.Value.Data.Text);
    }

    [Fact]
    public void Exception_data_with_hostile_keys_fails_closed_without_leaking_values()
    {
        using var fixture = CreateScope();
        var exception = new InvalidOperationException("safe");
        exception.Data[42] = Canary;

        var result = _service.RedactException(exception, fixture.Scope);
        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.Equal(StructuredRedactionService.UnsupportedValueMarker, result.Value.Data.Text);
    }

    [Fact]
    public void Scoped_value_that_overlaps_projection_markers_never_survives_marker_output()
    {
        using var fixture = CreateScope("REDACTION");
        IReadOnlyDictionary<string, object?> source = new Dictionary<string, object?> { ["self"] = null };
        var mutable = (Dictionary<string, object?>)source;
        mutable["self"] = mutable;

        var result = _service.RedactStructure(source, fixture.Scope);
        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("REDACTION", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_sensitive_scope_propagates_fail_closed_status_into_projection_summary()
    {
        using var material = EphemeralSecretMaterial.Create(Canary);
        using var invalidScope = SensitiveRedactionScope.Create([material], new RedactionLimits(maxSensitiveValueCharacters: 1));
        IReadOnlyDictionary<string, object?> source = new Dictionary<string, object?> { [Canary] = Canary };

        var result = _service.RedactStructure(source, invalidScope);
        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.True(result.Summary.LimitCount >= 2);
        Assert.False(result.Summary.IsComplete);
    }

    [Fact]
    public void Aggregate_projection_character_budget_fails_closed_and_remains_bounded()
    {
        using var fixture = CreateScope();
        IReadOnlyDictionary<string, object?> source = new Dictionary<string, object?> { ["a"] = "1234", ["b"] = "5678" };

        var result = _service.RedactStructure(source, fixture.Scope, new RedactionProjectionLimits(maxProjectedCharacters: 5));

        Assert.Equal(5, result.Summary.ProjectedCharacterCount);
        Assert.Equal(1, result.Summary.LimitCount);
        Assert.Equal("a", result.Value.Properties[0].Key);
        Assert.Equal("1234", result.Value.Properties[0].Value.Text);
        Assert.Empty(result.Value.Properties[1].Key);
        Assert.Empty(result.Value.Properties[1].Value.Text!);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(33, 1, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 4_097, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1_025, 1)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(1, 1, 1, 1_048_577)]
    public void Projection_limits_reject_unbounded_configuration(int depth, int nodes, int entries, int characters)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedactionProjectionLimits(depth, nodes, entries, characters));
    }

    private static RedactionScopeFixture CreateScope(string value = Canary)
    {
        return new RedactionScopeFixture(value);
    }

    private static IEnumerable<RedactedDataNode> Flatten(RedactedDataNode root)
    {
        yield return root;
        foreach (var property in root.Properties)
        {
            foreach (var child in Flatten(property.Value))
            {
                yield return child;
            }
        }

        foreach (var item in root.Items)
        {
            foreach (var child in Flatten(item))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>> ThrowBeforeFirstHeader()
    {
        throw new InvalidOperationException("Hostile header enumeration.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static IEnumerable<string> ThrowBeforeFirstValue()
    {
        throw new InvalidOperationException("Hostile value enumeration.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

}
