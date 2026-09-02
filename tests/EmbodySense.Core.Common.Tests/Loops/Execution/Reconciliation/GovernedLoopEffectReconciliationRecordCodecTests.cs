using System.Text;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationRecordCodecTests
{
    [Fact]
    public void Canonical_compact_schema_one_record_round_trips()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied, includeResolution: true);
        var encoded = GovernedLoopEffectReconciliationRecordCodec.Encode(valid);

        Assert.True(GovernedLoopEffectReconciliationRecordCodec.TryDecode(encoded, out var decoded, out var reason), reason);
        Assert.Equal(encoded, GovernedLoopEffectReconciliationRecordCodec.Encode(decoded!));
        Assert.DoesNotContain("\n", Encoding.UTF8.GetString(encoded), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_duplicate_missing_reordered_and_numeric_enum_json_are_rejected()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied, includeResolution: true);
        var json = Encoding.UTF8.GetString(GovernedLoopEffectReconciliationRecordCodec.Encode(valid));
        var unknown = json[..^1] + ",\"unknown\":true}";
        var duplicate = json.Insert(1, "\"schemaVersion\":1,");
        var missing = json.Replace("\"schemaVersion\":1,", string.Empty, StringComparison.Ordinal);
        var withoutSchema = json.Remove(1, "\"schemaVersion\":1,".Length);
        var insertion = withoutSchema.IndexOf(",\"caseVersion\"", StringComparison.Ordinal) + 1;
        var reordered = withoutSchema.Insert(insertion, "\"schemaVersion\":1,");
        var nonCanonicalWhitespace = " " + json;
        var numericEnum = json.Replace("\"kind\":\"authoritative\"", "\"kind\":1", StringComparison.Ordinal);

        Assert.False(Decode(unknown));
        Assert.False(Decode(duplicate));
        Assert.False(Decode(missing));
        Assert.False(Decode(reordered));
        Assert.False(Decode(nonCanonicalWhitespace));
        Assert.False(Decode(numericEnum));
    }

    [Fact]
    public void Wrong_schema_tampered_hash_depth_and_size_are_rejected()
    {
        var valid = GovernedLoopEffectReconciliationTestFixture.Case(GovernedLoopEffectReconciliationAssessmentKind.Inconclusive);
        var json = Encoding.UTF8.GetString(GovernedLoopEffectReconciliationRecordCodec.Encode(valid));
        var wrongSchema = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
        var tamperedHash = json.Replace(valid.Binding.CurrentAttemptHash, GovernedLoopEffectReconciliationTestFixture.Hash('0'), StringComparison.Ordinal);
        var tooDeep = new string('[', GovernedLoopEffectReconciliationContractLimits.MaxJsonDepth + 1) + new string(']', GovernedLoopEffectReconciliationContractLimits.MaxJsonDepth + 1);
        var oversized = new byte[GovernedLoopEffectReconciliationContractLimits.MaxRecordUtf8Bytes + 1];

        Assert.False(Decode(wrongSchema));
        Assert.False(Decode(tamperedHash));
        Assert.False(Decode(tooDeep));
        Assert.False(GovernedLoopEffectReconciliationRecordCodec.TryDecode(oversized, out _, out _));
    }

    private static bool Decode(string json)
        => GovernedLoopEffectReconciliationRecordCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _, out _);
}
