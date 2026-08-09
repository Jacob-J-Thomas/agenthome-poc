using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;

namespace EmbodySense.IntegrationTests.Architecture;

internal sealed class CountingCredentialValueProvider : ICredentialValueProvider
{
    internal int DeleteCount { get; private set; }

    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Success());
    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Success());
    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Success());

    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
    {
        DeleteCount++;
        return ValueTask.FromResult(CredentialProviderResult.Success());
    }

    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderHealthResult.Available());
}
