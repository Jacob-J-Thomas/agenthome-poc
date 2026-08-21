using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Tests.Credentials;

public sealed class CredentialLeaseContractTests
{
    private static readonly DateTimeOffset _issued = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Intent_enforces_exact_earliest_deadline_and_sixty_second_cap()
    {
        var deadlines = Deadlines(_issued.AddSeconds(75));
        Assert.Equal(_issued.AddSeconds(60), CredentialLeaseContract.ComputeEffectiveExpiry(_issued, deadlines));

        var earlier = deadlines with { GrantExpiresAtUtc = _issued.AddSeconds(25) };
        Assert.Equal(_issued.AddSeconds(25), CredentialLeaseContract.ComputeEffectiveExpiry(_issued, earlier));

        var intent = Intent(earlier);
        Assert.Null(CredentialLeaseContract.Validate(intent));
        Assert.StartsWith("sha256:", intent.ContentHash, StringComparison.Ordinal);
        Assert.Equal(_issued.AddSeconds(25), intent.EffectiveExpiresAtUtc);

        var changed = intent with { EffectiveExpiresAtUtc = intent.EffectiveExpiresAtUtc.AddTicks(1) };
        Assert.Equal("credential-lease-intent-expiry-invalid", CredentialLeaseContract.Validate(changed));
    }

