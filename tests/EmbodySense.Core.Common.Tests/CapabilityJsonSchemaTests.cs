using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Tests;

public sealed class CapabilityJsonSchemaTests
{
    [Fact]
    public void Schema_canonicalization_is_recursive_deterministic_and_preserves_array_order()
    {
        var first = Schema($"{{\"type\":\"object\",\"required\":[\"z\",\"a\"],\"properties\":{{\"z\":{{\"minimum\":1.0,\"type\":\"number\"}},\"a\":{{\"const\":true}}}},\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\"}}");
        var reordered = Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"properties\":{{\"a\":{{\"const\":true}},\"z\":{{\"type\":\"number\",\"minimum\":1}}}},\"required\":[\"z\",\"a\"],\"type\":\"object\"}}");

        Assert.Equal(first, reordered);
        Assert.True(first.Equals((object)reordered));
        Assert.False(first.Equals((object)first.CanonicalJson));
        Assert.Equal(first.GetHashCode(), reordered.GetHashCode());
        Assert.Equal(first.CanonicalJson, first.ToString());
        Assert.StartsWith("{\"$schema\":", first.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"required\":[\"z\",\"a\"]", first.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"minimum\":1", first.CanonicalJson, StringComparison.Ordinal);

        var literals = Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"enum\":[null,false,true,\"text\",2],\"nested\":[[{{\"b\":2,\"a\":1}}]]}}");
        Assert.Contains("[null,false,true,\"text\",2]", literals.CanonicalJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0", "1")]
    [InlineData("0.0100", "1e-2")]
    [InlineData("1000", "1e3")]
    [InlineData("1E+3", "1e3")]
    public void Schema_canonicalization_normalizes_lossless_finite_numbers(string firstNumber, string secondNumber)
    {
        var dialect = CapabilityJsonSchema.Draft202012Dialect;
        var first = Schema($"{{\"$schema\":\"{dialect}\",\"minimum\":{firstNumber}}}");
        var second = Schema($"{{\"$schema\":\"{dialect}\",\"minimum\":{secondNumber}}}");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("1e-999")]
    [InlineData("9007199254740993")]
    public void Schema_parser_rejects_numbers_that_cannot_round_trip_without_a_semantic_change(string number)
    {
        var json = $"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"minimum\":{number}}}";

        Assert.False(CapabilityJsonSchema.TryCreate(json, out var schema, out var error));
        Assert.Null(schema);
        Assert.Equal("unsafe_json_schema_number", error?.Code);
    }

    [Fact]
    public void Schema_parser_rejects_malformed_ambiguous_unsafe_and_noncanonical_inputs()
    {
        var dialect = CapabilityJsonSchema.Draft202012Dialect;
        var invalid = new (string? Json, string Code)[]
        {
            (null, "invalid_json_schema"),
            (string.Empty, "invalid_json_schema"),
            ("[]", "invalid_json_schema"),
            ("{}", "unsupported_json_schema_dialect"),
            ($"{{\"$schema\":\"https://json-schema.org/draft/2019-09/schema\"}}", "unsupported_json_schema_dialect"),
            ("{", "invalid_json_schema"),
            ($"{{\"$schema\":\"{dialect}\",}}", "invalid_json_schema"),
            ($"{{/*comment*/\"$schema\":\"{dialect}\"}}", "invalid_json_schema"),
            ($"{{\"$schema\":\"{dialect}\",\"title\":\"one\",\"title\":\"two\"}}", "duplicate_json_schema_property"),
            ($"{{\"$schema\":\"{dialect}\",\"properties\":{{\"item\":{{\"type\":\"string\",\"type\":\"number\"}}}}}}", "duplicate_json_schema_property"),
            ($"{{\"$schema\":\"{dialect}\",\"title\":\"Cafe\\u0301\"}}", "unsafe_json_schema_text"),
            ($"{{\"$schema\":\"{dialect}\",\"title\":\"unsafe\\u202e\"}}", "unsafe_json_schema_text"),
            ($"{{\"$schema\":\"{dialect}\",\"unsafe\\u202e\":true}}", "unsafe_json_schema_text"),
            ($"{{\"$schema\":\"{dialect}\",\"values\":[\"unsafe\\u202e\"]}}", "unsafe_json_schema_text"),
            ($"{{\"$schema\":\"{dialect}\",\"minimum\":-0}}", "unsafe_json_schema_number"),
            ($"{{\"$schema\":\"{dialect}\",\"minimum\":1e999}}", "unsafe_json_schema_number"),
            ($"{{\"$schema\":\"{dialect}\",\"title\":\"\ud800\"}}", "invalid_json_schema"),
            ($"{{\"$schema\":\"{dialect}\",\"title\":\"\udc00\"}}", "invalid_json_schema")
        };

        foreach (var (json, code) in invalid)
        {
            Assert.False(CapabilityJsonSchema.TryCreate(json, out var schema, out var error));
            Assert.Null(schema);
            Assert.Equal(code, error?.Code);
        }
    }

    [Fact]
    public void Schema_parser_enforces_size_depth_and_element_bounds()
    {
        var oversized = new string('x', CapabilityContractLimits.MaxSchemaCharacters + 1);
        Assert.False(CapabilityJsonSchema.TryCreate(oversized, out _, out var sizeError));
        Assert.Equal("invalid_json_schema", sizeError?.Code);

        var deep = $"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"value\":" + new string('[', CapabilityContractLimits.MaxSchemaDepth + 1) + "null" + new string(']', CapabilityContractLimits.MaxSchemaDepth + 1) + "}";
        Assert.False(CapabilityJsonSchema.TryCreate(deep, out _, out var depthError));
        Assert.Contains(depthError?.Code, new[] { "invalid_json_schema", "json_schema_shape_exceeded" });

        var elements = string.Join(',', Enumerable.Range(0, CapabilityContractLimits.MaxSchemaElements + 1).Select(index => $"\"p{index}\":null"));
        var many = $"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",{elements}}}";
        Assert.False(CapabilityJsonSchema.TryCreate(many, out _, out var elementError));
        Assert.Contains(elementError?.Code, new[] { "json_schema_shape_exceeded", "invalid_json_schema" });

        var escapeExpansionCapacity = CapabilityContractLimits.MaxSchemaCharacters - CapabilityJsonSchema.Draft202012Dialect.Length - 40;
        var escapeExpansion = $"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"title\":\"{new string('\u00e9', escapeExpansionCapacity)}\"}}";
        Assert.True(escapeExpansion.Length <= CapabilityContractLimits.MaxSchemaCharacters);
        Assert.False(CapabilityJsonSchema.TryCreate(escapeExpansion, out _, out var expansionError));
        Assert.Equal("json_schema_too_large", expansionError?.Code);
    }

    private static CapabilityJsonSchema Schema(string value)
    {
        Assert.True(CapabilityJsonSchema.TryCreate(value, out var schema, out var error), error?.Message);
        return schema!;
    }
}
