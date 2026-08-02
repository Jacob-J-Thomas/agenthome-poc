using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityPostureCatalogStore : ICapabilityCatalogStore
{
    internal CapabilityCatalogReadStatus Status { get; set; } = CapabilityCatalogReadStatus.Available;
    internal IReadOnlyList<CapabilityCatalogEntry> Entries { get; set; } = [];
    internal long Revision { get; set; } = 7;
    internal Exception? ReadException { get; set; }
    internal Queue<CapabilityCatalogReadResult> ReadResults { get; } = [];
    internal int ReadCount { get; private set; }
    internal int MutationCount { get; private set; }

    public Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        if (ReadException is not null)
        {
            throw ReadException;
        }
        if (ReadResults.TryDequeue(out var queued))
        {
            return Task.FromResult(queued);
        }
        if (Status == CapabilityCatalogReadStatus.Unavailable)
        {
            return Task.FromResult(new CapabilityCatalogReadResult(Status, null, "unavailable"));
        }

        var ordered = Entries.OrderBy(item => item.Descriptor.Id.Value, StringComparer.Ordinal).ToArray();
        var remaining = startAfterId is null ? ordered : ordered.Where(item => string.Compare(item.Descriptor.Id.Value, startAfterId, StringComparison.Ordinal) > 0).ToArray();
        var pageEntries = remaining.Take(maximumCount).ToArray();
        var nextCursor = remaining.Length > maximumCount ? pageEntries[^1].Descriptor.Id.Value : null;
        return Task.FromResult(new CapabilityCatalogReadResult(Status, new CapabilityCatalogPage(Revision, pageEntries, nextCursor), "available"));
    }

    public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default)
    {
        MutationCount++;
        throw new NotSupportedException("Posture tests must never mutate the catalog.");
    }
}
