using System.Text;
using System.Text.Json;
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
        Assert.True(HumanInputPolicyReference.TryParse("task-one@revision-one", out _));
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
    public void Workspace_scope_uses_only_the_canonical_runtime_identity()
    {
        var invalidWorkspaceIds = new[]
        {
            "workspace-one",
            "workspace-sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "workspace-sha256:" + new string('a', 63),
            "workspace-sha256:" + new string('a', 65),
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:extra",
            "workspace-sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        };

        foreach (var workspaceId in invalidWorkspaceIds)
        {
            var policy = HumanInputPolicyArtifactHash.Apply(Timeout() with { WorkspaceId = workspaceId });
            Assert.Contains(HumanInputPolicyArtifactValidator.Validate(policy).Errors, error => error.Code == "invalid_workspace_id" && error.Path == "$.workspaceId");
            Assert.Null(HumanInputPolicyResolutionSnapshot.TryCreate(workspaceId, "graph-one", "revision-one", "node-one", "actor-one", policy, Failure(), At));
        }
    }

    [Fact]
    public void Shared_text_safety_rejects_secret_markers_in_policy_ids_references_and_artifacts()
    {
        var timeout = Timeout();
        var unsafePolicyIds = new[] { "api_key-one", "authorization-one", "ghp_fake", "github_pat_fake", "xoxb-fake", "sk-fake", "access_token-one", "private_key-one" };

        Assert.All(unsafePolicyIds, policyId =>
        {
            Assert.False(HumanInputPolicyReference.TryParse(policyId + "@revision-one", out _));
            Assert.False(HumanInputPolicyArtifactValidator.Validate(HumanInputPolicyArtifactHash.Apply(timeout with { PolicyId = policyId })).IsValid);
        });
    }

    [Fact]
    public void Equivalent_noncanonical_json_bytes_are_rejected_before_artifact_admission()
    {
        var canonical = HumanInputPolicyArtifactJson.Serialize(Timeout());
        var text = Encoding.UTF8.GetString(canonical);
        var variants = new[]
        {
            Encoding.UTF8.GetBytes(" " + text),
            Encoding.UTF8.GetBytes(text.Replace("\"timeout-one\"", "\"time\\u006fut-one\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(text.Replace("\"responseWindowMilliseconds\":3600000", "\"responseWindowMilliseconds\":3600000.0", StringComparison.Ordinal)),
            ReverseProperties(canonical)
        };

        Assert.All(variants, variant => Assert.Throws<FormatException>(() => HumanInputPolicyArtifactJson.Deserialize(variant)));
    }

    [Fact]
    public void Trusted_snapshot_binds_exact_scope_policies_and_overflow_safe_finite_window()
    {
        var snapshot = HumanInputPolicyResolutionSnapshot.TryCreate("workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "graph-one", "revision-one", "node-one", "actor-one", Timeout(), Failure(), At);
        var overflow = HumanInputPolicyResolutionSnapshot.TryCreate("workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "graph-one", "revision-one", "node-one", "actor-one", Timeout(TimeSpan.FromDays(1)), Failure(), DateTimeOffset.MaxValue);

        Assert.NotNull(snapshot);
        Assert.True(HumanInputPolicyResolutionSnapshot.IsValid(snapshot));
        Assert.Equal(At.AddHours(1), snapshot!.ExpiresAtUtc);
        Assert.False(HumanInputPolicyResolutionSnapshot.IsValid(snapshot with { GraphId = "graph-two" }));
        Assert.Null(overflow);
    }

    internal static readonly DateTimeOffset At = new(2026, 8, 26, 15, 0, 0, TimeSpan.Zero);

    internal static HumanInputPolicyArtifact Timeout(TimeSpan? window = null)
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "timeout-one", "revision-one", HumanInputPolicyKind.ResponseWindow, "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "graph-one", "actor-one", (long)(window ?? TimeSpan.FromHours(1)).TotalMilliseconds, HumanInputTerminalDisposition.Unknown, string.Empty));

    internal static HumanInputPolicyArtifact Failure()
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "failure-one", "revision-one", HumanInputPolicyKind.DeadlineDisposition, "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "graph-one", "actor-one", null, HumanInputTerminalDisposition.Expired, string.Empty));

    private static byte[] ReverseProperties(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject().Reverse())
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
