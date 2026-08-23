using System.Text.Json;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Common.Tests.LocalWorkspace.Actions;

public sealed class WorkspaceActionInputContractTests
{
    [Theory]
    [InlineData(WorkspaceActionKind.Append, "expectedAbsent")]
    [InlineData(WorkspaceActionKind.Write, "expectedContentHash")]
    [InlineData(WorkspaceActionKind.Write, "expectedGovernedVersion")]
    [InlineData(WorkspaceActionKind.Delete, "expectedContentHash")]
    public void Closed_inputs_round_trip_deterministically(WorkspaceActionKind kind, string preconditionKind)
    {
        var json = Json(kind, Precondition(preconditionKind), kind == WorkspaceActionKind.Delete ? "[]" : "[{\"kind\":\"literalUtf8\",\"literal\":\"alpha\\n\"}]");

        Assert.True(WorkspaceActionInputContract.TryParse(json, kind, out var input, out var reason), reason);
        var encoded = WorkspaceActionInputContract.Encode(input!);
        Assert.True(WorkspaceActionInputContract.TryParse(encoded, kind, out var replay, out reason), reason);
        Assert.Equal(encoded, WorkspaceActionInputContract.Encode(replay!));
        Assert.Equal(WorkspaceActionOperationIds.For(kind), WorkspaceActionOperationIds.For(replay!.Kind));
        Assert.True(WorkspaceActionFingerprint.IsCanonicalSha256(WorkspaceActionInputContract.ComputePreconditionHash(replay.Precondition)));
    }

    [Fact]
    public void Literal_materialization_is_exact_strict_utf8_without_newline_conversion()
    {
        var json = Json(WorkspaceActionKind.Append, Precondition("expectedAbsent"), "[{\"kind\":\"literalUtf8\",\"literal\":\"a\\r\\n\"},{\"kind\":\"literalUtf8\",\"literal\":\"\ud83d\ude00\"}]");
        Assert.True(WorkspaceActionInputContract.TryParse(json, WorkspaceActionKind.Append, out var input, out _));

        Assert.Equal([0x61, 0x0d, 0x0a, 0xf0, 0x9f, 0x98, 0x80], WorkspaceActionInputContract.MaterializeLiteralBytes(input!));
    }

