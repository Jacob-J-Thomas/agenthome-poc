using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class CoordinatedPagedCapabilityCatalogStore(IReadOnlyList<CapabilityCatalogEntry> entries) : ICapabilityCatalogStore
{
    private IReadOnlyList<CapabilityCatalogEntry> _entries = entries;
    private int _blockFirstPage = 1;

    internal TaskCompletionSource FirstPageCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseFirstPage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Replace(CapabilityCatalogEntry replacement)
    {
        _entries = _entries.Select(entry => entry.Descriptor.Id.Equals(replacement.Descriptor.Id) ? replacement : entry).ToArray();
    }

    public async Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = _entries.OrderBy(entry => entry.Descriptor.Id.Value, StringComparer.Ordinal).ToArray();
        var pageEntries = ordered.Where(entry => startAfterId is null || string.CompareOrdinal(entry.Descriptor.Id.Value, startAfterId) > 0).Take(maximumCount).ToArray();
        var nextCursor = pageEntries.Length == maximumCount && string.CompareOrdinal(pageEntries[^1].Descriptor.Id.Value, ordered[^1].Descriptor.Id.Value) < 0 ? pageEntries[^1].Descriptor.Id.Value : null;
        if (startAfterId is null && Interlocked.Exchange(ref _blockFirstPage, 0) == 1)
        {
            FirstPageCaptured.TrySetResult();
            await ReleaseFirstPage.Task.WaitAsync(cancellationToken);
        }

        return new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(7, pageEntries, nextCursor), "Coordinated paged catalog read.");
    }

    public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
