using System.Runtime.CompilerServices;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class CountingCredentialValueProvider(StrongBox<int> deleteCount, bool deleteSucceeds = true) : ICredentialValueProvider
{
    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));

    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
    {
        deleteCount.Value++;
        return ValueTask.FromResult(deleteSucceeds ? CredentialProviderResult.Success() : CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.OutcomeUncertain)));
    }

    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderHealthResult.Missing());
}
