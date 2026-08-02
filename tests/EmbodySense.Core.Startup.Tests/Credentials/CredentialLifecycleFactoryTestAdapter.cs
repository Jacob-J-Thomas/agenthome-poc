using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Startup.Tests.Credentials;

internal sealed class CredentialLifecycleFactoryTestAdapter : ICredentialProviderLocatorVerifier, ICredentialValueProvider, ICredentialProviderLocatorSource, ICapabilityDependentIndex, ICredentialActiveRunIndex
{
    internal int DeleteCount { get; private set; }

    public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(InvalidProviderResult());

    public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(InvalidProviderResult());

    public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken) => ValueTask.FromResult(InvalidProviderResult());

    public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
    {
        DeleteCount++;
        return ValueTask.FromResult(CredentialProviderResult.Success());
    }

    public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderHealthResult.Missing());

    public ValueTask<CredentialProviderLocator?> CreateAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken) => ValueTask.FromResult<CredentialProviderLocator?>(null);

    public Task<CapabilityDependentIndexSnapshot> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, "sha256:" + new string('a', 64), [], "available"));

    public Task<IReadOnlyList<string>> CaptureAsync(CredentialCapabilityBinding binding, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    private static CredentialProviderResult InvalidProviderResult() => CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest));
}