    [Fact]
    public void Default_or_non_utc_timestamps_fail_closed()
    {
        var intent = Intent();

        Assert.Equal("credential-lease-intent-time-invalid", CredentialLeaseContract.Validate(intent with { IssuedAtUtc = default }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CredentialLeaseContract.ComputeEffectiveExpiry(default, intent.Deadlines));
        Assert.Throws<ArgumentOutOfRangeException>(() => CredentialLeaseContract.Prepare(intent, default));
    }

    [Fact]
    public void Phase_chain_is_direct_immutable_and_closed()
    {
        var intent = Intent();
        var prepared = CredentialLeaseContract.Prepare(intent, _issued);
        var authorized = CredentialLeaseContract.Advance(intent, prepared, CredentialLeasePhase.Authorized, _issued.AddSeconds(1), Hash('a'), Hash('b'));
        var boundary = CredentialLeaseContract.Advance(intent, authorized, CredentialLeasePhase.RedemptionBoundaryReached, _issued.AddSeconds(2));
        var redeemed = CredentialLeaseContract.Advance(intent, boundary, CredentialLeasePhase.Redeemed, _issued.AddSeconds(3));
        var history = CredentialLeaseContract.CreateHistory(intent, [prepared, authorized, boundary, redeemed]);

        Assert.Null(CredentialLeaseContract.Validate(history));
        Assert.Equal(CredentialLeaseOutcome.Redeemed, history.Current.Outcome);
        Assert.Throws<InvalidOperationException>(() => CredentialLeaseContract.Advance(intent, redeemed, CredentialLeasePhase.RedemptionAmbiguous, _issued.AddSeconds(4), failureCode: CredentialFailureCode.OutcomeUncertain));

        var changedIntent = intent with { CredentialUseGeneration = 2 };
        Assert.Equal("credential-lease-intent-hash-invalid", CredentialLeaseContract.Validate(changedIntent));
        var disconnected = new CredentialLeaseAttemptHistory(
            history.SchemaVersion,
            history.Intent,
            history.Versions.Select((item, index) => index == 2 ? item with { PreviousContentHash = Hash('c') } : item).ToArray());
        Assert.NotNull(CredentialLeaseContract.Validate(disconnected));
    }

    [Fact]
    public void Evidence_identity_is_exactly_scoped_to_operation_and_generation()
    {
        var first = CredentialLeaseContract.ComputeEvidenceId("credential-use-1", 1);
        var replay = CredentialLeaseContract.ComputeEvidenceId("credential-use-1", 1);
        var nextGeneration = CredentialLeaseContract.ComputeEvidenceId("credential-use-1", 2);

        Assert.Equal(first, replay);
        Assert.NotEqual(first, nextGeneration);
        Assert.StartsWith("credential-evidence-", first.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CredentialLeasePhase.IntentPrepared, CredentialLeasePhase.Redeemed)]
    [InlineData(CredentialLeasePhase.Authorized, CredentialLeasePhase.RedemptionFailed)]
    [InlineData(CredentialLeasePhase.RedemptionBoundaryReached, CredentialLeasePhase.NotRedeemed)]
    public void Illegal_phase_shortcuts_fail_closed(CredentialLeasePhase from, CredentialLeasePhase to)
    {
        var intent = Intent();
        var current = CredentialLeaseContract.Prepare(intent, _issued);
        if (from == CredentialLeasePhase.Authorized)
        {
            current = CredentialLeaseContract.Advance(intent, current, from, _issued.AddSeconds(1), Hash('a'), Hash('b'));
        }
        else if (from == CredentialLeasePhase.RedemptionBoundaryReached)
        {
            var authorized = CredentialLeaseContract.Advance(intent, current, CredentialLeasePhase.Authorized, _issued.AddSeconds(1), Hash('a'), Hash('b'));
            current = CredentialLeaseContract.Advance(intent, authorized, from, _issued.AddSeconds(2));
        }

        Assert.Throws<InvalidOperationException>(() => CredentialLeaseContract.Advance(intent, current, to, _issued.AddSeconds(3), failureCode: CredentialFailureCode.OutcomeUncertain));
    }

    [Fact]
    public void Redemption_boundary_requires_trusted_time_strictly_before_expiry()
    {
        var intent = Intent(Deadlines(_issued.AddSeconds(20)));
        var prepared = CredentialLeaseContract.Prepare(intent, _issued);
        var authorized = CredentialLeaseContract.Advance(intent, prepared, CredentialLeasePhase.Authorized, _issued.AddSeconds(1), Hash('a'), Hash('b'));

        Assert.Throws<ArgumentOutOfRangeException>(() => CredentialLeaseContract.Prepare(intent, intent.EffectiveExpiresAtUtc));
        Assert.Throws<InvalidOperationException>(() => CredentialLeaseContract.Advance(intent, prepared, CredentialLeasePhase.Authorized, intent.EffectiveExpiresAtUtc, Hash('a'), Hash('b')));
        Assert.NotNull(CredentialLeaseContract.Advance(intent, authorized, CredentialLeasePhase.RedemptionBoundaryReached, intent.EffectiveExpiresAtUtc.AddTicks(-1)));
        Assert.Throws<InvalidOperationException>(() => CredentialLeaseContract.Advance(intent, authorized, CredentialLeasePhase.RedemptionBoundaryReached, intent.EffectiveExpiresAtUtc));
        Assert.Throws<InvalidOperationException>(() => CredentialLeaseContract.Advance(intent, authorized, CredentialLeasePhase.RedemptionBoundaryReached, intent.EffectiveExpiresAtUtc.AddTicks(1)));
    }

    [Fact]
    public void Pre_authority_denial_can_close_not_redeemed_without_invented_evidence()
    {
        var intent = Intent();
        var prepared = CredentialLeaseContract.Prepare(intent, _issued);
        var denied = CredentialLeaseContract.Advance(intent, prepared, CredentialLeasePhase.NotRedeemed, _issued.AddSeconds(1), failureCode: CredentialFailureCode.Unauthorized);

        Assert.Equal(CredentialLeaseOutcome.NotRedeemed, denied.Outcome);
        Assert.Null(denied.CurrentAuthorityEvidenceHash);
        Assert.Null(denied.RegistryEvidenceHash);
    }

    [Fact]
    public void Exact_canonical_codec_rejects_unknown_fields_and_defensively_snapshots_versions()
    {
        var intent = Intent();
        var versions = new List<CredentialLeaseAttemptVersion> { CredentialLeaseContract.Prepare(intent, _issued) };
        var history = CredentialLeaseContract.CreateHistory(intent, versions);
        versions.Clear();

        var encoded = CredentialLeaseAttemptRecordCodec.Encode(history);
        Assert.True(CredentialLeaseAttemptRecordCodec.TryDecode(encoded, out var decoded, out var reason), reason);
        Assert.Equal(history.Intent, decoded!.Intent);
        Assert.Equal(history.Versions, decoded.Versions);
        Assert.Single(decoded!.Versions);

        var json = Encoding.UTF8.GetString(encoded);
        var unknown = Encoding.UTF8.GetBytes(json[..^1] + ",\"renewalToken\":\"forbidden\"}");
        Assert.False(CredentialLeaseAttemptRecordCodec.TryDecode(unknown, out _, out _));
    }

    [Fact]
    public void Persisted_lease_shape_is_value_free_and_has_no_bearer_or_renewal_surface()
    {
        var intent = Intent();
        var history = CredentialLeaseContract.CreateHistory(intent, [CredentialLeaseContract.Prepare(intent, _issued)]);
        using var document = JsonDocument.Parse(CredentialLeaseAttemptRecordCodec.Encode(history));
        var propertyNames = EnumeratePropertyNames(document.RootElement)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase) && !name.Contains("Requirement", StringComparison.Ordinal));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Renew", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name is "GetLease" or "ReadSecret" or "GetSecret");
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static CredentialLeaseIntent Intent(CredentialLeaseDeadlines? deadlines = null)
    {
        deadlines ??= Deadlines(_issued.AddMinutes(5));
        var candidate = new CredentialLeaseIntent(
            CredentialLeaseIntent.CurrentSchemaVersion,
            "lease-1",
            "credential-use-1",
            1,
            new CredentialLeaseExecutionScope(
                "workspace-1",
                "actor-1",
                Hash('1'),
                Hash('2'),
                Hash('3'),
                "run-1",
                "graph-1",
                "revision-1",
                Hash('4'),
                1,
                "role-1",
                1,
                Hash('5'),
                "loop-1",
                "loop-revision-1",
                1,
                Hash('6')),
            new CredentialLeaseAuthorityScope("proof-1", Hash('0'), "authority-1", 1, Hash('7'), "grant-1", 1, Hash('8'), Hash('9'), Hash('a'), null),
            new CredentialLeaseEffectScope("node-1", 1, "effect-1", "effect-operation-1", "idempotency-1", 1, Hash('b'), 5),
            new CredentialLeaseCapabilityScope("com.example/capability", "1.0.0", Hash('c'), "com.example", "adapter/use", "api-key"),
            new CredentialLeaseProfileScope(CredentialLeaseProfileApplicability.NotApplicable, null, null),
            new CredentialLeaseRegistryScope("reference-1", Hash('d'), 1, "consent-1", "com.example"),
            new CredentialLeaseTargetScope("service", CredentialLeaseContract.ComputeTargetFingerprint("service", "opaque-server-target"u8), "invoke", "perform governed operation"),
            _issued,
            deadlines,
            CredentialLeaseContract.ComputeEffectiveExpiry(_issued, deadlines),
            string.Empty);
        return CredentialLeaseContract.ApplyIntentHash(candidate);
    }

    private static CredentialLeaseDeadlines Deadlines(DateTimeOffset proofExpiry) => new(proofExpiry, null, null, null, null, null, null, null);
    private static string Hash(char character) => "sha256:" + new string(character, 64);
}
