using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Execution.Effects;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Effects;

public sealed class GovernedActuatorInputContractTests
{
    private static readonly CapabilityJsonSchema _schema = CapabilityContractTestData.Schema($$"""
        {
          "$schema": "{{CapabilityJsonSchema.Draft202012Dialect}}",
          "type": "object",
          "additionalProperties": false,
          "required": ["count", "target"],
          "properties": {
            "target": { "type": "string", "maxLength": 32 },
            "count": { "type": "integer" },
            "flags": { "type": "array", "maxItems": 3, "items": { "type": "boolean" } }
          }
        }
        """);

    [Fact]
    public void Canonical_input_hash_is_independent_of_property_order_and_whitespace()
    {
        Assert.True(GovernedActuatorInputContract.TryCreate("{\"target\":\"alpha\",\"count\":1,\"flags\":[true,false]}", _schema, out var first, out var firstReason), firstReason);
        Assert.True(GovernedActuatorInputContract.TryCreate(" { \"flags\" : [ true, false ], \"count\" : 1.0, \"target\" : \"alpha\" } ", _schema, out var second, out var secondReason), secondReason);

        Assert.Equal("{\"count\":1,\"flags\":[true,false],\"target\":\"alpha\"}", first!.CanonicalJson);
        Assert.Equal(first.CanonicalJson, second!.CanonicalJson);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(first.CanonicalJson), first.Utf8ByteCount);
    }

    [Theory]
    [InlineData("{}", "actuator-input-required-property-missing")]
    [InlineData("{\"target\":\"alpha\",\"count\":\"1\"}", "actuator-input-schema-mismatch")]
    [InlineData("{\"target\":\"alpha\",\"count\":1,\"extra\":true}", "actuator-input-additional-property")]
    [InlineData("{\"target\":\"alpha\",\"target\":\"beta\",\"count\":1}", "actuator-input-duplicate-property")]
    [InlineData("{\"target\":\"alpha\",\"count\":1,}", "actuator-input-malformed")]
    public void Malformed_or_schema_mismatched_input_fails_closed(string json, string expectedReason)
    {
        Assert.False(GovernedActuatorInputContract.TryCreate(json, _schema, out var evidence, out var reason));
        Assert.Null(evidence);
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData("{\"target\":\"\\uD800\",\"count\":1}")]
    [InlineData("{\"target\":\"\\uDC00\",\"count\":1}")]
    [InlineData("{\"\\uD800\":\"alpha\",\"target\":\"alpha\",\"count\":1}")]
    [InlineData("{\"\\uDC00\":\"alpha\",\"target\":\"alpha\",\"count\":1}")]
    public void Malformed_escaped_surrogates_fail_closed_through_both_public_input_apis(string json)
    {
        Assert.False(GovernedActuatorInputContract.TryCanonicalize(json, out var canonical, out var canonicalReason));
        Assert.Null(canonical);
        Assert.Equal("actuator-input-malformed", canonicalReason);

        Assert.False(GovernedActuatorInputContract.TryCreate(json, _schema, out var created, out var createReason));
        Assert.Null(created);
        Assert.Equal("actuator-input-malformed", createReason);
    }

    [Fact]
    public void Unsupported_or_unbounded_schema_input_fails_closed()
    {
        var unsupported = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"oneOf\":[{{\"type\":\"string\"}}]}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("\"value\"", unsupported, out _, out var reason));
        Assert.Equal("actuator-input-schema-unsupported", reason);

        var oversized = "{\"target\":\"alpha\",\"count\":1,\"padding\":\"" + new string('a', GovernedLoopEffectAttemptContractLimits.MaxCanonicalInputUtf8Bytes) + "\"}";
        Assert.False(GovernedActuatorInputContract.TryCreate(oversized, _schema, out _, out reason));
        Assert.Equal("actuator-input-invalid", reason);
    }

    [Theory]
    [InlineData("\"enum\":[\"allowed\"]")]
    [InlineData("\"pattern\":\"^a\"")]
    [InlineData("\"minimum\":2")]
    [InlineData("\"oneOf\":[{\"type\":\"string\"}]")]
    [InlineData("\"x-unknown\":true")]
    public void Unsupported_adjacent_schema_keywords_are_never_silently_ignored(string keyword)
    {
        var schema = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"string\",{keyword}}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("\"value\"", schema, out _, out var reason));
        Assert.Equal("actuator-input-schema-unsupported", reason);
    }

    [Theory]
    [InlineData("[\"target\",\"target\"]")]
    [InlineData("[\"target\",1]")]
    public void Malformed_required_schema_is_rejected(string required)
    {
        var schema = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\",\"required\":{required},\"properties\":{{\"target\":{{\"type\":\"string\"}}}}}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("{\"target\":\"value\"}", schema, out _, out var reason));
        Assert.Equal("actuator-input-schema-unsupported", reason);
    }

    [Fact]
    public void Nested_schema_cannot_redeclare_the_dialect()
    {
        var schema = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\",\"properties\":{{\"target\":{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"string\"}}}}}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("{\"target\":\"value\"}", schema, out _, out var reason));
        Assert.Equal("actuator-input-schema-unsupported", reason);
    }

    [Fact]
    public void Unsupported_optional_property_schema_is_rejected_even_when_the_property_is_absent()
    {
        var schema = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\",\"properties\":{{\"optional\":{{\"type\":\"string\",\"pattern\":\"^a\"}}}}}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("{}", schema, out _, out var reason));
        Assert.Equal("actuator-input-schema-unsupported", reason);
    }

    [Fact]
    public void Unsupported_items_schema_is_rejected_even_for_an_empty_array()
    {
        var schema = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"array\",\"items\":{{\"type\":\"number\",\"minimum\":0}}}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("[]", schema, out _, out var reason));
        Assert.Equal("actuator-input-schema-unsupported", reason);
    }

    [Fact]
    public void Malformed_unused_property_schema_is_rejected()
    {
        var schema = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\",\"properties\":{{\"optional\":true}}}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("{}", schema, out _, out var reason));
        Assert.Equal("actuator-input-schema-unsupported", reason);
    }

    [Fact]
    public void Generic_canonicalization_is_lossless_for_tiny_and_large_numbers()
    {
        Assert.True(GovernedActuatorInputContract.TryCanonicalize("{\"n\":0}", out var zero, out var zeroReason), zeroReason);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize("{\"n\":1e-1000}", out var tiny, out var tinyReason), tinyReason);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize("{\"n\":1e10000}", out var largeOne, out var largeOneReason), largeOneReason);
        Assert.True(GovernedActuatorInputContract.TryCanonicalize("{\"n\":2e10000}", out var largeTwo, out var largeTwoReason), largeTwoReason);

        Assert.NotEqual(zero!.CanonicalJson, tiny!.CanonicalJson);
        Assert.NotEqual(zero.Fingerprint, tiny.Fingerprint);
        Assert.NotEqual(largeOne!.Fingerprint, largeTwo!.Fingerprint);
        Assert.Equal("{\"n\":1e-1000}", tiny.CanonicalJson);
    }

    [Theory]
    [InlineData("{\"n\":1.00e2}", "{\"n\":1e2}")]
    [InlineData("{\"n\":100.0e-2}", "{\"n\":1}")]
    [InlineData("{\"n\":0.0010}", "{\"n\":1e-3}")]
    public void Exact_exponents_normalize_without_rounding(string json, string expected)
    {
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(json, out var evidence, out var reason), reason);
        Assert.Equal(expected, evidence!.CanonicalJson);
    }

    [Fact]
    public void Signed_zero_and_number_bounds_fail_closed()
    {
        Assert.False(GovernedActuatorInputContract.TryCanonicalize("-0", out _, out var signedZero));
        Assert.Equal("actuator-input-number-invalid", signedZero);
        Assert.False(GovernedActuatorInputContract.TryCanonicalize(new string('9', GovernedLoopEffectAttemptContractLimits.MaxInputNumberDigits + 1), out _, out var digits));
        Assert.Equal("actuator-input-number-invalid", digits);
        Assert.False(GovernedActuatorInputContract.TryCanonicalize("1e10001", out _, out var exponent));
        Assert.Equal("actuator-input-number-invalid", exponent);
    }

    [Fact]
    public void Unsafe_nested_untyped_keys_and_invalid_declared_keys_fail_closed()
    {
        var open = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("{\"outer\":{\"bad\\u0001key\":1}}", open, out _, out var nested));
        Assert.Equal("actuator-input-text-invalid", nested);

        var emptyProperty = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\",\"properties\":{{\"\":{{\"type\":\"string\"}}}}}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("{}", emptyProperty, out _, out var declared));
        Assert.Equal("actuator-input-schema-unsupported", declared);

        var emptyRequired = CapabilityContractTestData.Schema($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\",\"required\":[\"\"]}}");
        Assert.False(GovernedActuatorInputContract.TryCreate("{}", emptyRequired, out _, out var required));
        Assert.Equal("actuator-input-schema-unsupported", required);
    }
}
