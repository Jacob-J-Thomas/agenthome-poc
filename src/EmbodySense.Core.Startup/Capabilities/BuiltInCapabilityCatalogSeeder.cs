using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Seeds and verifies shipped implementations without assigning them to a loop or granting authority.</summary>
public sealed class BuiltInCapabilityCatalogSeeder
{
    private const int MaximumConvergenceAttempts = 8;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly IBuiltInCapabilityCatalogSeedObserver? _observer;

    /// <summary>Creates a seeder using the default server-owned trust provider.</summary>
    public BuiltInCapabilityCatalogSeeder() : this(FileCapabilityCatalogTrustProvider.CreateDefault(), null)
    {
    }

    /// <summary>Creates a seeder over an explicit server-owned trust provider.</summary>
    public BuiltInCapabilityCatalogSeeder(ICapabilityCatalogTrustProvider trustProvider) : this(trustProvider, null)
    {
    }

    /// <summary>Creates a seeder over explicit server-owned trust and post-commit observation infrastructure.</summary>
    /// <param name="trustProvider">The server-owned catalog trust provider.</param>
    /// <param name="observer">The optional observer notified after each applied built-in bootstrap transition commits.</param>
    public BuiltInCapabilityCatalogSeeder(ICapabilityCatalogTrustProvider trustProvider, IBuiltInCapabilityCatalogSeedObserver? observer)
    {
        ArgumentNullException.ThrowIfNull(trustProvider);
        _trustProvider = trustProvider;
        _observer = observer;
    }

