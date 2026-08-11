using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Common.Tests;

public sealed class GovernedLoopTypedValueTests
{
    [Fact]
    public void Every_supported_root_kind_is_canonical_bounded_and_hash_bound()
    {
        var candidates = new (GovernedLoopValueKind Kind, string Input, string Canonical)[]
        {
            (GovernedLoopValueKind.Text, "\"hello\"", "\"hello\""),
            (GovernedLoopValueKind.Boolean, "true", "true"),
            (GovernedLoopValueKind.Integer, "-42", "-42"),
            (GovernedLoopValueKind.Number, "1.0", "1"),
            (GovernedLoopValueKind.Object, "{\"z\":2.0,\"a\":[true,\"x\"]}", "{\"a\":[true,\"x\"],\"z\":2}"),
            (GovernedLoopValueKind.Array, "[3.0,{\"b\":false,\"a\":null}]", "[3,{\"a\":null,\"b\":false}]")
        };

        foreach (var candidate in candidates)
        {
            Assert.True(GovernedLoopTypedValue.TryCreate(1, candidate.Kind, candidate.Input, out var value, out var validation));
            Assert.True(validation.IsValid);
            Assert.Equal(candidate.Canonical, value!.CanonicalValueJson);
            Assert.Equal($"{{\"schemaVersion\":1,\"kind\":\"{GovernedLoopValueKindVocabulary.ToCanonical(candidate.Kind)}\",\"value\":{candidate.Canonical}}}", value.CanonicalJson);
            Assert.Equal(64, value.ContentHash.Length);
            Assert.True(GovernedLoopTypedValueHash.Matches(value, value.ContentHash));
        }
    }

    [Fact]
    public void Equivalent_structured_inputs_produce_byte_identical_values_and_hashes()
    {
        Assert.True(GovernedLoopTypedValue.TryCreate(1, GovernedLoopValueKind.Object, "{\"second\":2.00,\"first\":1e0}", out var first, out _));
        Assert.True(GovernedLoopTypedValue.TryCreate(1, GovernedLoopValueKind.Object, "{\"first\":1,\"second\":2}", out var second, out _));

        Assert.Equal(first, second);
        Assert.Equal(first!.CanonicalJson, second!.CanonicalJson);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal("98e830a29afa76adc9fccf20d1b8fa50154b9ac6100c1c04901fae3ea9825ae1", first.ContentHash);
    }

    [Theory]
    [InlineData(GovernedLoopValueKind.Text, "true", "typed-value.kind.mismatch")]
    [InlineData(GovernedLoopValueKind.Boolean, "0", "typed-value.kind.mismatch")]
    [InlineData(GovernedLoopValueKind.Integer, "1.0", "typed-value.integer.invalid")]
    [InlineData(GovernedLoopValueKind.Integer, "9223372036854775808", "typed-value.integer.invalid")]
    [InlineData(GovernedLoopValueKind.Number, "-0", "typed-value.number.invalid")]
    [InlineData(GovernedLoopValueKind.Number, "9007199254740993", "typed-value.number.invalid")]
    [InlineData(GovernedLoopValueKind.Object, "[]", "typed-value.kind.mismatch")]
    [InlineData(GovernedLoopValueKind.Array, "{}", "typed-value.kind.mismatch")]
    [InlineData(GovernedLoopValueKind.Binary, "\"AA==\"", "typed-value.kind.unsupported")]
    [InlineData(GovernedLoopValueKind.Unknown, "null", "typed-value.kind.unsupported")]
    public void Unsupported_kinds_shapes_coercions_and_numeric_forms_fail_closed(GovernedLoopValueKind kind, string json, string code)
    {
        Assert.False(GovernedLoopTypedValue.TryCreate(1, kind, json, out var value, out var validation));
        Assert.Null(value);
        Assert.Equal(code, Assert.Single(validation.Errors).Code);
    }

    [Fact]
    public void Explicit_null_retains_its_declared_kind_for_later_schema_validation()
    {
        Assert.True(GovernedLoopTypedValue.TryCreate(1, GovernedLoopValueKind.Integer, "null", out var value, out _));

        Assert.True(value!.IsNull);
        Assert.Equal(GovernedLoopValueKind.Integer, value.Kind);
        Assert.Equal("{\"schemaVersion\":1,\"kind\":\"integer\",\"value\":null}", value.CanonicalJson);
    }

