using System.Net;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Secrets;
using EmbodySense.Core.Common.Secrets.Redaction;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Common.Tests.Secrets;

public sealed class SensitiveRedactionScopeTests
{
    [Fact]
    public void RedactText_removes_raw_percent_form_and_base64_derivatives()
    {
        const string Canary = "top !'()~secret*+";
        using var material = EphemeralSecretMaterial.Create(Canary);
        using var scope = SensitiveRedactionScope.Create([material]);
        material.Dispose();
        var percent = Uri.EscapeDataString(Canary);
        var form = WebUtility.UrlEncode(Canary);
        var lowerPercent = percent.ToLowerInvariant();
        var lowerForm = form.ToLowerInvariant();
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary));

        var result = scope.RedactText($"raw={Canary};percent={percent};lowerPercent={lowerPercent};form={form};lowerForm={lowerForm};base64={base64}");

        Assert.Equal(RedactionStatus.Completed, result.Summary.Status);
        Assert.Equal(6, result.Summary.ReplacementCount);
        Assert.Equal(1, result.Summary.SensitiveValueCount);
        Assert.Equal(0, result.Summary.IgnoredValueCount);
        Assert.True(result.Summary.ExaminedCharacterCount > 0);
        Assert.True(result.Summary.WorkUnitCount > 0);
        Assert.DoesNotContain(Canary, result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(percent, result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(lowerPercent, result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(form, result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(lowerForm, result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(base64, result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactText_matches_percent_escape_hex_case_independently_per_nibble()
    {
        const string Canary = "þ?";
        using var material = EphemeralSecretMaterial.Create(Canary);
        using var scope = SensitiveRedactionScope.Create([material]);
        const string MixedCasePercentEncoding = "%C3%be%3f";

        var result = scope.RedactText("credential=" + MixedCasePercentEncoding);

        Assert.Equal(RedactionStatus.Completed, result.Summary.Status);
        Assert.Equal(1, result.Summary.ReplacementCount);
        Assert.DoesNotContain(MixedCasePercentEncoding, result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactText_prefers_longest_overlap_and_is_independent_of_scope_order()
    {
        var forward = CreateScope(["abc", "abcdef", "x y", "þ"]);
        var reverse = CreateScope(["þ", "x y", "abcdef", "abc"]);
        using var first = forward.Scope;
        using var second = reverse.Scope;
        DisposeAll(forward.Materials);
        DisposeAll(reverse.Materials);
        const string Input = "abcdef abc x+y w74=";

        var firstResult = first.RedactText(Input);
        var secondResult = second.RedactText(Input);

        Assert.Equal(firstResult, secondResult);
        Assert.Equal(4, firstResult.Summary.ReplacementCount);
    }

    [Fact]
    public void Create_ignores_empty_and_duplicate_values_without_retaining_the_callers_buffers()
    {
        using var empty = EphemeralSecretMaterial.Create("");
        using var first = EphemeralSecretMaterial.Create("canary");
        using var duplicate = EphemeralSecretMaterial.Create("canary");
        using var scope = SensitiveRedactionScope.Create([empty, first, duplicate]);
        first.Dispose();
        duplicate.Dispose();

        var result = scope.RedactText("canary remains scoped");

        Assert.True(scope.IsValid);
        Assert.Equal(1, scope.SensitiveValueCount);
        Assert.Equal(2, scope.IgnoredValueCount);
        Assert.Equal("[REDACTED] remains scoped", result.Value);
    }

    [Fact]
    public void Oversized_too_many_null_and_disposed_values_fail_closed()
    {
        var limits = new RedactionLimits(maxSensitiveValues: 1, maxSensitiveValueCharacters: 3);
        using var oversized = EphemeralSecretMaterial.Create("four");
        using var extra = EphemeralSecretMaterial.Create("two");
        using var valid = EphemeralSecretMaterial.Create("one");
        using var oversizedScope = SensitiveRedactionScope.Create([oversized], limits);
        using var tooManyScope = SensitiveRedactionScope.Create([oversized, extra], limits);
        using var nullScope = SensitiveRedactionScope.Create([null!], limits);
        var disposed = EphemeralSecretMaterial.Create("one");
        disposed.Dispose();
        using var disposedScope = SensitiveRedactionScope.Create([disposed], limits);
        using var partiallyBuiltScope = SensitiveRedactionScope.Create([valid, oversized], new RedactionLimits(maxSensitiveValues: 2, maxSensitiveValueCharacters: 3));

        AssertAllScopeLimits(oversizedScope, tooManyScope, nullScope, disposedScope, partiallyBuiltScope);
    }

    [Fact]
    public void Input_output_and_work_bounds_return_bounded_fail_closed_markers()
    {
        using var material = EphemeralSecretMaterial.Create("abc");
        using var inputScope = SensitiveRedactionScope.Create([material], new RedactionLimits(maxInputCharacters: 3));
        using var outputScope = SensitiveRedactionScope.Create([material], new RedactionLimits(maxOutputCharacters: 2));
        using var workScope = SensitiveRedactionScope.Create([material], new RedactionLimits(maxWorkUnits: 1));

        var input = inputScope.RedactText("abcd");
        var output = outputScope.RedactText("abc");
        var work = workScope.RedactText("abd");

        Assert.Equal(RedactionStatus.InputLimitExceeded, input.Summary.Status);
        Assert.Equal(RedactionStatus.OutputLimitExceeded, output.Summary.Status);
        Assert.Equal(RedactionStatus.WorkLimitExceeded, work.Summary.Status);
        Assert.True(input.Value.Length <= inputScope.Limits.MaxOutputCharacters);
        Assert.True(output.Value.Length <= outputScope.Limits.MaxOutputCharacters);
        Assert.True(work.Value.Length <= workScope.Limits.MaxOutputCharacters);
    }

    [Fact]
    public void Malformed_unicode_is_processed_deterministically_without_splitting_or_throwing()
    {
        const string Canary = "\uD800x";
        using var material = EphemeralSecretMaterial.Create(Canary);
        using var scope = SensitiveRedactionScope.Create([material]);

        var first = scope.RedactText("before" + Canary + "after\uDC00");
        var second = scope.RedactText("before" + Canary + "after\uDC00");

        Assert.Equal(first, second);
        Assert.Equal("before[REDACTED]after\uDC00", first.Value);
    }

    [Fact]
    public void Marker_collisions_never_reintroduce_the_scoped_value()
    {
        using var material = EphemeralSecretMaterial.Create("REDACTED");
        using var scope = SensitiveRedactionScope.Create([material]);

        var result = scope.RedactText("REDACTED");

        Assert.DoesNotContain("REDACTED", result.Value, StringComparison.Ordinal);
        Assert.Equal(1, result.Summary.ReplacementCount);
    }

    [Fact]
    public void Multiple_marker_collisions_fall_back_to_an_empty_replacement()
    {
        using var bracket = EphemeralSecretMaterial.Create("[");
        using var star = EphemeralSecretMaterial.Create("*");
        using var scope = SensitiveRedactionScope.Create([bracket, star]);

        var result = scope.RedactText("[*");

        Assert.Empty(result.Value);
        Assert.Equal(2, result.Summary.ReplacementCount);
    }

    [Fact]
    public void Dispose_releases_owned_patterns_and_public_projections_are_value_free()
    {
        const string Canary = "canary secret";
        using var material = EphemeralSecretMaterial.Create(Canary);
        var scope = SensitiveRedactionScope.Create([material]);
        Assert.DoesNotContain(Canary, scope.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(scope), StringComparison.Ordinal);

        scope.Dispose();
        scope.Dispose();

        Assert.True(scope.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => scope.RedactText(Canary));
    }

    [Fact]
    public void Create_disposes_partially_built_patterns_when_source_enumeration_throws()
    {
        using var first = EphemeralSecretMaterial.Create("first-canary");
        var values = new ThrowingSensitiveMaterialList(first);

        var exception = Assert.Throws<InvalidOperationException>(() => SensitiveRedactionScope.Create(values));

        Assert.Equal("Hostile sensitive-value enumeration.", exception.Message);
        Assert.False(first.IsDisposed);
    }

    [Fact]
    public void Create_enforces_the_value_count_while_enumerating_an_inconsistent_list()
    {
        using var first = EphemeralSecretMaterial.Create("first-canary");
        using var second = EphemeralSecretMaterial.Create("second-canary");
        using var scope = SensitiveRedactionScope.Create(new InconsistentSensitiveMaterialList(first, second), new RedactionLimits(maxSensitiveValues: 1));

        var result = scope.RedactText("first-canary second-canary");

        Assert.False(scope.IsValid);
        Assert.Equal(RedactionStatus.ScopeLimitExceeded, result.Summary.Status);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void Randomized_value_order_preserves_deterministic_output_and_canary_removal()
    {
        var random = new Random(213);
        var values = Enumerable.Range(0, 12).Select(index => $"canary-{index:D2}-{(char)('a' + index)}").ToArray();
        var payload = string.Join('|', values.Concat(values.Select(Uri.EscapeDataString)));

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var shuffled = values.OrderBy(_ => random.Next()).ToArray();
            var created = CreateScope(shuffled);
            using var scope = created.Scope;
            DisposeAll(created.Materials);

            var result = scope.RedactText(payload);

            Assert.Equal(24, result.Summary.ReplacementCount);
            Assert.All(values, value => Assert.DoesNotContain(value, result.Value, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1)]
    [InlineData(65, 1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1, 1)]
    [InlineData(1, 4_097, 1, 1, 1)]
    [InlineData(1, 1, 0, 1, 1)]
    [InlineData(1, 1, 262_145, 1, 1)]
    [InlineData(1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 262_145, 1)]
    [InlineData(1, 1, 1, 1, 0)]
    [InlineData(1, 1, 1, 1, 8_000_001)]
    public void Limits_reject_unbounded_configuration(int values, int valueCharacters, int inputCharacters, int outputCharacters, int workUnits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedactionLimits(values, valueCharacters, inputCharacters, outputCharacters, workUnits));
    }

    private static (SensitiveRedactionScope Scope, EphemeralSecretMaterial[] Materials) CreateScope(IReadOnlyList<string> values)
    {
        var materials = values.Select(value => EphemeralSecretMaterial.Create(value)).ToArray();
        return (SensitiveRedactionScope.Create(materials), materials);
    }

    private static void DisposeAll(IEnumerable<EphemeralSecretMaterial> materials)
    {
        foreach (var material in materials)
        {
            material.Dispose();
        }
    }

    private static void AssertAllScopeLimits(params SensitiveRedactionScope[] scopes)
    {
        foreach (var scope in scopes)
        {
            var result = scope.RedactText("four");
            Assert.False(scope.IsValid);
            Assert.Equal(RedactionStatus.ScopeLimitExceeded, result.Summary.Status);
            Assert.Empty(result.Value);
        }
    }

    private sealed class ThrowingSensitiveMaterialList(EphemeralSecretMaterial first) : IReadOnlyList<EphemeralSecretMaterial>
    {
        public int Count => 2;

        public EphemeralSecretMaterial this[int index] => index == 0 ? first : throw new InvalidOperationException("Hostile sensitive-value index access.");

        public IEnumerator<EphemeralSecretMaterial> GetEnumerator()
        {
            yield return first;
            throw new InvalidOperationException("Hostile sensitive-value enumeration.");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class InconsistentSensitiveMaterialList(params EphemeralSecretMaterial[] values) : IReadOnlyList<EphemeralSecretMaterial>
    {
        public int Count => 1;

        public EphemeralSecretMaterial this[int index] => values[index];

        public IEnumerator<EphemeralSecretMaterial> GetEnumerator()
        {
            return ((IEnumerable<EphemeralSecretMaterial>)values).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
