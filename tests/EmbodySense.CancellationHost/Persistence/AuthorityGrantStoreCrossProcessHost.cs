using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.CancellationHost.Persistence;

internal static class AuthorityGrantStoreCrossProcessHost
{
    private static readonly DateTimeOffset _recordedAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<int> RunAsync(string mode, string workspaceRoot, string trustRoot, string markerPath, string resultPath)
    {
        if (mode is not ("commit-receipt" or "crash-after-proof" or "crash-after-primary" or "crash-after-trust" or "crash-after-result"))
        {
            return 2;
        }

        var paths = new WorkspacePaths(workspaceRoot);
        var trust = new FileCapabilityCatalogTrustProvider(trustRoot);
        var receipt = CreateReceipt();
        var mutation = new AuthorityGrantStoreMutation(1, null, receipt);
        if (mode is "crash-after-proof" or "crash-after-primary")
        {
            var barrier = new AuthorityGrantCrashDurabilityBarrier(markerPath, mode == "crash-after-proof" ? 1 : 2);
            _ = await new AuthorityProfileStore(paths, trust, new FixedAuthorityGrantTimeProvider(_recordedAtUtc), barrier).CommitAsync(mutation);
            return 4;
        }

        ICapabilityCatalogTrustProvider effectiveTrust = mode == "crash-after-trust"
            ? new AuthorityGrantCrashAfterAdvanceTrustProvider(trust, markerPath)
            : trust;
        var result = await new AuthorityProfileStore(paths, effectiveTrust, new FixedAuthorityGrantTimeProvider(_recordedAtUtc)).CommitAsync(mutation);
        if (mode == "crash-after-result")
        {
            await File.WriteAllTextAsync(markerPath, result.Status.ToString());
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
        return 0;
    }

    private static AuthorityGrantOperationEvidence CreateReceipt()
    {
        if (!AuthorityGrantId.TryParse("cross-process-missing-grant", out var grantId, out _)
            || !AuthorityActorId.TryParse("user-owner", out var actorId, out _)
            || !AuthorityPurpose.TryParse("Delegate bounded work for one exact governed loop revision.", out var reason, out _))
        {
            throw new InvalidOperationException("The authority-grant host identifiers are invalid.");
        }

        return new AuthorityGrantOperationEvidence(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            "cross-process-receipt",
            new string('1', 64),
            AuthorityGrantOperationKind.Narrow,
            AuthorityGrantOperationOutcome.NotFound,
            AuthorityGrantOperationFailureCode.LifecycleConflict,
            grantId!,
            1,
            null,
            actorId!,
            reason!,
            new string('e', 64),
            null,
            _recordedAtUtc);
    }
}