    [Fact]
    public void Unsafe_unicode_duplicate_properties_and_bounded_shapes_are_rejected()
    {
        AssertCode(GovernedLoopValueKind.Text, "\"e\\u0301\"", "typed-value.text.unsafe");
        AssertCode(GovernedLoopValueKind.Text, "\"unsafe\\u202evalue\"", "typed-value.text.unsafe");
        AssertCode(GovernedLoopValueKind.Object, "{\"same\":1,\"same\":2}", "typed-value.object.duplicate-property");
        AssertCode(GovernedLoopValueKind.Array, $"[{string.Join(',', Enumerable.Repeat("null", CustomLoopLimits.MaxGraphTypedValueCollectionEntries + 1))}]", "typed-value.collection.exceeded");
        AssertCode(GovernedLoopValueKind.Text, $"\"{new string('x', CustomLoopLimits.MaxGraphTypedValueStringCharacters + 1)}\"", "typed-value.text.unsafe");
        AssertCode(GovernedLoopValueKind.Number, $"1e{new string('0', CustomLoopLimits.MaxGraphTypedValueExponentCharacters + 1)}", "typed-value.number.invalid");
        AssertCode(GovernedLoopValueKind.Text, $"\"{new string('x', CustomLoopLimits.MaxGraphTypedValueUtf8Bytes)}\"", "typed-value.json.invalid");

        var tooDeep = string.Concat(Enumerable.Repeat("[", CustomLoopLimits.MaxGraphTypedValueDepth), "null", Enumerable.Repeat("]", CustomLoopLimits.MaxGraphTypedValueDepth));
        Assert.False(GovernedLoopTypedValue.TryCreate(1, GovernedLoopValueKind.Array, tooDeep, out _, out var depthValidation));
        Assert.Contains(Assert.Single(depthValidation.Errors).Code, new[] { "typed-value.json.malformed", "typed-value.shape.exceeded" });
    }

    [Fact]
    public void Strict_reader_accepts_only_the_exact_canonical_schema_one_envelope()
    {
        Assert.True(GovernedLoopTypedValue.TryCreate(1, GovernedLoopValueKind.Object, "{\"b\":2,\"a\":1}", out var expected, out _));
        Assert.True(GovernedLoopTypedValue.TryDeserialize(expected!.CanonicalJson, out var parsed, out var valid));
        Assert.True(valid.IsValid);
        Assert.Equal(expected, parsed);

        AssertReadCode("{\"kind\":\"object\",\"schemaVersion\":1,\"value\":{\"a\":1,\"b\":2}}", "typed-value.document.noncanonical");
        AssertReadCode("{\"schemaVersion\":1,\"kind\":\"object\",\"value\":{\"b\":2,\"a\":1}}", "typed-value.document.noncanonical");
        AssertReadCode("{\"schemaVersion\":2,\"kind\":\"object\",\"value\":{}}", "typed-value.schema-version.unsupported");
        AssertReadCode("{\"schemaVersion\":1,\"kind\":\"Object\",\"value\":{}}", "typed-value.document.shape");
        AssertReadCode("{\"schemaVersion\":1,\"kind\":\"object\",\"value\":{},\"extra\":true}", "typed-value.document.shape");
        AssertReadCode("{", "typed-value.document.malformed");
    }

    [Fact]
    public void Hash_verification_rejects_noncanonical_or_mismatched_claims()
    {
        Assert.True(GovernedLoopTypedValue.TryCreate(1, GovernedLoopValueKind.Text, "\"value\"", out var value, out _));

        Assert.False(GovernedLoopTypedValueHash.Matches(value, new string('A', 64)));
        Assert.False(GovernedLoopTypedValueHash.Matches(value, new string('0', 64)));
        Assert.False(GovernedLoopTypedValueHash.Matches(null, value!.ContentHash));
        Assert.Equal(value!.ContentHash, GovernedLoopTypedValueHash.Compute(value));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopTypedValueHash.Compute(null!));
    }

    [Fact]
    public void Unsupported_schema_versions_have_no_alias_or_fallback_reader()
    {
        Assert.False(GovernedLoopTypedValue.TryCreate(0, GovernedLoopValueKind.Text, "\"value\"", out _, out var zero));
        Assert.False(GovernedLoopTypedValue.TryCreate(2, GovernedLoopValueKind.Text, "\"value\"", out _, out var future));
        Assert.Equal("typed-value.schema-version.unsupported", Assert.Single(zero.Errors).Code);
        Assert.Equal("typed-value.schema-version.unsupported", Assert.Single(future.Errors).Code);
    }

    [Fact]
    public void Error_and_result_contracts_cannot_expose_unbounded_or_mutable_evidence()
    {
        var error = GovernedLoopTypedValueError.Create("typed-value-invalid", "$", "The value is invalid.");
        Assert.Equal("typed-value-invalid", error.Code);
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedValueError.Create("INVALID", "$", "The value is invalid."));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedValueError.Create("valid", string.Empty, "The value is invalid."));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedValueError.Create("valid", new string('p', CustomLoopLimits.MaxGraphValidationErrorPathCharacters + 1), "The value is invalid."));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedValueError.Create("valid", "$", string.Empty));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedValueError.Create("valid", "$", new string('m', CustomLoopLimits.MaxGraphValidationErrorMessageCharacters + 1)));
        Assert.Throws<ArgumentException>(() => GovernedLoopTypedValueError.Create("valid", "$", "unsafe\u202emessage"));

        Assert.False(GovernedLoopTypedValue.TryCreate(1, GovernedLoopValueKind.Text, "true", out _, out var validation));
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopTypedValueError>)validation.Errors).Add(error));
    }

    private static void AssertCode(GovernedLoopValueKind kind, string json, string code)
    {
        Assert.False(GovernedLoopTypedValue.TryCreate(1, kind, json, out _, out var validation));
        Assert.Equal(code, Assert.Single(validation.Errors).Code);
    }

    private static void AssertReadCode(string json, string code)
    {
        Assert.False(GovernedLoopTypedValue.TryDeserialize(json, out _, out var validation));
        Assert.Equal(code, Assert.Single(validation.Errors).Code);
    }
}
