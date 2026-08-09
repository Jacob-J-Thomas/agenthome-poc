using System.Text;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.CancellationHost.Credentials;

internal sealed class CredentialRepairCrashValueProvider(bool markProviderSuccess, string providerEntryMarker, string providerSuccessMarker) : ICredentialValueProvider
{
    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public async ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
    {
        WriteMarker(providerEntryMarker, request.OperationId.Value);
        if (markProviderSuccess)
        {
            WriteMarker(providerSuccessMarker, request.OperationId.Value);
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return CredentialProviderResult.Success();
    }

    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderHealthResult.Missing());

    private static void WriteMarker(string path, string value)
    {
        using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        marker.Write(Encoding.UTF8.GetBytes(value));
        marker.Flush(flushToDisk: true);
    }
}
