using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;

namespace EmbodySense.Core.Startup.Tests.HumanInput;

internal sealed class HumanInputSupersedeCandidatePreparerTestCatalog : IHumanInputRequestCatalog
{
    internal HumanInputRequestCatalogReadResult? ReadResponse { get; set; } = new(HumanInputRequestCatalogReadStatus.NotFound, 0, null);

    internal Exception? ReadException { get; set; }

    public Task<HumanInputRequestCatalogPage> ListAsync(HumanInputRequestCatalogPageRequest? request, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanInputRequestCatalogPage(HumanInputRequestCatalogPageStatus.Ready, 0, [], null));

    public Task<HumanInputRequestCatalogReadResult> ReadAsync(string requestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadException is not null)
        {
            throw ReadException;
        }

        return Task.FromResult(ReadResponse!);
    }
}