    [Fact]
    public void Credential_reference_is_value_free_and_requires_later_shared_bridge()
    {
        var json = Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[{\"credentialReferenceId\":\"credential-ref-1\",\"kind\":\"credentialReference\"}]");
        Assert.True(WorkspaceActionInputContract.TryParse(json, WorkspaceActionKind.Write, out var input, out _));
        Assert.True(WorkspaceActionInputContract.RequiresCredentialBridge(input!));
        Assert.Throws<InvalidOperationException>(() => WorkspaceActionInputContract.MaterializeLiteralBytes(input!));
        Assert.DoesNotContain("secret", WorkspaceActionInputContract.Encode(input!), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(WorkspaceActionKind.Delete, "expectedAbsent", "[]")]
    [InlineData(WorkspaceActionKind.Delete, "expectedContentHash", "[{\"kind\":\"literalUtf8\",\"literal\":\"x\"}]")]
    [InlineData(WorkspaceActionKind.Append, "expectedAbsent", "[]")]
    public void Operation_specific_shapes_fail_closed(WorkspaceActionKind kind, string preconditionKind, string segments)
    {
        Assert.False(WorkspaceActionInputContract.TryParse(Json(kind, Precondition(preconditionKind), segments), kind, out _, out _));
    }

    [Fact]
    public void Exact_precondition_union_rejects_extra_missing_or_malformed_evidence()
    {
        var malformed = new[]
        {
            "{\"kind\":\"expectedAbsent\",\"expectedContentHash\":\"" + Hash('a') + "\"}",
            "{\"kind\":\"expectedContentHash\"}",
            "{\"kind\":\"expectedContentHash\",\"expectedContentHash\":\"UPPER\"}",
            "{\"expectedGovernedVersion\":1,\"kind\":\"expectedGovernedVersion\",\"priorAfterEvidenceHash\":\"" + Hash('b') + "\"}",
            "{\"expectedGovernedVersion\":0,\"kind\":\"expectedGovernedVersion\",\"priorAfterEvidenceHash\":\"" + Hash('b') + "\",\"priorAfterEvidenceId\":\"after-1\"}",
        };
        foreach (var precondition in malformed)
        {
            Assert.False(WorkspaceActionInputContract.TryParse(Json(WorkspaceActionKind.Write, precondition, "[{\"kind\":\"literalUtf8\",\"literal\":\"x\"}]"), WorkspaceActionKind.Write, out _, out _));
        }
    }

    [Fact]
    public void Segment_count_and_literal_byte_bounds_accept_maximum_and_reject_max_plus_one()
    {
        var maximumSegments = string.Join(',', Enumerable.Repeat("{\"kind\":\"literalUtf8\",\"literal\":\"\"}", WorkspaceActionContractLimits.MaxContentSegments));
        Assert.True(WorkspaceActionInputContract.TryParse(Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[" + maximumSegments + "]"), WorkspaceActionKind.Write, out _, out _));
        var tooMany = maximumSegments + ",{\"kind\":\"literalUtf8\",\"literal\":\"\"}";
        Assert.False(WorkspaceActionInputContract.TryParse(Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[" + tooMany + "]"), WorkspaceActionKind.Write, out _, out _));

        var first = JsonSerializer.Serialize(new string('x', WorkspaceActionContractLimits.MaxLiteralCharacters));
        var tooLongLiteral = JsonSerializer.Serialize(new string('x', WorkspaceActionContractLimits.MaxLiteralCharacters + 1));
        var remainder = JsonSerializer.Serialize(new string('x', WorkspaceActionContractLimits.MaxLiteralUtf8Bytes - WorkspaceActionContractLimits.MaxLiteralCharacters));
        var overRemainder = JsonSerializer.Serialize(new string('x', WorkspaceActionContractLimits.MaxLiteralUtf8Bytes - WorkspaceActionContractLimits.MaxLiteralCharacters + 1));
        Assert.True(WorkspaceActionInputContract.TryParse(Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[{\"kind\":\"literalUtf8\",\"literal\":" + first + "},{\"kind\":\"literalUtf8\",\"literal\":" + remainder + "}]"), WorkspaceActionKind.Write, out _, out _));
        Assert.False(WorkspaceActionInputContract.TryParse(Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[{\"kind\":\"literalUtf8\",\"literal\":" + tooLongLiteral + "}]"), WorkspaceActionKind.Write, out _, out _));
        Assert.False(WorkspaceActionInputContract.TryParse(Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[{\"kind\":\"literalUtf8\",\"literal\":" + first + "},{\"kind\":\"literalUtf8\",\"literal\":" + overRemainder + "}]"), WorkspaceActionKind.Write, out _, out _));
    }

    [Fact]
    public void Credential_reference_count_accepts_maximum_and_rejects_max_plus_one()
    {
        var maximum = string.Join(',', Enumerable.Range(1, WorkspaceActionContractLimits.MaxCredentialReferences)
            .Select(index => $"{{\"credentialReferenceId\":\"credential-{index}\",\"kind\":\"credentialReference\"}}"));

        Assert.True(WorkspaceActionInputContract.TryParse(
            Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[" + maximum + "]"),
            WorkspaceActionKind.Write,
            out _,
            out _));
        Assert.False(WorkspaceActionInputContract.TryParse(
            Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[" + maximum + ",{" +
                "\"credentialReferenceId\":\"credential-over\",\"kind\":\"credentialReference\"}]"),
            WorkspaceActionKind.Write,
            out _,
            out _));
    }

    [Fact]
    public void Malformed_unicode_duplicate_properties_and_unknown_fields_are_rejected()
    {
        var invalidSurrogate = Json(WorkspaceActionKind.Write, Precondition("expectedAbsent"), "[{\"kind\":\"literalUtf8\",\"literal\":\"\\ud800\"}]");
        var duplicate = "{\"precondition\":" + Precondition("expectedAbsent") + ",\"schemaVersion\":1,\"scopeId\":\"workspace\",\"scopeId\":\"other\",\"segments\":[{\"kind\":\"literalUtf8\",\"literal\":\"x\"}],\"target\":\"a.txt\"}";
        var unknown = "{\"extra\":true,\"precondition\":" + Precondition("expectedAbsent") + ",\"schemaVersion\":1,\"scopeId\":\"workspace\",\"segments\":[{\"kind\":\"literalUtf8\",\"literal\":\"x\"}],\"target\":\"a.txt\"}";

        Assert.False(WorkspaceActionInputContract.TryParse(invalidSurrogate, WorkspaceActionKind.Write, out _, out _));
        Assert.False(WorkspaceActionInputContract.TryParse(duplicate, WorkspaceActionKind.Write, out _, out _));
        Assert.False(WorkspaceActionInputContract.TryParse(unknown, WorkspaceActionKind.Write, out _, out _));
    }

    private static string Json(WorkspaceActionKind kind, string precondition, string segments)
    {
        _ = kind;
        return "{\"precondition\":" + precondition + ",\"schemaVersion\":1,\"scopeId\":\"workspace\",\"segments\":" + segments + ",\"target\":\"notes/file.txt\"}";
    }

    private static string Precondition(string kind)
        => kind switch
        {
            "expectedAbsent" => "{\"kind\":\"expectedAbsent\"}",
            "expectedContentHash" => "{\"expectedContentHash\":\"" + Hash('a') + "\",\"kind\":\"expectedContentHash\"}",
            "expectedGovernedVersion" => "{\"expectedGovernedVersion\":1,\"kind\":\"expectedGovernedVersion\",\"priorAfterEvidenceHash\":\"" + Hash('b') + "\",\"priorAfterEvidenceId\":\"after-alpha\"}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string Hash(char value) => new(value, 64);
}
