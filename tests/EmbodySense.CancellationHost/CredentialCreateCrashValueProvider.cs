using System.Text;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

internal sealed class CredentialCreateCrashValueProvider(string providerEntryMarker) : ICredentialValueProvider
{
    public async ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        using var marker = new FileStream(providerEntryMarker, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        await marker.WriteAsync(Encoding.UTF8.GetBytes(request.OperationId.Value), cancellationToken);
        marker.Flush(flushToDisk: true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return CredentialProviderResult.Success();
    }

    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));
    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));
    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));
    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderHealthResult.Missing());
}
