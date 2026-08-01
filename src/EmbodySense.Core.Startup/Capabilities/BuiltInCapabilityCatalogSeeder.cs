using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Seeds shipped implementations through the catalog contracts without ambient enablement, assignment, trust, or authority.</summary>
public sealed class BuiltInCapabilityCatalogSeeder
{
    private const int MaximumConvergenceAttempts = 8;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;

    /// <summary>Creates a seeder using the default server-owned trust provider.</summary>
    public BuiltInCapabilityCatalogSeeder() : this(FileCapabilityCatalogTrustProvider.CreateDefault())
    {
    }

    /// <summary>Creates a seeder over an explicit server-owned trust provider.</summary>
    public BuiltInCapabilityCatalogSeeder(ICapabilityCatalogTrustProvider trustProvider)
    {
        ArgumentNullException.ThrowIfNull(trustProvider);
        _trustProvider = trustProvider;
    }

    /// <summary>Idempotently declares and installs every shipped capability.</summary>
    /// <param name="paths">The target workspace paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after all exact built-in descriptors are present.</returns>
    /// <exception cref="InvalidOperationException">The catalog is unavailable, conflicting, or contains a mismatched built-in declaration.</exception>
    public async Task SeedAsync(WorkspacePaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var service = new CapabilityCatalogService(new CapabilityCatalogStore(paths, _trustProvider));
        foreach (var descriptor in BuiltInCapabilityCatalog.Descriptors)
        {
            await SeedDescriptorAsync(service, descriptor, cancellationToken);
        }
    }

    private static async Task SeedDescriptorAsync(CapabilityCatalogService service, CapabilityDescriptor descriptor, CancellationToken cancellationToken)
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
                existing = declared.Entry;
                catalogRevision = declared.CatalogRevision!.Value;
            }
            else if (!CapabilityDescriptorHash.TryCompute(existing.Descriptor, out var existingHash, out _) || !CapabilityDescriptorHash.TryCompute(descriptor, out var expectedHash, out _) || !existingHash!.Equals(expectedHash))
            {
                throw new InvalidOperationException($"Built-in capability `{descriptor.Id.Value}` conflicts with the retained descriptor.");
            }

            if (existing!.Lifecycle.Retirement == CapabilityRetirementState.Removed)
            {
                throw new InvalidOperationException($"Built-in capability `{descriptor.Id.Value}` is retained as removed and cannot be resurrected automatically.");
            }

            if (existing.Lifecycle.Installation != CapabilityInstallationState.Installed)
            {
                var installed = await service.InstallAsync(descriptor.Id, catalogRevision, OperationId("install", descriptor.Id), cancellationToken);
                if (installed.Status == CapabilityCatalogMutationStatus.Conflict)
                {
                    continue;
                }

                RequireCommitted(installed, descriptor.Id);
            }

            return;
        }

        throw new InvalidOperationException($"Built-in capability `{descriptor.Id.Value}` did not converge after bounded optimistic retries.");
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

    private static void RequireCommitted(CapabilityCatalogMutationResult result, CapabilityId id)
    {
        if (result.Status is not CapabilityCatalogMutationStatus.Applied and not CapabilityCatalogMutationStatus.NoChange and not CapabilityCatalogMutationStatus.Replayed)
        {
            throw new InvalidOperationException($"Built-in capability `{id.Value}` seeding failed with `{result.Status}`.");
        }
    }
}