    /// <summary>Idempotently declares, installs, verifies, enables, and health-checks every exact shipped capability.</summary>
    /// <param name="paths">The target workspace paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after all exact built-in descriptors are present.</returns>
    /// <exception cref="InvalidOperationException">The catalog is unavailable, conflicting, or contains a mismatched built-in declaration.</exception>
    public async Task SeedAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var store = new CapabilityCatalogStore(paths, _trustProvider);
        var service = new CapabilityCatalogService(store);
        foreach (var descriptor in BuiltInCapabilityCatalog.Descriptors)
        {
            await SeedDescriptorAsync(service, store, descriptor, cancellationToken);
        }
    }

    private async Task SeedDescriptorAsync(CapabilityCatalogService service, CapabilityCatalogStore store, CapabilityDescriptor descriptor, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumConvergenceAttempts; attempt++)
        {
            var (existing, catalogRevision) = await FindAsync(service, descriptor.Id, cancellationToken);
            if (existing is null)
            {
                var declared = await service.DeclareAsync(descriptor, catalogRevision, OperationId("declare", descriptor.Id), cancellationToken);
                if (declared.Status == CapabilityCatalogMutationStatus.Conflict)
                {
                    continue;
                }

                RequireCommitted(declared, descriptor.Id);
                await ObserveAppliedAsync(declared, cancellationToken);
                continue;
            }
            else if (!CapabilityDescriptorHash.TryCompute(existing.Descriptor, out var existingHash, out _) || !CapabilityDescriptorHash.TryCompute(descriptor, out var expectedHash, out _) || !existingHash!.Equals(expectedHash))
            {
                throw new InvalidOperationException($"Built-in capability `{descriptor.Id.Value}` conflicts with the retained descriptor.");
            }

            if (existing!.Lifecycle.Retirement == CapabilityRetirementState.Removed)
            {
                throw new InvalidOperationException($"Built-in capability `{descriptor.Id.Value}` is retained as removed and cannot be resurrected automatically.");
            }

            var provedGeneration = await ProveInterruptedBootstrapAsync(store, descriptor, existing, cancellationToken);
            if (provedGeneration is null)
            {
                return;
            }

            if (existing.Lifecycle.Installation != CapabilityInstallationState.Installed)
            {
                var installed = await MutateBootstrapStageAsync(store, CapabilityCatalogMutationKind.Install, descriptor.Id, catalogRevision, provedGeneration.Value, OperationId("install", descriptor.Id), cancellationToken);
                if (installed.Status == CapabilityCatalogMutationStatus.Conflict)
                {
                    continue;
                }

                RequireCommitted(installed, descriptor.Id);
                await ObserveAppliedAsync(installed, cancellationToken);
                continue;
            }

            if (existing.Lifecycle.Trust != CapabilityTrustState.Verified)
            {
                var verified = await MutateBootstrapStageAsync(store, CapabilityCatalogMutationKind.Verify, descriptor.Id, catalogRevision, provedGeneration.Value, OperationId("verify", descriptor.Id), cancellationToken);
                if (verified.Status == CapabilityCatalogMutationStatus.Conflict)
                {
                    continue;
                }

                RequireCommitted(verified, descriptor.Id);
                await ObserveAppliedAsync(verified, cancellationToken);
                continue;
            }

            if (existing.Lifecycle.Enablement != CapabilityEnablementState.Enabled)
            {
                var enabled = await MutateBootstrapStageAsync(store, CapabilityCatalogMutationKind.Enable, descriptor.Id, catalogRevision, provedGeneration.Value, OperationId("enable", descriptor.Id), cancellationToken);
                if (enabled.Status == CapabilityCatalogMutationStatus.Conflict)
                {
                    continue;
                }

                RequireCommitted(enabled, descriptor.Id);
                await ObserveAppliedAsync(enabled, cancellationToken);
                continue;
            }

            if (existing.Lifecycle.Health != CapabilityHealthState.Healthy)
            {
                var healthy = await MutateBootstrapStageAsync(store, CapabilityCatalogMutationKind.MarkHealthy, descriptor.Id, catalogRevision, provedGeneration.Value, OperationId("healthy", descriptor.Id), cancellationToken);
                if (healthy.Status == CapabilityCatalogMutationStatus.Conflict)
                {
                    continue;
                }

                RequireCommitted(healthy, descriptor.Id);
                await ObserveAppliedAsync(healthy, cancellationToken);
                continue;
            }

            return;
        }

        throw new InvalidOperationException($"Built-in capability `{descriptor.Id.Value}` did not converge after bounded optimistic retries.");
    }

    private Task ObserveAppliedAsync(CapabilityCatalogMutationResult result, CancellationToken cancellationToken)
    {
        return result.Status == CapabilityCatalogMutationStatus.Applied && result.Entry is not null && _observer is not null
            ? _observer.TransitionCommittedAsync(result.Entry, cancellationToken)
            : Task.CompletedTask;
    }

    private static Task<CapabilityCatalogMutationResult> MutateBootstrapStageAsync(CapabilityCatalogStore store, CapabilityCatalogMutationKind kind, CapabilityId id, long expectedCatalogRevision, long expectedCatalogGeneration, string operationId, CancellationToken cancellationToken)
    {
        return store.MutateAtGenerationAsync(new CapabilityCatalogMutation(kind, operationId, expectedCatalogRevision, id, null), expectedCatalogGeneration, cancellationToken);
    }

    private static async Task<long?> ProveInterruptedBootstrapAsync(CapabilityCatalogStore store, CapabilityDescriptor descriptor, CapabilityCatalogEntry current, CancellationToken cancellationToken)
    {
        var read = await store.ReadOperationReceiptsAsync(descriptor.Id, cancellationToken);
        if (read.Status != CapabilityCatalogReadStatus.Available || read.CatalogGeneration is null)
        {
            throw new InvalidOperationException($"Built-in capability `{descriptor.Id.Value}` bootstrap ownership cannot be established without current proved operation receipts.");
        }

        var operationIds = BootstrapOperationIds(descriptor.Id);
        var receipts = read.Receipts.ToDictionary(receipt => receipt.OperationId, StringComparer.Ordinal);
        if (receipts.Count == 0 || receipts.Keys.Any(operationId => !operationIds.Contains(operationId, StringComparer.Ordinal)))
        {
            return null;
        }

        var lastStage = operationIds.TakeWhile(receipts.ContainsKey).Count() - 1;
        if (lastStage < 0 || receipts.Count != lastStage + 1 || !CapabilityDescriptorIdentity.TryCreate(descriptor, out var expectedIdentity, out _))
        {
            return null;
        }

        for (var stage = 0; stage <= lastStage; stage++)
        {
            var receipt = receipts[operationIds[stage]];
            if (receipt.Outcome != CapabilityCatalogMutationStatus.Applied || !IsExpectedBootstrapStage(receipt.Entry, expectedIdentity!, operationIds[stage], stage))
            {
                return null;
            }
        }

        var lastReceipt = receipts[operationIds[lastStage]];
        var currentMatchesProof = current.Revision == lastReceipt.Entry.Revision
            && current.UpdatedAtUtc == lastReceipt.Entry.UpdatedAtUtc
            && string.Equals(current.LastOperationId, lastReceipt.Entry.LastOperationId, StringComparison.Ordinal)
            && current.Lifecycle == lastReceipt.Entry.Lifecycle;
        return currentMatchesProof ? read.CatalogGeneration.Value : null;
    }

    private static bool IsExpectedBootstrapStage(CapabilityCatalogEntry entry, CapabilityDescriptorIdentity expectedIdentity, string operationId, int stage)
    {
        var lifecycle = entry.Lifecycle;
        return entry.Revision == stage + 1
            && string.Equals(entry.LastOperationId, operationId, StringComparison.Ordinal)
            && lifecycle.DescriptorIdentity.Equals(expectedIdentity)
            && lifecycle.Declaration == CapabilityDeclarationState.Declared
            && lifecycle.Installation == (stage >= 1 ? CapabilityInstallationState.Installed : CapabilityInstallationState.NotInstalled)
            && lifecycle.Trust == (stage >= 2 ? CapabilityTrustState.Verified : CapabilityTrustState.Unverified)
            && lifecycle.Enablement == (stage >= 3 ? CapabilityEnablementState.Enabled : CapabilityEnablementState.Disabled)
            && lifecycle.Health == (stage >= 4 ? CapabilityHealthState.Healthy : CapabilityHealthState.Unknown)
            && lifecycle.Retirement == CapabilityRetirementState.Active;
    }

    private static async Task<(CapabilityCatalogEntry? Entry, long CatalogRevision)> FindAsync(CapabilityCatalogService service, CapabilityId id, CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var read = await service.ReadAsync(cursor, CapabilityCatalogLimits.MaximumPageSize, cancellationToken);
            if (read.Status != CapabilityCatalogReadStatus.Available || read.Page is null)
            {
                throw new InvalidOperationException("Built-in capabilities cannot be seeded without the current proved catalog.");
            }

            var existing = read.Page.Entries.SingleOrDefault(entry => entry.Descriptor.Id.Equals(id));
            if (existing is not null || read.Page.NextCursor is null)
            {
                return (existing, read.Page.CatalogRevision);
            }

            cursor = read.Page.NextCursor;
        }
        while (cursor is not null);

        throw new InvalidOperationException("The bounded capability catalog query did not terminate.");
    }

    private static string OperationId(string action, CapabilityId id) => $"builtin-{action}-{id.Value.Replace('/', '-')}-v1";

    private static string[] BootstrapOperationIds(CapabilityId id) =>
    [
        OperationId("declare", id),
        OperationId("install", id),
        OperationId("verify", id),
        OperationId("enable", id),
        OperationId("healthy", id)
    ];

    private static void RequireCommitted(CapabilityCatalogMutationResult result, CapabilityId id)
    {
        if (result.Status is not CapabilityCatalogMutationStatus.Applied and not CapabilityCatalogMutationStatus.NoChange and not CapabilityCatalogMutationStatus.Replayed)
        {
            throw new InvalidOperationException($"Built-in capability `{id.Value}` seeding failed with `{result.Status}`.");
        }
    }
}
