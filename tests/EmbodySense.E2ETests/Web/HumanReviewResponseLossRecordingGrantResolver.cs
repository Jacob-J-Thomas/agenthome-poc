using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.E2ETests.Web;

internal sealed class HumanReviewResponseLossRecordingGrantResolver(IAuthorityGrantResolver inner) : IAuthorityGrantResolver
{
    public AuthorityGrantResolution? Resolution { get; private set; }

    public string? ExceptionType { get; private set; }

    public async Task<AuthorityGrantResolution> ResolveAsync(AuthorityGrantReference? reference, CancellationToken cancellationToken = default)
    {
        try
        {
            var resolution = await inner.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            Resolution = resolution;
            return resolution;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ExceptionType = exception.GetType().Name;
            throw;
        }
    }
}
