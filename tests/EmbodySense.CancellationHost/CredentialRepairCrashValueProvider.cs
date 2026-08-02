using System.Text;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

internal sealed class CredentialRepairCrashValueProvider(bool markProviderSuccess, string providerSuccessMarker) : ICredentialValueProvider
{
    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
    {
        if (markProviderSuccess)
        {
            using var marker = new FileStream(providerSuccessMarker, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            marker.Write(Encoding.UTF8.GetBytes(request.OperationId.Value));
            marker.Flush(flushToDisk: true);
        }

        Environment.FailFast(markProviderSuccess ? "Injected crash after durable provider success." : "Injected crash after durable repair intent.");
        return ValueTask.FromResult(CredentialProviderResult.Success());
    }

    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderHealthResult.Missing());
}
