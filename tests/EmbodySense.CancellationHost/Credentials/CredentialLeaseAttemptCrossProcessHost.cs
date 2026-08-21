using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Credentials.Leases;

namespace EmbodySense.CancellationHost.Credentials;

internal static class CredentialLeaseAttemptCrossProcessHost
{
    private static readonly DateTimeOffset _issuedAtUtc = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<int> RunAsync(string phase, string workspaceRoot)
    {
        if (phase is not ("prepared" or "authorized" or "boundary" or "redeemed"))
        {
            return 2;
        }

        var intent = Intent();
        var store = new CredentialLeaseAttemptStore(new WorkspacePaths(workspaceRoot));
        var prepared = CredentialLeaseContract.Prepare(intent, _issuedAtUtc);
        var begun = await store.BeginAsync(intent, prepared);
        if (begun.Status != EmbodySense.Core.Application.Credentials.Leases.Models.CredentialLeaseAttemptStoreStatus.Created
            || begun.History is null
            || begun.Lease is null)
        {
            return 3;
        }

        using var owner = begun.Lease;
        var history = begun.History;
        if (phase is "authorized" or "boundary" or "redeemed")
        {
            history = await AdvanceAsync(store, owner, history, CredentialLeasePhase.Authorized, _issuedAtUtc.AddSeconds(1), Hash('a'), Hash('b'));
        }
        if (phase is "boundary" or "redeemed")
        {
            history = await AdvanceAsync(store, owner, history, CredentialLeasePhase.RedemptionBoundaryReached, _issuedAtUtc.AddSeconds(2));
        }
        if (phase == "redeemed")
        {
            history = await AdvanceAsync(store, owner, history, CredentialLeasePhase.Redeemed, _issuedAtUtc.AddSeconds(3));
        }

        Console.WriteLine("ready");
        await Console.Out.FlushAsync();
        _ = await Console.In.ReadLineAsync();
        return 4;
    }

    private static async Task<CredentialLeaseAttemptHistory> AdvanceAsync(
        CredentialLeaseAttemptStore store,
        EmbodySense.Core.Application.Credentials.Leases.ICredentialLeaseAttemptLease owner,
        CredentialLeaseAttemptHistory history,
        CredentialLeasePhase phase,
        DateTimeOffset recordedAtUtc,
        string? authorityHash = null,
        string? registryHash = null)
    {
        var version = CredentialLeaseContract.Advance(history.Intent, history.Current, phase, recordedAtUtc, authorityHash, registryHash);
        var replacement = CredentialLeaseContract.CreateHistory(history.Intent, [.. history.Versions, version]);
        var result = await store.CompareExchangeAsync(history.Current.ContentHash, replacement, owner);
        return result.History ?? throw new InvalidOperationException("The credential lease phase was not durably committed.");
    }

    private static CredentialLeaseIntent Intent()
    {
        var deadlines = new CredentialLeaseDeadlines(_issuedAtUtc.AddMinutes(1), null, null, null, null, null, null, null);
        var candidate = new CredentialLeaseIntent(
            CredentialLeaseIntent.CurrentSchemaVersion,
            "lease-cross-process-1",
            "credential-use-cross-process-1",
            1,
            new CredentialLeaseExecutionScope("workspace-1", "actor-1", Hash('1'), Hash('2'), Hash('3'), "run-1", "graph-1", "revision-1", Hash('4'), 1, "role-1", 1, Hash('5'), "loop-1", "loop-revision-1", 1, Hash('6')),
            new CredentialLeaseAuthorityScope("proof-1", Hash('0'), "authority-1", 1, Hash('7'), "grant-1", 1, Hash('8'), Hash('9'), Hash('a'), null),
            new CredentialLeaseEffectScope("node-1", 1, "effect-1", "effect-operation-1", "idempotency-1", 1, Hash('b'), 5),
            new CredentialLeaseCapabilityScope("com.example/capability", "1.0.0", Hash('c'), "com.example", "adapter/use", "api-key"),
            new CredentialLeaseProfileScope(CredentialLeaseProfileApplicability.NotApplicable, null, null),
            new CredentialLeaseRegistryScope("reference-1", Hash('d'), 1, "consent-1", "com.example"),
            new CredentialLeaseTargetScope("service", CredentialLeaseContract.ComputeTargetFingerprint("service", "opaque-server-target"u8), "invoke", "perform governed operation"),
            _issuedAtUtc,
            deadlines,
            CredentialLeaseContract.ComputeEffectiveExpiry(_issuedAtUtc, deadlines),
            string.Empty);
        return CredentialLeaseContract.ApplyIntentHash(candidate);
    }

    private static string Hash(char value) => "sha256:" + new string(value, 64);
}
