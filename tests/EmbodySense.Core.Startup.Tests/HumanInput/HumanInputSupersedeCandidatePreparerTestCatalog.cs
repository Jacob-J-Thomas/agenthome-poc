using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputSupersedeCandidatePreparerTestCatalog : IHumanInputRequestCatalog
{
    internal HumanInputRequestCatalogReadResult? ReadResponse { get; set; } = new(HumanInputRequestCatalogReadStatus.NotFound, 0, null);

    internal Exception? ReadException { get; set; }

    internal bool DelayReadUntilCancellation { get; set; }

    internal TaskCompletionSource<bool>? ReadEntered { get; set; }

    internal int ReadCount { get; private set; }

    public Task<HumanInputRequestCatalogPage> ListAsync(HumanInputRequestCatalogPageRequest? request, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanInputRequestCatalogPage(HumanInputRequestCatalogPageStatus.Ready, 0, [], null));

    public async Task<HumanInputRequestCatalogReadResult> ReadAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        ReadEntered?.TrySetResult(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (DelayReadUntilCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (ReadException is not null)
        {
            throw ReadException;
        }

        return ReadResponse!;
    }
}
