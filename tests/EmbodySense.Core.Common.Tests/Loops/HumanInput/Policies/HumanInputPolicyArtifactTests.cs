using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

namespace EmbodySense.Core.Common.Tests.Loops.HumanInput.Policies;

public sealed class HumanInputPolicyArtifactTests
{
    [Fact]
    public void Exact_versioned_policy_artifacts_are_hash_bound_deeply_detached_and_canonical_json_round_trips()
    {
        var timeout = Timeout();
        var json = HumanInputPolicyArtifactJson.Serialize(timeout);
        var restored = HumanInputPolicyArtifactJson.Deserialize(json);

        Assert.True(HumanInputPolicyReference.TryParse("timeout-one@revision-one", out var reference));
        Assert.Equal(timeout.Reference, reference);
        Assert.False(HumanInputPolicyReference.TryParse("timeout-one", out _));
        Assert.False(HumanInputPolicyReference.TryParse("default@revision-one", out _));
        Assert.True(HumanInputPolicyArtifactValidator.Validate(timeout).IsValid);
        Assert.Equal(timeout, restored);
        Assert.Equal(json, HumanInputPolicyArtifactJson.Serialize(restored));
        Assert.False(HumanInputPolicyArtifactValidator.Validate(timeout with { ResponseWindowMilliseconds = 0 }).IsValid);
        Assert.False(HumanInputPolicyArtifactValidator.Validate(timeout with { ContentHash = new string('a', 64) }).IsValid);
    }

    [Fact]
    public void Wrong_kind_secret_unbounded_unknown_and_noncanonical_artifacts_fail_closed()
    {
        var timeout = Timeout();
        var variants = new[]
        {
            timeout with { PolicyId = "secret-one" },
            timeout with { Kind = HumanInputPolicyKind.Unknown },
            timeout with { TerminalDisposition = HumanInputTerminalDisposition.Expired },
            timeout with { ResponseWindowMilliseconds = (long)TimeSpan.FromDays(31).TotalMilliseconds },
            Failure() with { ResponseWindowMilliseconds = 1 },
            Failure() with { TerminalDisposition = HumanInputTerminalDisposition.Unknown },
        };

        Assert.All(variants, value => Assert.False(HumanInputPolicyArtifactValidator.Validate(HumanInputPolicyArtifactHash.Apply(value)).IsValid));
        Assert.Throws<FormatException>(() => HumanInputPolicyArtifactJson.Deserialize("{\"schemaVersion\":1}"u8));
    }

    [Fact]
    public void Trusted_snapshot_binds_exact_scope_policies_and_overflow_safe_finite_window()
    {
        var snapshot = HumanInputPolicyResolutionSnapshot.TryCreate("workspace-one", "graph-one", "revision-one", "node-one", "actor-one", Timeout(), Failure(), At);
        var overflow = HumanInputPolicyResolutionSnapshot.TryCreate("workspace-one", "graph-one", "revision-one", "node-one", "actor-one", Timeout(TimeSpan.FromDays(1)), Failure(), DateTimeOffset.MaxValue);

        Assert.NotNull(snapshot);
        Assert.True(HumanInputPolicyResolutionSnapshot.IsValid(snapshot));
        Assert.Equal(At.AddHours(1), snapshot!.ExpiresAtUtc);
        Assert.False(HumanInputPolicyResolutionSnapshot.IsValid(snapshot with { GraphId = "graph-two" }));
        Assert.Null(overflow);
    }

    internal static readonly DateTimeOffset At = new(2026, 8, 26, 15, 0, 0, TimeSpan.Zero);

    internal static HumanInputPolicyArtifact Timeout(TimeSpan? window = null)
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "timeout-one", "revision-one", HumanInputPolicyKind.ResponseWindow, "workspace-one", "graph-one", "actor-one", (long)(window ?? TimeSpan.FromHours(1)).TotalMilliseconds, HumanInputTerminalDisposition.Unknown, string.Empty));

    internal static HumanInputPolicyArtifact Failure()
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "failure-one", "revision-one", HumanInputPolicyKind.DeadlineDisposition, "workspace-one", "graph-one", "actor-one", null, HumanInputTerminalDisposition.Expired, string.Empty));
}
